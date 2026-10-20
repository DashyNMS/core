using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>One notification to show: what it says and how insistent it is. How it's shown (a Windows toast, a phone notification) is up to the app.</summary>
public sealed record PlannedNotification(
    string Title,
    string Body,
    string? Detail,
    AlertSeverity Severity,
    ToastPersistence Persistence,
    bool PlaySound,
    bool IsProblem,
    AlertChange? Change)
{
    /// <summary>
    /// The device and rule's notification, if one is still showing, should
    /// change in place without popping up again (#5) - more faults, or some
    /// cleared, on a group that was already firing.
    /// </summary>
    public bool IsUpdate { get; init; }

    /// <summary>
    /// Names the device and rule's notification (<see cref="AlertChange.NotificationTag"/>),
    /// so a later one for the same group replaces it. Null for a summary.
    /// </summary>
    public string? Tag { get; init; }
}

/// <summary>What a poll's alert changes turn into: the notifications to show, or why there are none.</summary>
public sealed class AlertNotificationPlan
{
    public static readonly AlertNotificationPlan Nothing = new(Array.Empty<PlannedNotification>(), isSummary: false, notifiableCount: 0, suppressionReason: null);

    public AlertNotificationPlan(IReadOnlyList<PlannedNotification> notifications, bool isSummary, int notifiableCount, string? suppressionReason, int leftOutByRuleCount = 0)
    {
        Notifications = notifications;
        LeftOutByRuleCount = leftOutByRuleCount;
        IsSummary = isSummary;
        NotifiableCount = notifiableCount;
        SuppressionReason = suppressionReason;
    }

    public IReadOnlyList<PlannedNotification> Notifications { get; }

    /// <summary>More changes deserved a notification than <see cref="NotificationSettings.MaxToastsPerPoll"/> allows, so they are rolled into one summary.</summary>
    public bool IsSummary { get; }

    /// <summary>How many changes deserved a notification, before any summarising.</summary>
    public int NotifiableCount { get; }

    /// <summary>Why nothing is shown, in one line for the log - "Why did nothing pop up?" is the most common question about notifications.</summary>
    public string? SuppressionReason { get; }

    /// <summary>How many changes the rules chosen in <see cref="NotificationSettings.RuleMode"/> left out (#268), before severity and quiet hours were weighed.</summary>
    public int LeftOutByRuleCount { get; }
}

/// <summary>
/// Which alert changes deserve a notification, what each says, and when they
/// are rolled into one summary (#195). Kept free of any UI so desktop and
/// mobile decide the same way - the desktop shows the result as Windows
/// toasts, the phone as its own notifications.
/// </summary>
public static class AlertNotificationPlanner
{
    /// <param name="changes">This poll's alert changes.</param>
    /// <param name="isFirstPoll">The first poll after starting, which <see cref="NotificationSettings.SuppressOnFirstPoll"/> can keep quiet.</param>
    /// <param name="settings">The user's notification settings.</param>
    /// <param name="localNow">Now, in local time - for quiet hours.</param>
    /// <param name="deviceName">Names an alert's device the way the app's alert list does.</param>
    /// <param name="deviceLocation">An alert's device location, for the summary's "+2 more in Rack 3".</param>
    /// <param name="wasSelfInitiated">True for a change this app itself made (acknowledging from the app), which never notifies.</param>
    public static AlertNotificationPlan Plan(
        IReadOnlyList<AlertChange> changes,
        bool isFirstPoll,
        NotificationSettings settings,
        DateTime localNow,
        Func<Alert, string> deviceName,
        Func<Alert, string?>? deviceLocation = null,
        Func<AlertChange, bool>? wasSelfInitiated = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(deviceName);

        if (changes.Count == 0)
        {
            return AlertNotificationPlan.Nothing;
        }

        if (!settings.Enabled)
        {
            return Suppressed("notifications are switched off in settings");
        }

        if (isFirstPoll && settings.SuppressOnFirstPoll)
        {
            return Suppressed("this was the first poll after starting");
        }

        // The rules chosen to notify about (#268) come first: a left-out alert
        // is quiet whatever its severity or the time of day.
        var allowed = changes.Where(change => settings.AllowsRule(change.Alert)).ToList();
        var leftOut = changes.Count - allowed.Count;

        var notifiable = allowed.Where(change => ShouldNotify(change, settings, localNow, wasSelfInitiated)).ToList();
        if (notifiable.Count == 0)
        {
            var reasons = new List<string>();
            if (leftOut > 0)
            {
                reasons.Add($"{leftOut} left out by the alert rules chosen in settings");
            }

            if (allowed.Count > 0)
            {
                reasons.Add(DescribeSuppression(allowed, settings, localNow));
            }

            return Suppressed("none notifiable (" + string.Join("; ", reasons) + ")", leftOut);
        }

        // Updates change a group's notification in place, so they never count
        // towards a burst - and a summary covers them.
        var fresh = notifiable.Where(change => change.Kind != AlertChangeKind.Updated).ToList();
        if (fresh.Count > settings.MaxToastsPerPoll)
        {
            return new AlertNotificationPlan(new[] { Summary(fresh, settings, deviceName, deviceLocation) }, isSummary: true, notifiable.Count, suppressionReason: null, leftOut);
        }

        var planned = notifiable.Select(change => ForChange(change, settings, deviceName)).ToList();
        return new AlertNotificationPlan(planned, isSummary: false, notifiable.Count, suppressionReason: null, leftOut);
    }

