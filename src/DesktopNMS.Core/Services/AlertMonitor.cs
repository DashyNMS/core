using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Services;

/// <summary>Outcome of one polling cycle.</summary>
public sealed class AlertPollResult
{
    public required bool Succeeded { get; init; }

    public required IReadOnlyList<Alert> Alerts { get; init; }

    public required IReadOnlyList<AlertChange> Changes { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>True for the first successful poll of this session.</summary>
    public required bool IsFirstPoll { get; init; }

    /// <summary>
    /// This poll only recorded what's open (#5) - after the server switched
    /// between alerts and faults, or with state saved by an older DashyNMS.
    /// <see cref="Changes"/> is empty, and any notification still showing
    /// from before should be withdrawn: its ids may now mean something else.
    /// </summary>
    public bool IsBaseline { get; init; }

    /// <summary>Whether <see cref="Alerts"/> are faults (LibreNMS 26.10+) or alerts.</summary>
    public AlertSource Source { get; init; }

    public string? ErrorMessage { get; init; }

    public bool IsAuthenticationFailure { get; init; }

    public static AlertPollResult Failed(string message, bool authenticationFailure = false) => new()
    {
        Succeeded = false,
        Alerts = Array.Empty<Alert>(),
        Changes = Array.Empty<AlertChange>(),
        CompletedAt = DateTimeOffset.Now,
        IsFirstPoll = false,
        ErrorMessage = message,
        IsAuthenticationFailure = authenticationFailure,
    };
}

/// <summary>
/// Polls LibreNMS on a timer and reports what changed.
/// </summary>
/// <remarks>
/// Runs on a background task rather than a DispatcherTimer so a slow or hung
/// server cannot freeze the UI, and so it keeps working while the window is
/// hidden in the notification area.
/// </remarks>
public sealed class AlertMonitor : IDisposable
{
    private readonly ILibreNmsClient _client;
    private readonly IConnectionState _session;
    private readonly ISettingsStore _settings;
    private readonly INotificationStateStore _notificationState;
    private readonly IDeviceCache _devices;
    private readonly ILogger<AlertMonitor> _logger;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private AlertWatchState _previous = AlertWatchState.Empty;
    private bool _hasCompletedFirstPoll;
    private bool _disposed;

    public AlertMonitor(
        ILibreNmsClient client,
        IConnectionState session,
        ISettingsStore settings,
        INotificationStateStore notificationState,
        IDeviceCache devices,
        ILogger<AlertMonitor> logger)
    {
        _client = client;
        _session = session;
        _settings = settings;
        _notificationState = notificationState;
        _devices = devices;
        _logger = logger;
    }

    /// <summary>Raised on a background thread after every polling cycle.</summary>
    public event EventHandler<AlertPollResult>? Polled;

    /// <summary>Raised when a poll starts, so the UI can show a busy indicator.</summary>
    public event EventHandler? PollStarted;

    public bool IsRunning => _loop is { IsCompleted: false };

    public DateTimeOffset? LastSuccessfulPoll { get; private set; }

    /// <summary>
    /// When this monitor's loop began. Since it starts immediately on sign-in
    /// (before any tab has been opened), <see cref="SensorMonitor"/> and
    /// <see cref="DeviceMonitor"/> - both started lazily, whenever their own
    /// tab first shows - align their own recurring tick to this timestamp, so
    /// every tab's countdown reaches zero and refreshes at the same moment
    /// instead of drifting apart based on when each tab happened to open.
    /// </summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>
    /// Whole seconds until this monitor's next tick, for the Alerts tab's own
    /// countdown display. Always recomputed fresh from wall-clock time - see
    /// <see cref="PollAlignment"/> - so it stays in step with the Health/
    /// Devices/Dashboard tabs' countdowns, all aligned to this same schedule.
    /// </summary>
    public int SecondsUntilNextPoll() => PollAlignment.GetSecondsRemaining(StartedAt, _settings.Current.PollIntervalSeconds);

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        StartedAt = DateTimeOffset.UtcNow;
        _previous = _notificationState.Load();
        _hasCompletedFirstPoll = false;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);

        _logger.LogInformation(
            "Alert monitor started; polling every {Interval}s",
            _settings.Current.PollIntervalSeconds);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;

