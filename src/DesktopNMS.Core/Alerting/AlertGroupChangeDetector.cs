using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>One alert or fault as last seen: enough to know its group and whether it was open, across a restart.</summary>
public sealed record AlertWatchEntry(int DeviceId, int RuleId, int State, DateTime? ClearedAtUtc = null)
{
    public AlertGroupKey Group => new(DeviceId, RuleId);

    public bool IsOutstanding => State != (int)AlertState.Recovered;

    public bool IsUnacknowledged => IsOutstanding && State != (int)AlertState.Acknowledged;
}

/// <summary>
/// What the last poll saw (#5): every open alert or fault by
/// <see cref="AlertKey"/>, and those that recovered lately, so firing again
/// reads "again". Kept with the source it came from, so the switch to
/// LibreNMS 26.10's faults - which number afresh - starts from a quiet
/// baseline instead of mistaking fault 12 for the old alert 12.
/// </summary>
public sealed class AlertWatchState
{
    public const int CurrentVersion = 2;

    public AlertWatchState(AlertSource? source, IReadOnlyDictionary<AlertKey, AlertWatchEntry> entries, bool needsBaseline = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Source = source;
        Entries = entries;
        NeedsBaseline = needsBaseline;
    }

    /// <summary>Nothing seen yet - after signing in to a new server, or with no saved state.</summary>
    public static AlertWatchState Empty { get; } = new(null, new Dictionary<AlertKey, AlertWatchEntry>());

    /// <summary>
    /// Saved by a DashyNMS before #5: bare alert ids, no group or source. Not
    /// safe to compare against, so the next poll is a quiet baseline.
    /// </summary>
    public static AlertWatchState Outdated { get; } = new(null, new Dictionary<AlertKey, AlertWatchEntry>(), needsBaseline: true);

    /// <summary>Where the entries came from - null before the first poll.</summary>
    public AlertSource? Source { get; }

    public IReadOnlyDictionary<AlertKey, AlertWatchEntry> Entries { get; }

    /// <summary>The next poll should record what's open without notifying.</summary>
    public bool NeedsBaseline { get; }

    /// <summary>Alerts or faults that were open last poll.</summary>
    public IEnumerable<KeyValuePair<AlertKey, AlertWatchEntry>> Outstanding => Entries.Where(e => e.Value.IsOutstanding);
}

/// <summary>What one poll changed, and the state to carry into the next.</summary>
public sealed class AlertWatchResult
{
    public AlertWatchResult(IReadOnlyList<AlertChange> changes, AlertWatchState next, string? baselineReason)
    {
        Changes = changes;
        Next = next;
        BaselineReason = baselineReason;
    }

    public IReadOnlyList<AlertChange> Changes { get; }

    public AlertWatchState Next { get; }

    /// <summary>Why this poll only recorded what's open, for the log - null on an ordinary poll.</summary>
    public string? BaselineReason { get; }

    /// <summary>
    /// This poll started again from what's open, without notifying. Any
    /// notification still showing from before points at ids that may now
    /// mean something else, so the app should withdraw them.
    /// </summary>
    public bool IsBaseline => BaselineReason is not null;
}

/// <summary>
/// Compares a poll against the last one by device and rule (#5), so a rule
/// firing on eight cores is one change, not eight. Pure, like
/// <see cref="AlertChangeDetector"/>, which still compares single rows.
/// </summary>
/// <remarks>
/// A group recovers only when the last of its open faults is seen in the
/// recovered state. Something that is simply missing - deleted, out of
/// reach of the token, or not fetched - is never a recovery: it drops out
/// quietly, or with <c>unresolved</c> is kept as it was until a later poll
/// can tell.
/// </remarks>
public static class AlertGroupChangeDetector
{
    /// <summary>How long a recovery is remembered, so firing again within it reads "again".</summary>
    public static TimeSpan ReopenWindow { get; } = TimeSpan.FromHours(24);

    /// <param name="previous">What the last poll saw.</param>
    /// <param name="source">Where this poll's rows came from.</param>
    /// <param name="current">
    /// A complete, successful poll: the open alerts or faults, plus any of
    /// last poll's that were looked up and found recovered.
    /// </param>
    /// <param name="utcNow">Now, for remembering recoveries.</param>
    /// <param name="unresolved">Last poll's open rows this poll couldn't account for - kept as they were.</param>
    public static AlertWatchResult Detect(
        AlertWatchState previous,
        AlertSource source,
        IReadOnlyList<Alert> current,
        DateTime utcNow,
        IReadOnlyCollection<AlertKey>? unresolved = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var baseline = previous.NeedsBaseline
            ? "notification state saved by an older DashyNMS"
            : previous.Source is { } was && was != source
                ? source == AlertSource.Fault ? "LibreNMS now lists faults rather than alerts" : "LibreNMS now lists alerts rather than faults"
                : null;

        if (baseline is not null)
        {
            var quiet = current
                .Where(a => a.State != AlertState.Recovered)
                .GroupBy(a => a.Identity)
                .ToDictionary(g => g.Key, g => Entry(g.First(), clearedAtUtc: null));
            return new AlertWatchResult(Array.Empty<AlertChange>(), new AlertWatchState(source, quiet), baseline);
        }

        var rows = current.GroupBy(a => a.Identity).Select(g => g.First()).ToList();
        var carried = unresolved is null
            ? new Dictionary<AlertKey, AlertWatchEntry>()
            : previous.Outstanding.Where(e => unresolved.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value);

        var changes = new List<AlertChange>();
        foreach (var group in rows.GroupBy(a => new AlertGroupKey(a.DeviceId, a.RuleId)))
        {
            var change = Compare(group.Key, group.ToList(), previous, carried, utcNow);
            if (change is not null)
            {
                changes.Add(change);
            }
        }

        return new AlertWatchResult(changes, Next(previous, source, rows, carried, utcNow), baselineReason: null);
    }

