using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>How often a rule has fired on a device before, within a period (#270).</summary>
/// <param name="Count">Times it went into alert before this one - occurrences, not fault events (#7).</param>
/// <param name="LastFired">The latest of those, in local time - null when there were none.</param>
/// <param name="Period">The period counted over.</param>
/// <param name="IsIncomplete">The log fetched doesn't cover the whole period, so <paramref name="Count"/> is a minimum.</param>
public sealed record AlertHistorySummary(int Count, DateTime? LastFired, TimeSpan Period, bool IsIncomplete = false)
{
    /// <summary>"Fired 6 times in the last 30 days · last 2 days ago", or "No earlier alerts in the last 30 days".</summary>
    public string Describe(DateTime localNow)
    {
        var days = (int)Math.Round(Period.TotalDays);
        var span = $"in the last {days} day{(days == 1 ? string.Empty : "s")}";
        if (Count == 0 || LastFired is not { } last)
        {
            return IsIncomplete ? "No earlier alerts in the history available" : $"No earlier alerts {span}";
        }

        var times = Count == 1 ? "once" : $"{Count} times";
        var atLeast = IsIncomplete ? "at least " : string.Empty;
        return $"Fired {atLeast}{times} {span} · last {Ago(localNow - last)}";
    }

    private static string Ago(TimeSpan ago) => ago switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)ago.TotalMinutes}m ago",
        { TotalDays: < 1 } => $"{(int)ago.TotalHours}h ago",
        { TotalDays: < 2 } => "yesterday",
        _ => $"{(int)ago.TotalDays} days ago",
    };
}

/// <summary>
/// One stretch of a rule firing on a device (#7): from its first fault going
/// into alert until its last recovering. Eight cores going into alert
/// together are one occurrence of eight fault events.
/// </summary>
/// <param name="Started">When the first fault went into alert, local time - null when that's older than the log fetched.</param>
/// <param name="Ended">When the last fault recovered, local time - null while it's still open.</param>
/// <param name="FaultCount">How many faults went into alert during it - 1 for an alert before LibreNMS 26.10.</param>
/// <param name="RecoveryUnseen">A legacy alert fired again with no recovery logged between - this one ended, but when isn't known.</param>
public sealed record AlertOccurrence(DateTime? Started, DateTime? Ended, int FaultCount, bool RecoveryUnseen = false)
{
    /// <summary>It started before the oldest log entry fetched, so when - and with how many faults - isn't known.</summary>
    public bool StartedBeforeHistory => Started is null;

    public bool IsOpen => Ended is null && !RecoveryUnseen;
}

/// <summary>
/// Reads a rule's history on a device from LibreNMS's alert log (#270, #7) -
/// shared with DashyNMS Mobile, whose alert page shows the same, to tell a
/// one-off from a flapping alert.
/// </summary>
/// <remarks>
/// Before LibreNMS 26.10 a firing is a row in state 1 and a recovery a row in
/// state 0. From 26.10 each fault logs its own rows (with <c>fault_id</c>),
/// so a firing is rebuilt from the faults outstanding: it starts when the
/// first goes into alert and ends when the last recovers. Acknowledgements
/// and worse/better/changed rows are in the same log, but aren't firings.
/// </remarks>
public static class AlertHistory
{
    /// <summary>The period counted over by default.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(30);

