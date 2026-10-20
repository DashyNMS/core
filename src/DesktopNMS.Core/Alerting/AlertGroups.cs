using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>
/// A device and rule - what LibreNMS acknowledges together (#4, #6). Its own
/// type, so it can never be passed where a fault or alert id is wanted.
/// </summary>
public readonly record struct AlertGroupKey(int DeviceId, int RuleId)
{
    public override string ToString() => $"device:{DeviceId}/rule:{RuleId}";
}

/// <summary>
/// One rule firing on one device: every open fault LibreNMS 26.10 keeps for
/// it - one per matching core, port, sensor or location - or, on an older
/// server, the single alert row. Most groups hold one fault, and then read
/// exactly like that one alert.
/// </summary>
public sealed class AlertGroup
{
    internal AlertGroup(AlertGroupKey key, IReadOnlyList<Alert> faults)
    {
        Key = key;
        Faults = faults;
    }

    public AlertGroupKey Key { get; }

    public int DeviceId => Key.DeviceId;

    public int RuleId => Key.RuleId;

    /// <summary>Unacknowledged first, then by what they're about, then by id.</summary>
    public IReadOnlyList<Alert> Faults { get; }

    /// <summary>The fault to show for a group of one, or the group's first.</summary>
    public Alert Primary => Faults[0];

    public bool IsSingle => Faults.Count == 1;

    public int FaultCount => Faults.Count;

    public string DisplayHostname => Primary.DisplayHostname;

    public string DisplayRuleName => Primary.DisplayRuleName;

    /// <summary>The worst of its faults.</summary>
    public AlertSeverity Severity => Faults.Select(f => f.Severity).OrderByDescending(s => s.SortRank()).First();

    /// <summary>Faults still needing attention - firing, worsened or improved, but not acknowledged.</summary>
    public int UnacknowledgedCount => Faults.Count(IsUnacknowledged);

    public int AcknowledgedCount => Faults.Count(f => f.State == AlertState.Acknowledged);

    /// <summary>Active while any fault is unacknowledged; acknowledged once every fault is.</summary>
    public AlertState State => UnacknowledgedCount > 0 ? AlertState.Active
        : AcknowledgedCount == Faults.Count ? AlertState.Acknowledged
        : Primary.State;

    /// <summary>Some faults acknowledged and some not - worth saying, as acknowledging one may not have covered the rest.</summary>
    public bool IsPartlyAcknowledged => AcknowledgedCount > 0 && UnacknowledgedCount > 0;

    /// <summary>The earliest any of its faults was first seen (or last changed, where LibreNMS doesn't say).</summary>
    public DateTime? FirstSeen => Faults.Select(f => f.FirstSeen ?? f.Timestamp).Where(t => t is not null).Min();

    /// <summary>The latest any of its faults changed.</summary>
    public DateTime? LastChanged => Faults.Select(f => f.Timestamp ?? f.LastSeen).Where(t => t is not null).Max();

    internal static bool IsUnacknowledged(Alert fault)
        => fault.State is AlertState.Active or AlertState.Worse or AlertState.Better;

    public override string ToString() => $"{Key} {DisplayHostname}: {DisplayRuleName} [{FaultCount} fault(s), {State}]";
}

/// <summary>
/// One rule across every device it's firing on - the roll-up above
/// <see cref="AlertGroup"/> (#4): "Rule X · 384 devices". For showing only;
/// actions still go to a device's group or a single fault.
/// </summary>
public sealed class AlertRuleGroup
{
    internal AlertRuleGroup(int ruleId, IReadOnlyList<AlertGroup> groups)
    {
        RuleId = ruleId;
        Groups = groups;
    }

    public int RuleId { get; }

    /// <summary>One per device, in <see cref="AlertGrouping"/>'s order.</summary>
    public IReadOnlyList<AlertGroup> Groups { get; }

    public string DisplayRuleName => Groups[0].DisplayRuleName;

    public int DeviceCount => Groups.Count;

    public int FaultCount => Groups.Sum(g => g.FaultCount);

    public AlertSeverity Severity => Groups.Select(g => g.Severity).OrderByDescending(s => s.SortRank()).First();

    public bool IsSingle => Groups.Count == 1;

    /// <summary>The latest any of its devices' faults changed.</summary>
    public DateTime? LastChanged => Groups.Select(g => g.LastChanged).Where(t => t is not null).Max();

    public override string ToString() => $"rule:{RuleId} {DisplayRuleName} [{DeviceCount} device(s), {FaultCount} fault(s)]";
}

/// <summary>Groups alerts or faults by device and rule, and those by rule (#4).</summary>
public static class AlertGrouping
{
    /// <summary>
    /// One group per device and rule, worst first, then most recently
    /// changed, then by host name and ids - a stable order, so lists and
    /// paging don't shuffle between polls.
    /// </summary>
    public static IReadOnlyList<AlertGroup> ByDeviceAndRule(IEnumerable<Alert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);

        return alerts
            .GroupBy(a => new AlertGroupKey(a.DeviceId, a.RuleId))
            .Select(g => new AlertGroup(g.Key, g
                .OrderBy(f => AlertGroup.IsUnacknowledged(f) ? 0 : 1)
                .ThenBy(f => f.EntityKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Id)
                .ToList()))
            .OrderByDescending(g => g.Severity.SortRank())
            .ThenByDescending(g => g.LastChanged ?? DateTime.MinValue)
            .ThenBy(g => g.DisplayHostname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.DeviceId)
            .ThenBy(g => g.RuleId)
            .ToList();
    }

    /// <summary>The device/rule groups gathered by rule: worst first, then the most devices, then the most recently changed.</summary>
    public static IReadOnlyList<AlertRuleGroup> ByRule(IEnumerable<AlertGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        return groups
            .GroupBy(g => g.RuleId)
            .Select(g => new AlertRuleGroup(g.Key, g.ToList()))
            .OrderByDescending(r => r.Severity.SortRank())
            .ThenByDescending(r => r.DeviceCount)
            .ThenByDescending(r => r.LastChanged ?? DateTime.MinValue)
            .ThenBy(r => r.DisplayRuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.RuleId)
            .ToList();
    }

    /// <summary>
    /// What one fault is about - "CPU 0", a port, a location - from its own
    /// details, or its entity type and id. Null for a fault about the device
    /// as a whole, such as one carried over from a pre-26.10 alert.
    /// </summary>
    public static string? EntityName(Alert fault)
    {
        ArgumentNullException.ThrowIfNull(fault);

        if (string.IsNullOrEmpty(fault.EntityKey) && fault.EntityType is null)
        {
            return null;
        }

        var titled = AlertFaultParser.Parse(fault.Details, fault.Timestamp).Faults.FirstOrDefault()?.Title;
        if (!string.IsNullOrWhiteSpace(titled) && titled != "Matched row"
            && !string.Equals(titled, fault.Hostname, StringComparison.OrdinalIgnoreCase))
        {
            return titled;
        }

        if (fault.EntityType is { Length: > 0 } type)
        {
            var label = char.ToUpperInvariant(type[0]) + type[1..].Replace('_', ' ');
            return fault.EntityId is { } id ? $"{label} {id}" : label;
        }

        return string.IsNullOrEmpty(fault.EntityKey) ? null : fault.EntityKey;
    }
}