    private static AlertChange? Compare(
        AlertGroupKey group,
        IReadOnlyList<Alert> rows,
        AlertWatchState previous,
        IReadOnlyDictionary<AlertKey, AlertWatchEntry> carried,
        DateTime utcNow)
    {
        var before = previous.Entries.Where(e => e.Value.Group == group).ToDictionary(e => e.Key, e => e.Value);
        var wasOpen = before.Values.Any(e => e.IsOutstanding);
        var wasActive = before.Values.Any(e => e.IsUnacknowledged);
        var recentlyRecovered = before.Values.Any(e => !e.IsOutstanding && IsRecent(e, utcNow));

        var open = rows
            .Where(a => a.State != AlertState.Recovered)
            .OrderBy(a => AlertGroup.IsUnacknowledged(a) ? 0 : 1)
            .ThenBy(a => a.EntityKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Id)
            .ToList();
        var stillCarried = carried.Where(e => e.Value.Group == group).Select(e => e.Value).ToList();

        var openCount = open.Count + stillCarried.Count;
        var isActive = open.Any(AlertGroup.IsUnacknowledged) || stillCarried.Any(e => e.IsUnacknowledged);

        var cleared = rows
            .Where(a => a.State == AlertState.Recovered && before.TryGetValue(a.Identity, out var e) && e.IsOutstanding)
            .OrderByDescending(a => a.Timestamp ?? DateTime.MinValue)
            .ToList();
        var added = open.Count(a => !before.TryGetValue(a.Identity, out var e) || !e.IsOutstanding);

        var previousState = wasActive ? AlertState.Active
            : wasOpen ? AlertState.Acknowledged
            : recentlyRecovered ? AlertState.Recovered
            : (AlertState?)null;

        AlertChange Make(AlertChangeKind kind, Alert shown, IReadOnlyList<Alert> faults)
            => new(shown, kind, previousState, faults, openCount, added, cleared.Count);

        if (!wasOpen)
        {
            // Firing again, or new. Something that turns up already acknowledged isn't news.
            if (!isActive || open.Count == 0)
            {
                return null;
            }

            var again = recentlyRecovered || open.Any(a => before.ContainsKey(a.Identity));
            return Make(again ? AlertChangeKind.Reopened : AlertChangeKind.New, open[0], open);
        }

        if (openCount == 0)
        {
            // Recovered only once the last open fault is seen recovering - never because rows went missing.
            return cleared.Count > 0 ? Make(AlertChangeKind.Recovered, cleared[0], cleared) : null;
        }

        if (open.Count == 0)
        {
            // Only faults that couldn't be looked up are left open: some cleared, the rest unknown.
            return wasActive && cleared.Count > 0 ? Make(AlertChangeKind.Updated, cleared[0], open) : null;
        }

        if (!wasActive && isActive)
        {
            var returned = open.Any(a => AlertGroup.IsUnacknowledged(a)
                && before.TryGetValue(a.Identity, out var e) && e.State == (int)AlertState.Acknowledged);
            return Make(returned ? AlertChangeKind.Unacknowledged : AlertChangeKind.New, open[0], open);
        }

        if (wasActive && !isActive)
        {
            return Make(AlertChangeKind.Acknowledged, open[0], open);
        }

        // Still firing: more faults, or some cleared, update the group; nothing else is news.
        return wasActive && (added > 0 || cleared.Count > 0)
            ? Make(AlertChangeKind.Updated, open[0], open)
            : null;
    }

    private static AlertWatchState Next(
        AlertWatchState previous,
        AlertSource source,
        IReadOnlyList<Alert> rows,
        IReadOnlyDictionary<AlertKey, AlertWatchEntry> carried,
        DateTime utcNow)
    {
        var next = new Dictionary<AlertKey, AlertWatchEntry>(carried);

        foreach (var row in rows)
        {
            if (row.State != AlertState.Recovered)
            {
                next[row.Identity] = Entry(row, clearedAtUtc: null);
                continue;
            }

            // A recovery is remembered only if this app saw it open - not the server's whole history.
            if (previous.Entries.TryGetValue(row.Identity, out var was))
            {
                var clearedAt = was.IsOutstanding ? utcNow : was.ClearedAtUtc;
                if (clearedAt is { } at && utcNow - at < ReopenWindow)
                {
                    next[row.Identity] = Entry(row, at);
                }
            }
        }

        foreach (var (key, entry) in previous.Entries)
        {
            if (!entry.IsOutstanding && !next.ContainsKey(key) && IsRecent(entry, utcNow))
            {
                next[key] = entry;
            }
        }

        return new AlertWatchState(source, next);
    }

    private static AlertWatchEntry Entry(Alert alert, DateTime? clearedAtUtc)
        => new(alert.DeviceId, alert.RuleId, alert.StateValue, clearedAtUtc);

    private static bool IsRecent(AlertWatchEntry entry, DateTime utcNow)
        => entry.ClearedAtUtc is { } at && utcNow - at < ReopenWindow;
}