    /// <param name="entries">The device's alert log, any order.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="localNow">Now, in local time.</param>
    /// <param name="serverTimestampsAreUtc">How the server stores its times - see <see cref="Configuration.AppSettings.ServerTimestampsAreUtc"/>.</param>
    /// <param name="excludeCurrent">Leave out the newest occurrence, as it's the alert being looked at.</param>
    /// <param name="period">How far back to count; <see cref="DefaultPeriod"/> if null.</param>
    /// <param name="truncated">
    /// The log stopped at the number of entries asked for, so older ones exist
    /// that weren't fetched. If it then doesn't reach back over the whole
    /// period, the count is reported as a minimum.
    /// </param>
    public static AlertHistorySummary Summarise(
        IEnumerable<AlertLogEntry> entries,
        int ruleId,
        int deviceId,
        DateTime localNow,
        bool serverTimestampsAreUtc,
        bool excludeCurrent = true,
        TimeSpan? period = null,
        bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var log = entries.Where(e => e.TimeLogged is not null).ToList();
        var within = period ?? DefaultPeriod;
        var since = localNow - within;

        var occurrences = Occurrences(log, ruleId, deviceId, serverTimestampsAreUtc).ToList();
        if (excludeCurrent && occurrences.Count > 0)
        {
            occurrences.RemoveAt(occurrences.Count - 1);
        }

        var reachesBack = !truncated
            || (log.Count > 0 && log.Min(e => ServerTime.ToLocal(e.TimeLogged!.Value, serverTimestampsAreUtc)) <= since);

        // One that started before the log fetched may or may not have started within the period.
        var unknownStart = occurrences.Any(o => o.StartedBeforeHistory && (o.Ended is null || o.Ended >= since));

        var counted = occurrences.Where(o => o.Started >= since).Select(o => o.Started!.Value).ToList();
        return new AlertHistorySummary(counted.Count, counted.Count > 0 ? counted.Max() : null, within, !reachesBack || unknownStart);
    }

    /// <summary>
    /// The rule's occurrences on the device, oldest first, rebuilt from its
    /// log rows. Legacy and per-fault rows can be mixed - a server upgraded
    /// to 26.10 keeps its older entries.
    /// </summary>
    public static IReadOnlyList<AlertOccurrence> Occurrences(
        IEnumerable<AlertLogEntry> entries,
        int ruleId,
        int deviceId,
        bool serverTimestampsAreUtc)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var rows = entries
            .Where(e => e.RuleId == ruleId && e.DeviceId == deviceId && e.TimeLogged is not null)
            .OrderBy(e => e.TimeLogged)
            .ThenBy(e => e.Id)
            .ToList();

        var occurrences = new List<AlertOccurrence>();
        var openFaults = new HashSet<int>();
        var faultsSeen = new HashSet<int>();
        DateTime? started = null;
        var open = false;
        var firedYet = false;

        // Recoveries before the first firing fetched belong to one occurrence that began earlier.
        void Orphan(DateTime at)
        {
            if (!firedYet && occurrences.Count > 0)
            {
                occurrences[^1] = occurrences[^1] with { Ended = at, FaultCount = occurrences[^1].FaultCount + 1 };
            }
            else
            {
                occurrences.Add(new AlertOccurrence(null, at, 1));
            }
        }

        foreach (var row in rows)
        {
            var at = ServerTime.ToLocal(row.TimeLogged!.Value, serverTimestampsAreUtc);
            var firing = row.StateValue == (int)AlertState.Active;
            var recovered = row.StateValue == (int)AlertState.Recovered;

            if (firing)
            {
                firedYet = true;
                if (open && row.FaultId is null && openFaults.Count == 0)
                {
                    // A legacy alert only fires again after recovering, even if the recovery wasn't logged.
                    occurrences.Add(new AlertOccurrence(started, null, Math.Max(1, faultsSeen.Count), RecoveryUnseen: true));
                    open = false;
                }

                if (!open)
                {
                    open = true;
                    started = at;
                    faultsSeen.Clear();
                }

                if (row.FaultId is { } id)
                {
                    openFaults.Add(id);
                    faultsSeen.Add(id);
                }

                continue;
            }

            if (!recovered)
            {
                // Acknowledged, or worse/better/changed - not a firing either way.
                continue;
            }

            if (row.FaultId is { } faultId)
            {
                var wasOpen = openFaults.Remove(faultId);
                if (!open)
                {
                    // A recovery for a fault that went into alert before the oldest entry fetched.
                    if (!wasOpen)
                    {
                        Orphan(at);
                    }

                    continue;
                }

                if (openFaults.Count > 0)
                {
                    continue;
                }
            }
            else if (!open)
            {
                Orphan(at);
                continue;
            }
            else
            {
                // A legacy alert recovering ends it, faults or not.
                openFaults.Clear();
            }

            occurrences.Add(new AlertOccurrence(started, at, Math.Max(1, faultsSeen.Count)));
            open = false;
            started = null;
        }

        if (open)
        {
            occurrences.Add(new AlertOccurrence(started, null, Math.Max(1, faultsSeen.Count)));
        }

        return occurrences;
    }
}
