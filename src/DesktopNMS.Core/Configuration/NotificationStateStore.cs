using System.Text.Json;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Configuration;

/// <summary>
/// Remembers what the last poll saw, so restarting DashyNMS does not
/// re-announce everything that is outstanding - see <see cref="AlertWatchState"/>.
/// </summary>
public interface INotificationStateStore
{
    AlertWatchState Load();

    void Save(AlertWatchState state);
}

public sealed class NotificationStateStore : INotificationStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<NotificationStateStore> _logger;

    public NotificationStateStore(ILogger<NotificationStateStore> logger)
    {
        _logger = logger;
    }

    public AlertWatchState Load()
    {
        try
        {
            return File.Exists(AppPaths.NotificationStateFile)
                ? Read(File.ReadAllText(AppPaths.NotificationStateFile))
                : AlertWatchState.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read notification state; starting from empty");
            return AlertWatchState.Empty;
        }
    }

    public void Save(AlertWatchState state)
    {
        try
        {
            var temp = AppPaths.NotificationStateFile + ".tmp";
            File.WriteAllText(temp, Write(state, DateTime.UtcNow));
            File.Move(temp, AppPaths.NotificationStateFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write notification state");
        }
    }

    /// <summary>
    /// Reads notified.json. A file from before #5 - bare alert ids, no
    /// version - can't be compared safely, so it reads as
    /// <see cref="AlertWatchState.Outdated"/> when it held anything.
    /// </summary>
    internal static AlertWatchState Read(string json)
    {
        var state = JsonSerializer.Deserialize<PersistedState>(json, SerializerOptions);
        if (state is null)
        {
            return AlertWatchState.Empty;
        }

        if (state.Version < AlertWatchState.CurrentVersion)
        {
            return state.Alerts is { Count: > 0 } ? AlertWatchState.Outdated : AlertWatchState.Empty;
        }

        var entries = new Dictionary<AlertKey, AlertWatchEntry>();
        foreach (var (text, entry) in state.Entries ?? new Dictionary<string, PersistedEntry>())
        {
            if (AlertKey.TryParse(text, out var key))
            {
                entries[key] = new AlertWatchEntry(entry.Device, entry.Rule, entry.State, entry.ClearedAtUtc);
            }
        }

        AlertSource? source = state.Source switch
        {
            "fault" => AlertSource.Fault,
            "alert" => AlertSource.Alert,
            _ => null,
        };

        return new AlertWatchState(source, entries);
    }

    internal static string Write(AlertWatchState state, DateTime savedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);

        var persisted = new PersistedState
        {
            Version = AlertWatchState.CurrentVersion,
            SavedAtUtc = savedAtUtc,
            Source = state.Source switch
            {
                AlertSource.Fault => "fault",
                AlertSource.Alert => "alert",
                _ => null,
            },
            Entries = state.Entries.ToDictionary(
                pair => pair.Key.ToString(),
                pair => new PersistedEntry
                {
                    Device = pair.Value.DeviceId,
                    Rule = pair.Value.RuleId,
                    State = pair.Value.State,
                    ClearedAtUtc = pair.Value.ClearedAtUtc,
                }),
        };

        return JsonSerializer.Serialize(persisted, SerializerOptions);
    }

    private sealed class PersistedState
    {
        /// <summary>Missing (0) in files from before #5.</summary>
        public int Version { get; set; }

        public DateTime SavedAtUtc { get; set; }

        /// <summary>"fault" or "alert".</summary>
        public string? Source { get; set; }

        /// <summary><see cref="AlertKey"/> as text ("fault:12") to what was seen.</summary>
        public Dictionary<string, PersistedEntry>? Entries { get; set; }

        /// <summary>Before #5: alert id to state value.</summary>
        public Dictionary<string, int>? Alerts { get; set; }
    }

    private sealed class PersistedEntry
    {
        public int Device { get; set; }

        public int Rule { get; set; }

        public int State { get; set; }

        public DateTime? ClearedAtUtc { get; set; }
    }
}
