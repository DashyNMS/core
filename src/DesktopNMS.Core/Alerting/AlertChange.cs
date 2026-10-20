using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>What happened to an alert between two polls.</summary>
public enum AlertChangeKind
{
    /// <summary>An alert id that was not present in the previous snapshot.</summary>
    New,

    /// <summary>Previously recovered, now firing again.</summary>
    Reopened,

    /// <summary>Moved to state 2 (acknowledged).</summary>
    Acknowledged,

    /// <summary>Was acknowledged, has gone back to state 1.</summary>
    Unacknowledged,

    /// <summary>Moved to state 0 (recovered).</summary>
    Recovered,

    /// <summary>
    /// A device/rule group that was already firing gained or cleared faults
    /// (#5) - "2 more faults", "1 cleared, 3 still open". An update to the
    /// group's notification, not a new one.
    /// </summary>
    Updated,
}

/// <summary>
/// A single difference between the previous and current alert snapshots: one
/// alert, or from <see cref="AlertGroupChangeDetector"/> one device/rule
/// group and the faults in it.
/// </summary>
public sealed class AlertChange
{
    public AlertChange(Alert alert, AlertChangeKind kind, AlertState? previousState)
        : this(alert, kind, previousState, new[] { alert }, alert.State == AlertState.Recovered ? 0 : 1, addedCount: 0, clearedCount: 0)
    {
    }

    /// <param name="alert">The fault to show for the group - its first outstanding one, or for a recovery the last to clear.</param>
    /// <param name="kind">What happened.</param>
    /// <param name="previousState">The group's state last poll, if it was seen.</param>
    /// <param name="faults">The group's outstanding faults, or for a recovery the ones that cleared.</param>
    /// <param name="outstandingCount">How many faults the group has open now - zero once recovered.</param>
    /// <param name="addedCount">Faults open now that weren't last poll.</param>
    /// <param name="clearedCount">Faults that recovered since last poll.</param>
    public AlertChange(
        Alert alert,
        AlertChangeKind kind,
        AlertState? previousState,
        IReadOnlyList<Alert> faults,
        int outstandingCount,
        int addedCount,
        int clearedCount)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(faults);

        Alert = alert;
        Kind = kind;
        PreviousState = previousState;
        Faults = faults;
        OutstandingCount = outstandingCount;
        AddedCount = addedCount;
        ClearedCount = clearedCount;
    }

    public Alert Alert { get; }

    public AlertChangeKind Kind { get; }

    /// <summary>Null when the alert was not in the previous snapshot.</summary>
    public AlertState? PreviousState { get; }

    /// <summary>The device and rule this change is about.</summary>
    public AlertGroupKey Group => new(Alert.DeviceId, Alert.RuleId);

    /// <summary>The group's outstanding faults, or for a recovery the ones that cleared. Just <see cref="Alert"/> for a single alert.</summary>
    public IReadOnlyList<Alert> Faults { get; }

    /// <summary>How many faults the group has open now.</summary>
    public int OutstandingCount { get; }

    /// <summary>Faults open now that weren't last poll.</summary>
    public int AddedCount { get; }

    /// <summary>Faults that recovered since last poll.</summary>
    public int ClearedCount { get; }

    /// <summary>
    /// Names the group's notification, so a later change to the same device
    /// and rule replaces it rather than adding another - "device:7/rule:3".
    /// </summary>
    public string NotificationTag => Group.ToString();

    /// <summary>True for the kinds that represent a problem starting or resuming.</summary>
    public bool IsProblem => Kind is AlertChangeKind.New or AlertChangeKind.Reopened or AlertChangeKind.Unacknowledged;

    public override string ToString() => $"{Kind}: {Alert}";
}
