using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Spotlights;

namespace DesktopNMS.Core.Logs;

/// <summary>
/// The Event log dashboard widget's list (#202): the fleet-wide event log,
/// newest first, narrowed by type and text. Here rather than in the widget
/// so DashyNMS Mobile can show the same feed.
/// </summary>
public static class EventLogFeed
{
    /// <summary>
    /// How many entries to ask LibreNMS for. The API can't filter by type or
    /// text, so with a filter on, fetch a deeper page and filter that -
    /// otherwise a rare type would show nothing at all.
    /// </summary>
    public const int FilteredFetchLimit = 250;

    public static int FetchLimit(int count, string? type, string? search)
        => string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(search) ? count : Math.Max(count, FilteredFetchLimit);

    /// <summary>
    /// A Spotlight of up to this many devices asks for each device's own log
    /// (desktop#552): exact however busy the rest of the network is. More
    /// than that would be too many calls on every poll, so a bigger one
    /// filters the fleet-wide log instead.
    /// </summary>
    public const int MaxPerDeviceFetches = 8;

    /// <summary>
    /// How deep the fleet-wide log is read for a bigger Spotlight. A group
    /// that's quiet on a busy network can still show fewer than it could -
    /// LibreNMS can't filter the fleet-wide log by device.
    /// </summary>
    public const int SpotlightFetchLimit = 500;

    /// <summary>
    /// The entries to choose from for a card kept to <paramref name="scope"/>
    /// (null or the whole network: the fleet-wide log, as before), newest
    /// first. Narrow them further with <see cref="Filter"/>; read the type
    /// choices from them with <see cref="Types"/>.
    /// </summary>
    public static async Task<IReadOnlyList<EventLogEntry>> FetchAsync(
        ILogsApi logs,
        int count,
        string? type,
        string? search,
        SpotlightScope? scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logs);

        var limit = FetchLimit(count, type, search);
        if (scope is null || scope.IsWholeNetwork)
        {
            return await logs.ListEventLogAsync(null, limit, cancellationToken).ConfigureAwait(false);
        }

        if (scope.IsEmpty)
        {
            return Array.Empty<EventLogEntry>();
        }

        IEnumerable<EventLogEntry> entries;
        if (scope.DeviceIds.Count <= MaxPerDeviceFetches)
        {
            var pages = await Task.WhenAll(scope.DeviceIds.Select(id => logs.ListEventLogAsync(id, limit, cancellationToken))).ConfigureAwait(false);
            entries = pages.SelectMany(p => p);
        }
        else
        {
            var all = await logs.ListEventLogAsync(null, Math.Max(limit, SpotlightFetchLimit), cancellationToken).ConfigureAwait(false);
            entries = all.Where(e => scope.Includes(e.DeviceId));
        }

        return entries
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .OrderByDescending(e => e.Timestamp ?? DateTime.MinValue)
            .ThenByDescending(e => e.Id)
            .ToList();
    }

    /// <summary>
    /// The entries to show: only <paramref name="type"/> (any case) when set,
    /// only those whose message, type, hostname or sysName contains
    /// <paramref name="search"/> when set, newest first, at most
    /// <paramref name="count"/>.
    /// </summary>
    public static IReadOnlyList<EventLogEntry> Filter(IEnumerable<EventLogEntry> entries, string? type, string? search, int count)
    {
        var term = search?.Trim();
        return entries
            .Where(e => string.IsNullOrWhiteSpace(type) || string.Equals(e.Type?.Trim(), type.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(e => string.IsNullOrEmpty(term) || Matches(e, term))
            .OrderByDescending(e => e.Timestamp ?? DateTime.MinValue)
            .ThenByDescending(e => e.Id)
            .Take(Math.Max(count, 0))
            .ToList();
    }

    /// <summary>Every type among <paramref name="entries"/>, sorted, each once whatever its case - the type filter's choices.</summary>
    public static IReadOnlyList<string> Types(IEnumerable<EventLogEntry> entries)
        => entries
            .Select(e => e.Type?.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool Matches(EventLogEntry entry, string term)
        => Contains(entry.Message, term)
           || Contains(entry.Type, term)
           || Contains(entry.Hostname, term)
           || Contains(entry.SysName, term);

    private static bool Contains(string? text, string term)
        => text?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false;
}
