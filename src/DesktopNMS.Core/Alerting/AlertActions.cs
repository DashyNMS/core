using System.Net;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>Acknowledging, or returning to active.</summary>
public enum AlertActionKind
{
    Acknowledge,
    Unacknowledge,
}

/// <summary>
/// Whether acknowledging or unmuting one fault would touch only that fault
/// (#6). LibreNMS 26.10 acts on one fault only while the rule notifies per
/// entity and the device's open faults are within its limit; otherwise on
/// every open fault for the device and rule.
/// </summary>
public sealed record AlertFaultScope(bool IsSingleFault, string Explanation)
{
    /// <summary>
    /// From the rule as just read and how many faults the device has open for it.
    /// An inherited limit can't be read through the API, so then a single fault
    /// is confirmed only when it's the only one open.
    /// </summary>
    public static AlertFaultScope For(AlertRule? rule, int openFaults)
    {
        if (openFaults <= 1)
        {
            return new(true, "This is the only open fault for this device and rule.");
        }

        if (rule is null)
        {
            return new(false, "The rule couldn't be read, so the scope can't be confirmed - LibreNMS may act on every open fault for this device and rule.");
        }

        if (rule.NotifyPerEntity != true)
        {
            return new(false, "This rule doesn't notify per entity, so LibreNMS acts on every open fault for this device and rule.");
        }

        if (rule.MaxEntities is not { } limit)
        {
            return new(false, "This rule's per-entity limit is the server default, which can't be read, so the scope can't be confirmed - LibreNMS may act on every open fault for this device and rule.");
        }

        return openFaults <= Math.Max(1, limit)
            ? new(true, $"This rule notifies per entity, and {openFaults} open faults is within its limit of {limit}.")
            : new(false, $"{openFaults} open faults is over this rule's per-entity limit of {limit}, so LibreNMS acts on every open fault for this device and rule.");
    }
}

/// <summary>What an acknowledge or unmute actually did, read back from LibreNMS afterwards.</summary>
public sealed class AlertActionResult
{
    public AlertActionResult(
        AlertActionKind kind,
        AlertSource source,
        IReadOnlyList<Alert> changed,
        IReadOnlyList<Alert> alreadyDone,
        IReadOnlyList<Alert> unchanged,
        IReadOnlyList<Alert> gone,
        string? error = null,
        AlertFaultScope? refusedScope = null)
    {
        Kind = kind;
        Source = source;
        Changed = changed;
        AlreadyDone = alreadyDone;
        Unchanged = unchanged;
        Gone = gone;
        Error = error;
        RefusedScope = refusedScope;
    }

    public AlertActionKind Kind { get; }

    public AlertSource Source { get; }

    /// <summary>Now in the asked-for state, and weren't before - what Undo should act on.</summary>
    public IReadOnlyList<Alert> Changed { get; }

    /// <summary>Already in that state before anything was sent.</summary>
    public IReadOnlyList<Alert> AlreadyDone { get; }

    /// <summary>Still not in that state after the request.</summary>
    public IReadOnlyList<Alert> Unchanged { get; }

    /// <summary>No longer open - recovered or removed while this ran.</summary>
    public IReadOnlyList<Alert> Gone { get; }

    /// <summary>Why a request failed, if one did.</summary>
    public string? Error { get; }

    /// <summary>
    /// Set when a single-fault action wasn't sent, because LibreNMS would
    /// act on more than that fault or that can't be ruled out. Offer the group action instead.
    /// </summary>
    public AlertFaultScope? RefusedScope { get; }

    public bool Succeeded => RefusedScope is null && Error is null && Unchanged.Count == 0 && (Changed.Count > 0 || AlreadyDone.Count > 0 || Gone.Count > 0);

    /// <summary>Only what was verified to have changed can be undone.</summary>
    public bool CanUndo => Changed.Count > 0;

    /// <summary>"Acknowledged 8 faults", "Acknowledged 3 faults; 1 couldn't be", "Already acknowledged".</summary>
    public string Summary
    {
        get
        {
            if (RefusedScope is not null)
            {
                return RefusedScope.Explanation;
            }

            var verb = Kind == AlertActionKind.Acknowledge ? "Acknowledged" : "Returned to active";
            var parts = new List<string>();
            if (Changed.Count > 0)
            {
                parts.Add($"{verb} {Count(Changed.Count)}");
            }

            if (Unchanged.Count > 0)
            {
                parts.Add($"{Count(Unchanged.Count)} couldn't be {(Kind == AlertActionKind.Acknowledge ? "acknowledged" : "returned to active")}");
            }

            if (Gone.Count > 0)
            {
                parts.Add($"{Count(Gone.Count)} no longer open");
            }

            if (parts.Count == 0)
            {
                parts.Add(AlreadyDone.Count > 0
                    ? Kind == AlertActionKind.Acknowledge ? "Already acknowledged" : "Already active"
                    : "Nothing to " + (Kind == AlertActionKind.Acknowledge ? "acknowledge" : "return to active"));
            }

            var summary = string.Join("; ", parts);
            return Error is null ? summary : $"{summary}. {Error}";
        }
    }

