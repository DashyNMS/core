using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>
/// Which details belong to an alert or fault (#7). On LibreNMS 26.10 a fault
/// carries its own; its log entries are the ones with its <c>fault_id</c>.
/// Picking the latest log for the device and rule instead would show
/// another core's details when eight are faulting.
/// </summary>
public static class AlertFaultDetails
{
    /// <summary>
    /// A fault's details from its own payload, or null when it has none - a
    /// legacy alert, whose details are only in the alert log.
    /// </summary>
    public static AlertDetail? Current(Alert alert, IReadOnlySet<string>? ruleFields = null)
    {
        ArgumentNullException.ThrowIfNull(alert);

        return alert.Details is { ValueKind: System.Text.Json.JsonValueKind.Object }
            ? AlertFaultParser.Parse(alert.Details, alert.Timestamp ?? alert.LastSeen, ruleFields)
            : null;
    }

    /// <summary>
    /// The newest log entry for this alert or fault: one with its
    /// <c>fault_id</c> for a fault, or for a legacy alert the newest for its
    /// device and rule. A fault falls back to a device/rule entry only when
    /// it's about the whole device (carried over from a pre-26.10 alert) and
    /// the entry has no <c>fault_id</c> either - never another fault's.
    /// </summary>
    public static AlertLogEntry? LatestLog(IEnumerable<AlertLogEntry> entries, Alert alert)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(alert);

        var forRule = entries.Where(e => e.RuleId == alert.RuleId && e.DeviceId == alert.DeviceId).ToList();

        if (alert.Source == AlertSource.Fault)
        {
            var own = forRule.Where(e => e.FaultId == alert.Id).MaxBy(e => e.Id);
            if (own is not null || !string.IsNullOrEmpty(alert.EntityKey))
            {
                return own;
            }

            return forRule.Where(e => e.FaultId is null && IsFiringOrRecovery(e)).MaxBy(e => e.Id);
        }

        return forRule.MaxBy(e => e.Id);
    }

    /// <summary>Every log entry about this fault, newest first - its own history, not the group's.</summary>
    public static IReadOnlyList<AlertLogEntry> History(IEnumerable<AlertLogEntry> entries, Alert alert)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(alert);

        return entries
            .Where(e => e.RuleId == alert.RuleId && e.DeviceId == alert.DeviceId
                && (alert.Source == AlertSource.Alert || e.FaultId == alert.Id))
            .OrderByDescending(e => e.Id)
            .ToList();
    }

    /// <summary>
    /// The details to show for an alert or fault: its own payload when it
    /// has one, otherwise from <see cref="LatestLog"/>.
    /// </summary>
    public static AlertDetail Resolve(Alert alert, IEnumerable<AlertLogEntry> entries, IReadOnlySet<string>? ruleFields = null)
        => Current(alert, ruleFields) ?? AlertFaultParser.Parse(LatestLog(entries, alert), ruleFields);

    // A legacy row recording a firing or a recovery, not a 26.10 rule-level worse/better/changed row listing every fault.
    private static bool IsFiringOrRecovery(AlertLogEntry entry)
        => entry.StateValue is (int)AlertState.Active or (int)AlertState.Recovered or (int)AlertState.Acknowledged;
}

/// <summary>Alert log lookups for one alert or fault (#7).</summary>
public static class AlertLogLookups
{
    /// <summary>
    /// The newest log entry for <paramref name="alert"/> - see
    /// <see cref="AlertFaultDetails.LatestLog"/>. Use this rather than
    /// <see cref="ILogsApi.GetLatestForRuleAsync"/>, which can't tell one
    /// fault of a rule from another.
    /// </summary>
    public static async Task<AlertLogEntry?> GetLatestForAlertAsync(
        this ILogsApi logs,
        Alert alert,
        int searchDepth = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(alert);

        var entries = await logs.ListAlertLogAsync(alert.DeviceId, searchDepth, cancellationToken).ConfigureAwait(false);
        return AlertFaultDetails.LatestLog(entries, alert);
    }
}