    /// <summary>Whether one change deserves a notification of its own.</summary>
    public static bool ShouldNotify(AlertChange change, NotificationSettings settings, DateTime localNow, Func<AlertChange, bool>? wasSelfInitiated = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(settings);

        // Acknowledging or returning an alert to active from within the app
        // must not notify you about your own action.
        if (change.Kind is AlertChangeKind.Acknowledged or AlertChangeKind.Unacknowledged
            && wasSelfInitiated is not null
            && WasSelfInitiated(change, wasSelfInitiated))
        {
            return false;
        }

        if (!settings.AllowsRule(change.Alert))
        {
            return false;
        }

        var severity = change.Alert.Severity;
        if (settings.IsInQuietHours(localNow, severity))
        {
            return false;
        }

        return change.Kind switch
        {
            AlertChangeKind.New or AlertChangeKind.Reopened or AlertChangeKind.Unacknowledged or AlertChangeKind.Updated
                => settings.ForSeverity(severity).Enabled,
            AlertChangeKind.Recovered => settings.NotifyOnRecovery,
            AlertChangeKind.Acknowledged => settings.NotifyOnAcknowledge,
            _ => false,
        };
    }

    /// <summary>
    /// A group's acknowledgement was this app's own if any of its faults was:
    /// the tracker records the fault acted on, and a group change is shown by
    /// its first. Every fault is asked, as asking consumes the record.
    /// </summary>
    private static bool WasSelfInitiated(AlertChange change, Func<AlertChange, bool> wasSelfInitiated)
    {
        if (change.Faults.Count <= 1)
        {
            return wasSelfInitiated(change);
        }

        var any = false;
        foreach (var fault in change.Faults)
        {
            any |= wasSelfInitiated(new AlertChange(fault, change.Kind, change.PreviousState));
        }

        return any;
    }

    /// <summary>"Critical: core-sw-01", "Critical again: ...", "Recovered: ..." and so on.</summary>
    public static string BuildTitle(AlertChange change, string device)
    {
        ArgumentNullException.ThrowIfNull(change);

        return change.Kind switch
        {
            AlertChangeKind.Recovered => $"Recovered: {device}",
            AlertChangeKind.Acknowledged => $"Acknowledged: {device}",
            AlertChangeKind.Unacknowledged => $"Unacknowledged: {device}",
            AlertChangeKind.Reopened => $"{change.Alert.Severity.ToDisplayString()} again: {device}",
            _ => $"{change.Alert.Severity.ToDisplayString()}: {device}",
        };
    }

    /// <summary>Explains, in one line, why a set of changes produced no notification.</summary>
    public static string DescribeSuppression(IReadOnlyList<AlertChange> changes, NotificationSettings settings, DateTime localNow)
    {
        var reasons = new List<string>();

        var quiet = changes.Count(c => settings.IsInQuietHours(localNow, c.Alert.Severity));
        if (quiet > 0)
        {
            reasons.Add($"{quiet} within quiet hours");
        }

        var acknowledged = changes.Count(c => c.Kind == AlertChangeKind.Acknowledged);
        if (acknowledged > 0 && !settings.NotifyOnAcknowledge)
        {
            reasons.Add($"{acknowledged} acknowledged, and 'notify on acknowledge' is off");
        }

        var recovered = changes.Count(c => c.Kind == AlertChangeKind.Recovered);
        if (recovered > 0 && !settings.NotifyOnRecovery)
        {
            reasons.Add($"{recovered} recovered, and 'notify on recovery' is off");
        }

        foreach (var severity in new[] { AlertSeverity.Critical, AlertSeverity.Warning, AlertSeverity.Ok })
        {
            if (settings.ForSeverity(severity).Enabled)
            {
                continue;
            }

            var count = changes.Count(c => c.IsProblem && c.Alert.Severity == severity);
            if (count > 0)
            {
                reasons.Add($"{count} {severity.ToDisplayString().ToLowerInvariant()}, which is switched off");
            }
        }

        return reasons.Count > 0 ? string.Join("; ", reasons) : "no reason recorded";
    }