    private string Count(int count)
    {
        var noun = Source == AlertSource.Fault ? "fault" : "alert";
        return count == 1 ? $"1 {noun}" : $"{count} {noun}s";
    }
}

/// <summary>
/// Acknowledges and unmutes the way LibreNMS 26.10 actually applies them
/// (#6): by device and rule, refreshing after each write so a group that
/// LibreNMS updated in one go isn't written again (and its notes doubled),
/// and reporting only what was read back afterwards. On older servers a
/// group is one alert, and this does the same with one write.
/// </summary>
public sealed class AlertActions
{
    private static readonly IReadOnlyCollection<AlertState> OpenStates = new[] { AlertState.Active, AlertState.Acknowledged };

    private readonly IAlertsApi _alerts;
    private readonly IAlertRulesApi _rules;

    public AlertActions(IAlertsApi alerts, IAlertRulesApi rules)
    {
        _alerts = alerts;
        _rules = rules;
    }

    /// <summary>Acknowledges every open fault for a device and rule.</summary>
    public Task<AlertActionResult> AcknowledgeGroupAsync(AlertGroupKey group, string? note, bool untilClear = true, CancellationToken cancellationToken = default)
        => GroupAsync(AlertActionKind.Acknowledge, group, note, untilClear, cancellationToken);

    /// <summary>Returns every acknowledged fault for a device and rule to active.</summary>
    public Task<AlertActionResult> UnacknowledgeGroupAsync(AlertGroupKey group, string? note, CancellationToken cancellationToken = default)
        => GroupAsync(AlertActionKind.Unacknowledge, group, note, untilClear: false, cancellationToken);

    /// <summary>
    /// Whether one fault can be acted on alone, from the rule and open faults
    /// as they are now. Settings can still change before a write, so a
    /// single-fault action reports what actually changed.
    /// </summary>
    public async Task<AlertFaultScope> GetFaultScopeAsync(Alert fault, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fault);

        var open = await ReadGroupAsync(fault.Group(), cancellationToken).ConfigureAwait(false);
        return await ScopeAsync(fault, open.Count, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Acknowledges one fault - only when <see cref="GetFaultScopeAsync"/>
    /// confirms LibreNMS will touch just that one; otherwise nothing is sent
    /// and <see cref="AlertActionResult.RefusedScope"/> says why.
    /// </summary>
    public Task<AlertActionResult> AcknowledgeFaultAsync(Alert fault, string? note, bool untilClear = true, CancellationToken cancellationToken = default)
        => FaultAsync(AlertActionKind.Acknowledge, fault, note, untilClear, cancellationToken);

    /// <summary>Returns one fault to active, on the same terms as <see cref="AcknowledgeFaultAsync"/>.</summary>
    public Task<AlertActionResult> UnacknowledgeFaultAsync(Alert fault, string? note, CancellationToken cancellationToken = default)
        => FaultAsync(AlertActionKind.Unacknowledge, fault, note, untilClear: false, cancellationToken);

    private async Task<AlertActionResult> FaultAsync(AlertActionKind kind, Alert fault, string? note, bool untilClear, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fault);

        var group = fault.Group();
        var before = await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false);
        var source = SourceOf(before, fault.Source);

        var scope = await ScopeAsync(fault, before.Count, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSingleFault)
        {
            return new AlertActionResult(kind, source, Array.Empty<Alert>(), Array.Empty<Alert>(), Array.Empty<Alert>(), Array.Empty<Alert>(), refusedScope: scope);
        }

        var target = before.FirstOrDefault(a => a.Identity == fault.Identity);
        if (target is null)
        {
            return new AlertActionResult(kind, source, Array.Empty<Alert>(), Array.Empty<Alert>(), Array.Empty<Alert>(), new[] { fault });
        }

        if (IsDone(kind, target))
        {
            return new AlertActionResult(kind, source, Array.Empty<Alert>(), new[] { target }, Array.Empty<Alert>(), Array.Empty<Alert>());
        }

        var error = await WriteAsync(kind, target, note, untilClear, cancellationToken).ConfigureAwait(false);