        _cts = null;
        _loop = null;

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
        _logger.LogInformation("Alert monitor stopped");
    }

    /// <summary>Asks the loop to poll immediately rather than waiting for the timer.</summary>
    public void RequestRefresh()
    {
        // CurrentCount is a hint only, but the semaphore is capped at 1 so the
        // worst case is a redundant release being swallowed.
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
            // Refresh requested while shutting down.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);

            var interval = TimeSpan.FromSeconds(_settings.Current.PollIntervalSeconds);

            try
            {
                // Returns true when RequestRefresh signalled, false on timeout.
                await _wake.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!_session.IsConnected)
        {
            return;
        }

        // A manual refresh landing on top of a timer tick must not double-fetch.
        if (!await _pollGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            PollStarted?.Invoke(this, EventArgs.Empty);

            var query = _settings.Current.IncludeRecoveredAlerts ? AlertQuery.All : AlertQuery.Open;
            var alerts = await _client.Alerts.ListAsync(query, cancellationToken).ConfigureAwait(false);
            var source = await _client.Alerts.GetSourceAsync(cancellationToken).ConfigureAwait(false);

            // Device names for the list come from here, not from the alert rows,
            // so refresh before publishing the result. This never throws.
            await _devices
                .EnsureCurrentAsync(alerts.Select(a => a.DeviceId), cancellationToken)
                .ConfigureAwait(false);

            // Recoveries are looked up whatever the list shows (#5).
            var (recovered, unresolved) = await FindRecoveredAsync(alerts, query, source, cancellationToken).ConfigureAwait(false);
            var watched = recovered.Count == 0 ? alerts : alerts.Concat(recovered).ToList();

            var result = AlertGroupChangeDetector.Detect(_previous, source, watched, DateTime.UtcNow, unresolved);
            _previous = result.Next;
            _notificationState.Save(_previous);

            var isFirstPoll = !_hasCompletedFirstPoll;
            _hasCompletedFirstPoll = true;
            LastSuccessfulPoll = DateTimeOffset.Now;

            if (result.IsBaseline)
            {
                _logger.LogInformation("Recorded {Count} open {Source}s without notifying: {Reason}", alerts.Count, source, result.BaselineReason);
            }

            _logger.LogDebug(
                "Polled {Count} alerts, {Changes} change(s){First}",
                alerts.Count,
                result.Changes.Count,
                isFirstPoll ? " (first poll)" : string.Empty);

            Polled?.Invoke(this, new AlertPollResult
            {
                Succeeded = true,
                Alerts = alerts,
                Changes = result.Changes,
                CompletedAt = LastSuccessfulPoll.Value,
                IsFirstPoll = isFirstPoll,
                IsBaseline = result.IsBaseline,
                Source = source,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Poll failed");
            Polled?.Invoke(this, AlertPollResult.Failed(ex.ToUserMessage(), ex.IsAuthenticationFailure));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Poll failed unexpectedly");
            Polled?.Invoke(this, AlertPollResult.Failed(ex.Message));
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>
    /// Clears the "already notified" memory, so the next poll treats everything
    /// outstanding as new. Used after signing in to a different server.
    /// </summary>
    public void ResetHistory()
    {
        _previous = AlertWatchState.Empty;
        _hasCompletedFirstPoll = false;
        _notificationState.Save(_previous);
    }

    /// <summary>
    /// Looks up last poll's open alerts that this poll's list no longer has,
    /// so a recovery is seen even when recovered alerts aren't listed. A
    /// handful are fetched by id; a rule with many - a mass recovery - is
    /// fetched once, by rule. One that can't be looked up is unresolved and
    /// kept for the next poll; one that's gone is never taken as recovered.
    /// </summary>
    private async Task<(IReadOnlyList<Alert> Recovered, IReadOnlyCollection<AlertKey> Unresolved)> FindRecoveredAsync(
        IReadOnlyList<Alert> listed,
        AlertQuery query,
        AlertSource source,
        CancellationToken cancellationToken)
    {
        var none = (Array.Empty<Alert>(), (IReadOnlyCollection<AlertKey>)Array.Empty<AlertKey>());
        if (query.States?.Contains(AlertState.Recovered) == true || _previous.NeedsBaseline || _previous.Source != source)
        {
            // The list already says, or there's nothing comparable to look for.
            return none;
        }

        var seen = listed.Select(a => a.Identity).ToHashSet();
        var missing = _previous.Outstanding.Where(e => !seen.Contains(e.Key)).ToList();
        if (missing.Count == 0)
        {
            return none;
        }

        var recovered = new List<Alert>();
        var unresolved = new List<AlertKey>();

        foreach (var rule in missing.GroupBy(e => e.Value.RuleId))
        {
            var keys = rule.Select(e => e.Key).ToHashSet();
            try
            {
                IEnumerable<Alert> found;
                if (keys.Count <= LookupByIdLimit)
                {
                    var rows = new List<Alert>();
                    foreach (var key in keys)
                    {
                        rows.AddRange(await _client.Alerts
                            .ListAsync(new AlertQuery { Id = key.Id, States = AlertQuery.All.States, OrderBy = null }, cancellationToken)
                            .ConfigureAwait(false));
                    }

                    found = rows;
                }
                else
                {
                    found = await _client.Alerts
                        .ListAsync(new AlertQuery { RuleId = rule.Key, States = AlertQuery.All.States }, cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var row in found.Where(r => keys.Contains(r.Identity)))
                {
                    if (row.State == AlertState.Recovered)
                    {
                        recovered.Add(row);
                    }
                    else
                    {
                        // Open after all - the list and this lookup crossed. Ask again next poll.
                        unresolved.Add(row.Identity);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not check whether {Count} alert(s) for rule {Rule} recovered; checking again next poll", keys.Count, rule.Key);
                unresolved.AddRange(keys);
            }
        }

        return (recovered, unresolved);
    }

    /// <summary>Up to this many missing alerts for one rule are looked up one by one; more, and the rule's recoveries are listed instead.</summary>
    private const int LookupByIdLimit = 5;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _cts?.Dispose();
        _wake.Dispose();
        _pollGate.Dispose();
    }
}