    /// <summary>The first line of an alert note, trimmed to fit a notification.</summary>
    public static string FirstLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var index = text.IndexOfAny(new[] { '\r', '\n' });
        var line = index >= 0 ? text[..index] : text;
        return line.Length <= 120 ? line : line[..117] + "...";
    }

    private static PlannedNotification ForChange(AlertChange change, NotificationSettings settings, Func<Alert, string> deviceName)
    {
        var alert = change.Alert;
        var isProblem = change.IsProblem;
        var isUpdate = change.Kind == AlertChangeKind.Updated;
        var severitySettings = settings.ForSeverity(alert.Severity);

        // Recoveries and acknowledgements are informational: never sticky, never a sound.
        // An update keeps the group's notification as insistent as it was, but silent.
        return new PlannedNotification(
            BuildTitle(change, deviceName(alert)),
            alert.DisplayRuleName,
            Detail(change),
            alert.Severity,
            isProblem || isUpdate ? severitySettings.Persistence : ToastPersistence.Transient,
            isProblem && severitySettings.PlaySound,
            isProblem,
            change)
        {
            IsUpdate = isUpdate,
            Tag = change.NotificationTag,
        };
    }

    /// <summary>
    /// What the faults are: "CPU 0, CPU 1 and 2 more", "2 more faults, 6 open",
    /// "All 4 faults cleared" - or for a single alert its note, or what it's about.
    /// </summary>
    public static string? Detail(AlertChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        switch (change.Kind)
        {
            case AlertChangeKind.Updated:
            {
                var parts = new List<string>();
                if (change.AddedCount > 0)
                {
                    parts.Add(change.AddedCount == 1 ? "1 more fault" : $"{change.AddedCount} more faults");
                }

                if (change.ClearedCount > 0)
                {
                    parts.Add($"{change.ClearedCount} cleared");
                }

                parts.Add($"{change.OutstandingCount} open");
                return string.Join(", ", parts);
            }

            case AlertChangeKind.Recovered when change.ClearedCount > 1:
                return $"All {change.ClearedCount} faults cleared";
        }

        if (change.OutstandingCount > 1)
        {
            var names = change.Faults.Select(AlertGrouping.EntityName).OfType<string>().Distinct().ToList();
            if (names.Count == 0)
            {
                return $"{change.OutstandingCount} faults";
            }

            var shown = names.Take(2).ToList();
            var more = change.OutstandingCount - shown.Count;
            return more > 0 ? $"{string.Join(", ", shown)} and {more} more" : string.Join(" and ", shown);
        }

        return string.IsNullOrWhiteSpace(change.Alert.Note)
            ? AlertGrouping.EntityName(change.Alert)
            : FirstLine(change.Alert.Note!);
    }

    private static PlannedNotification Summary(
        IReadOnlyList<AlertChange> changes,
        NotificationSettings settings,
        Func<Alert, string> deviceName,
        Func<Alert, string?>? deviceLocation)
    {
        var problems = changes.Where(c => c.IsProblem).ToList();
        var critical = problems.Count(c => c.Alert.Severity == AlertSeverity.Critical);

        // "3 new critical alerts" / "core-sw-02 - High temperature" / "+2 more in Rack 3".
        var (title, body, detail) = problems.Count > 0
            ? AlertSummaryText.Build(problems
                .Select(c => new AlertSummaryItem(c.Alert.Severity, deviceName(c.Alert), c.Alert.DisplayRuleName, deviceLocation?.Invoke(c.Alert)))
                .ToList())
            : ($"{changes.Count} alert updates", "See DashyNMS for details.", null);

        var sticky = critical > 0 && settings.Critical.Persistence != ToastPersistence.Transient;

        return new PlannedNotification(
            title,
            body,
            detail,
            critical > 0 ? AlertSeverity.Critical : AlertSeverity.Warning,
            sticky ? ToastPersistence.UntilDismissed : ToastPersistence.Transient,
            PlaySound: true,
            IsProblem: problems.Count > 0,
            Change: null);
    }

    private static AlertNotificationPlan Suppressed(string reason, int leftOutByRuleCount = 0)
        => new(Array.Empty<PlannedNotification>(), isSummary: false, notifiableCount: 0, reason, leftOutByRuleCount);
}