        // Report the whole group: if settings changed since the check, LibreNMS may have acted on more than asked.
        var after = await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false);
        return Result(kind, source, before.Where(a => !IsDone(kind, a)).ToList(), before.Where(a => IsDone(kind, a)).ToList(), after, error);
    }

    private async Task<AlertActionResult> GroupAsync(AlertActionKind kind, AlertGroupKey group, string? note, bool untilClear, CancellationToken cancellationToken)
    {
        var before = await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false);
        var source = SourceOf(before, AlertSource.Alert);
        var pending = before.Where(a => !IsDone(kind, a)).ToList();
        var alreadyDone = before.Where(a => IsDone(kind, a)).ToList();

        if (pending.Count == 0)
        {
            return Result(kind, source, pending, alreadyDone, before, error: null);
        }

        // One write, then look: without per-entity behaviour LibreNMS has
        // updated the whole group, and writing again would only add notes.
        string? error = await WriteAsync(kind, pending[0], note, untilClear, cancellationToken).ConfigureAwait(false);
        var after = await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false);

        var attempted = new HashSet<AlertKey> { pending[0].Identity };
        var remaining = StillPending(kind, pending, after, attempted);
        if (remaining.Count > 0 && error is null)
        {
            // Per entity: one write each for the rest, then a final look.
            foreach (var alert in remaining)
            {
                attempted.Add(alert.Identity);
                error = await WriteAsync(kind, alert, note, untilClear, cancellationToken).ConfigureAwait(false) ?? error;
            }

            after = await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false);
        }

        return Result(kind, source, pending, alreadyDone, after, error);
    }

    /// <summary>Faults still needing the action that haven't been written to yet.</summary>
    private static List<Alert> StillPending(AlertActionKind kind, IReadOnlyList<Alert> pending, IReadOnlyList<Alert> after, HashSet<AlertKey> attempted)
    {
        var now = after.ToDictionary(a => a.Identity);
        return pending
            .Where(a => !attempted.Contains(a.Identity) && now.TryGetValue(a.Identity, out var current) && !IsDone(kind, current))
            .ToList();
    }

    private static AlertActionResult Result(AlertActionKind kind, AlertSource source, IReadOnlyList<Alert> pending, IReadOnlyList<Alert> alreadyDone, IReadOnlyList<Alert> after, string? error)
    {
        var now = after.ToDictionary(a => a.Identity);
        var changed = new List<Alert>();
        var unchanged = new List<Alert>();
        var gone = new List<Alert>();

        foreach (var alert in pending)
        {
            if (!now.TryGetValue(alert.Identity, out var current))
            {
                gone.Add(alert);
            }
            else if (IsDone(kind, current))
            {
                changed.Add(current);
            }
            else
            {
                unchanged.Add(current);
            }
        }

        return new AlertActionResult(kind, source, changed, alreadyDone, unchanged, gone, error);
    }

    /// <summary>Sends one write; a failure is returned to report, not thrown, so what did change is still read back.</summary>
    private async Task<string?> WriteAsync(AlertActionKind kind, Alert alert, string? note, bool untilClear, CancellationToken cancellationToken)
    {
        try
        {
            if (kind == AlertActionKind.Acknowledge)
            {
                await _alerts.AcknowledgeAsync(alert.Id, note, untilClear, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _alerts.UnmuteAsync(alert.Id, note, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        catch (LibreNmsApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Gone, or nothing left for LibreNMS to act on - the read-back says which.
            return null;
        }
        catch (LibreNmsApiException ex)
        {
            return ex.ToUserMessage();
        }
    }

    private async Task<IReadOnlyList<Alert>> ReadGroupAsync(AlertGroupKey group, CancellationToken cancellationToken)
    {
        var rows = await _alerts
            .ListAsync(new AlertQuery { RuleId = group.RuleId, States = OpenStates }, cancellationToken)
            .ConfigureAwait(false);
        return rows.Where(a => a.DeviceId == group.DeviceId).ToList();
    }

    private async Task<AlertFaultScope> ScopeAsync(Alert fault, int openFaults, CancellationToken cancellationToken)
    {
        if (openFaults <= 1)
        {
            return AlertFaultScope.For(null, openFaults);
        }

        AlertRule? rule;
        try
        {
            rule = await _rules.GetAsync(fault.RuleId, cancellationToken).ConfigureAwait(false);
        }
        catch (LibreNmsApiException)
        {
            rule = null;
        }

        return AlertFaultScope.For(rule, openFaults);
    }

    private static bool IsDone(AlertActionKind kind, Alert alert)
        => kind == AlertActionKind.Acknowledge ? alert.State == AlertState.Acknowledged : alert.State != AlertState.Acknowledged;

    private static AlertSource SourceOf(IReadOnlyList<Alert> rows, AlertSource fallback)
        => rows.Count > 0 ? rows[0].Source : fallback;
}

internal static class AlertGroupKeyExtensions
{
    public static AlertGroupKey Group(this Alert alert) => new(alert.DeviceId, alert.RuleId);
}
