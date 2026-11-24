using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Api;

/// <summary>Log endpoints.</summary>
public interface ILogsApi
{
    /// <summary>
    /// GET /api/v0/logs/alertlog/{device}, newest first - or, with no device,
    /// GET /api/v0/logs/alertlog for every device's entries together.
    /// </summary>
    /// <param name="deviceId">Device to fetch entries for, or null for every device. Each entry still carries its device id, hostname and sysName.</param>
    /// <param name="limit">Maximum entries to return. The server defaults to 50.</param>
    Task<IReadOnlyList<AlertLogEntry>> ListAlertLogAsync(
        int? deviceId,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent alert log entry for a rule on a device. Right
    /// for a legacy alert; on LibreNMS 26.10 it may belong to another fault of
    /// the same rule, so use <see cref="Alerting.AlertLogLookups.GetLatestForAlertAsync"/> (#7).
    /// </summary>
    Task<AlertLogEntry?> GetLatestForRuleAsync(
        int deviceId,
        int ruleId,
        int searchDepth = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v0/logs/eventlog/{device}, newest first - LibreNMS's general
    /// audit trail for the device, distinct from the alert log. With no device,
    /// GET /api/v0/logs/eventlog returns every device's entries together.
    /// </summary>
    /// <remarks>
    /// There is no separate offset/skip parameter here (a "start" query
    /// parameter was tried and confirmed, against a real server, to have no
    /// effect - the same top <paramref name="limit"/> entries came back
    /// regardless). "Load more" further back therefore means re-issuing this
    /// call with a larger <paramref name="limit"/> and taking only the tail
    /// beyond what is already on screen, rather than true paging. To read far
    /// back, use <see cref="ListEventLogUpToAsync"/> instead.
    /// </remarks>
    /// <param name="deviceId">Device to fetch entries for, or null for every device (#194).</param>
    /// <param name="limit">Maximum entries to return, counted from newest. The server defaults to 50.</param>
    Task<IReadOnlyList<EventLogEntry>> ListEventLogAsync(
        int? deviceId,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <see cref="ListEventLogUpToAsync"/> and <see cref="ListAlertLogUpToAsync"/>
    /// work here. False unless an implementation says otherwise, so a host's own
    /// wrapper keeps exactly the reads it allowed before they existed (#101).
    /// </summary>
    bool CanReadUpTo => false;

    /// <summary>
    /// The newest event log entries at or before <paramref name="upTo"/>, newest
    /// first - how to read further back without asking for everything since (#101).
    /// </summary>
    /// <remarks>
    /// LibreNMS compares the time with the column the entries carry, so to
    /// continue from an entry pass its own <see cref="EventLogEntry.Timestamp"/>
    /// back as it came, and never one that's been through <see cref="ServerTime"/>:
    /// no time zone conversion is made, whatever the value's kind. Entries at
    /// that exact second come back again, because several can share one, so match
    /// them by <see cref="EventLogEntry.Id"/>. If a whole page has nothing new,
    /// more than <paramref name="limit"/> share the second: ask for more.
    /// </remarks>
    /// <param name="deviceId">Device to fetch entries for, or null for every device.</param>
    /// <param name="upTo">A time in the server's own terms. Part of a second counts as the next whole one, so nothing is skipped.</param>
    /// <param name="limit">Maximum entries to return.</param>
    /// <exception cref="NotSupportedException"><see cref="CanReadUpTo"/> is false.</exception>
    Task<IReadOnlyList<EventLogEntry>> ListEventLogUpToAsync(
        int? deviceId,
        DateTime upTo,
        int limit,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <summary>As <see cref="ListEventLogUpToAsync"/>, for the alert log and <see cref="AlertLogEntry.TimeLogged"/>.</summary>
    /// <exception cref="NotSupportedException"><see cref="CanReadUpTo"/> is false.</exception>
    Task<IReadOnlyList<AlertLogEntry>> ListAlertLogUpToAsync(
        int? deviceId,
        DateTime upTo,
        int limit,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
