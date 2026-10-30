using System.Globalization;
using System.Text;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Graylog;

/// <summary>
/// Builds Graylog searches the way LibreNMS does (<c>GraylogApi::buildSimpleQuery</c>,
/// <c>getAddresses</c> and <c>Table\GraylogController</c>), so a device's
/// Graylog tab here shows the same messages its Graylog tab in LibreNMS does.
/// </summary>
public static class GraylogQuery
{
    private const string Ipv4Loopback = "127.0.0.1";
    private const string Ipv6LoopbackExpanded = "0000:0000:0000:0000:0000:0000:0000:0001";
    private const string Ipv6LoopbackCompressed = "::1";

    /// <summary>
    /// The relative-time ranges LibreNMS's Graylog filter offers, in seconds
    /// (0 = all time).
    /// </summary>
    public static IReadOnlyList<(int Seconds, string Label)> Ranges { get; } = new[]
    {
        (0, "All time"),
        (300, "Last 5 minutes"),
        (900, "Last 15 minutes"),
        (1800, "Last 30 minutes"),
        (3600, "Last 1 hour"),
        (7200, "Last 2 hours"),
        (28800, "Last 8 hours"),
        (86400, "Last 1 day"),
        (172800, "Last 2 days"),
        (432000, "Last 5 days"),
        (604800, "Last 7 days"),
        (1209600, "Last 14 days"),
        (2592000, "Last 30 days"),
    };

    /// <summary>
    /// <c>message:"search" &amp;&amp; field: ("a" OR "b")</c>, either part
    /// left out when empty, or "*" when both are. Unlike LibreNMS, quotes and
    /// backslashes in the search text are escaped, so a search containing a
    /// quote is searched for rather than breaking the query.
    /// </summary>
    public static string BuildSimpleQuery(string? search, string? field, IReadOnlyCollection<string>? addresses)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            parts.Add("message:\"" + Escape(search.Trim()) + "\"");
        }

        if (addresses is { Count: > 0 })
        {
            var queryField = string.IsNullOrWhiteSpace(field) ? "source" : field.Trim();
            parts.Add(queryField + ": (\"" + string.Join("\" OR \"", addresses.Select(Escape)) + "\")");
        }

        return parts.Count == 0 ? "*" : string.Join(" && ", parts);
    }

    /// <summary>Appends LibreNMS's <c> AND level: &lt;=N</c> - only messages at that severity or more severe.</summary>
    public static string WithMaxLevel(string query, int? maxLevel) =>
        maxLevel is { } level
            ? query + " AND level: <=" + level.ToString(CultureInfo.InvariantCulture)
            : query;

    /// <summary>
    /// The longest query sent. Graylog takes the query in the URL, and a
    /// longer one can be refused by Graylog or a proxy in front of it with an
    /// obscure HTTP error.
    /// </summary>
    public const int MaxQueryLength = 3500;

    /// <summary>
    /// A dashboard card's query kept to a Spotlight (desktop#552): its
    /// search, in Graylog's own syntax, and only messages from the
    /// Spotlight's devices - <c>(search) AND source: ("a" OR "b")</c>.
    /// Each device is matched by its hostname, display name, IP and sysName
    /// (<see cref="DeviceAddresses"/> without a DNS lookup or its interface
    /// addresses, which would cost calls per device on every poll). Devices
    /// are added in order until the query would pass
    /// <see cref="MaxQueryLength"/>; the rest are counted in
    /// <see cref="GraylogSpotlightQuery.DevicesLeftOut"/> for the card to
    /// mention. A whole-network scope gives the search alone, or "*"; a
    /// Spotlight with no devices gives a null query - nothing to search.
    /// </summary>
    /// <param name="search">The card's search in Graylog syntax, or blank for everything.</param>
    /// <param name="field">Graylog's field holding the source address (<c>GraylogSettings.QueryField</c>); blank is "source".</param>
    /// <param name="scope">The worked-out Spotlight.</param>
    /// <param name="devices">The app's device list, to find each device's addresses.</param>
    public static GraylogSpotlightQuery ForSpotlight(string? search, string? field, Spotlights.SpotlightScope scope, IEnumerable<Device> devices)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(devices);

        var trimmed = search?.Trim();
        var hasSearch = !string.IsNullOrEmpty(trimmed) && trimmed != "*";
        if (scope.IsWholeNetwork)
        {
            return new GraylogSpotlightQuery(hasSearch ? trimmed : "*", 0);
        }

        var byId = devices.GroupBy(d => d.DeviceId).ToDictionary(g => g.Key, g => g.First());
        var inScope = scope.DeviceIds.Where(byId.ContainsKey).OrderBy(id => id).Select(id => byId[id]).ToList();
        if (inScope.Count == 0)
        {
            return new GraylogSpotlightQuery(null, 0);
        }

        var prefix = hasSearch ? "(" + trimmed + ") AND " : string.Empty;
        var queryField = string.IsNullOrWhiteSpace(field) ? "source" : field.Trim();
        var addresses = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var included = 0;

        foreach (var device in inScope)
        {
            var added = DeviceAddresses(device, null, null).Where(a => !seen.Contains(a)).ToList();
            if (added.Count == 0)
            {
                included++;
                continue;
            }

            var candidate = Sources(queryField, addresses.Concat(added));
            if (included > 0 && prefix.Length + candidate.Length > MaxQueryLength)
            {
                break;
            }

            addresses.AddRange(added);
            seen.UnionWith(added);
            included++;
        }

        // Every device in scope had no address at all: nothing could match.
        if (addresses.Count == 0)
        {
            return new GraylogSpotlightQuery(null, 0);
        }

        return new GraylogSpotlightQuery(prefix + Sources(queryField, addresses), inScope.Count - included);
    }

    private static string Sources(string field, IEnumerable<string> addresses)
        => field + ": (\"" + string.Join("\" OR \"", addresses.Select(Escape)) + "\")";

    /// <summary>The stream filter LibreNMS passes: "streams:{id}", or null for every stream.</summary>
    public static string? StreamFilter(string? streamId) =>
        string.IsNullOrWhiteSpace(streamId) ? null : "streams:" + streamId;

    /// <summary>
    /// The addresses a device's messages might carry in the query field,
    /// in LibreNMS's order: its hostname resolved to an IPv4 address, its
    /// hostname, its display name, its IP and its sysName - then, with
    /// "match any address" on, every IPv4/IPv6 address on its interfaces
    /// except loopback. Blanks and repeats are dropped.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="resolvedHostname">
    /// The hostname resolved to an IPv4 address, or null if it didn't resolve
    /// (PHP's <c>gethostbyname</c> hands the hostname back then, which the
    /// de-duplication drops anyway).
    /// </param>
    /// <param name="interfaceAddresses">The device's interface addresses when "match any address" is on, otherwise null.</param>
    public static IReadOnlyList<string> DeviceAddresses(
        Device device,
        string? resolvedHostname,
        IEnumerable<DeviceIpAddress>? interfaceAddresses)
    {
        ArgumentNullException.ThrowIfNull(device);

        var candidates = new List<string?>
        {
            resolvedHostname,
            device.Hostname,

            // LibreNMS's displayName(): the display name, or the hostname.
            string.IsNullOrWhiteSpace(device.Display) ? device.Hostname : device.Display,
            device.Ip,
            device.SysName,
        };

        if (interfaceAddresses is not null)
        {
            foreach (var address in interfaceAddresses)
            {
                if (address.IsIpv6)
                {
                    // LibreNMS matches the expanded form it stores; the
                    // compressed form is what syslog senders usually use, so
                    // both are included.
                    if (address.Ipv6Address != Ipv6LoopbackExpanded)
                    {
                        candidates.Add(address.Ipv6Address);
                    }

                    if (address.Ipv6Compressed != Ipv6LoopbackCompressed)
                    {
                        candidates.Add(address.Ipv6Compressed);
                    }
                }
                else if (address.Ipv4Address != Ipv4Loopback)
                {
                    candidates.Add(address.Ipv4Address);
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate.Trim()))
            {
                result.Add(candidate.Trim());
            }
        }

        return result;
    }

    /// <summary>Syslog severity names, as LibreNMS labels them (<c>lang/en/syslog.php</c>).</summary>
    public static string SeverityName(int level) => level switch
    {
        0 => "Emergency",
        1 => "Alert",
        2 => "Critical",
        3 => "Error",
        4 => "Warning",
        5 => "Notice",
        6 => "Informational",
        7 => "Debug",
        _ => level.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"(6) Informational", LibreNMS's Level column; blank when the message has no level.</summary>
    public static string LevelText(int? level) =>
        level is { } value and >= 0
            ? $"({value.ToString(CultureInfo.InvariantCulture)}) {SeverityName(value)}"
            : string.Empty;

    /// <summary>
    /// "(4) security/authorization messages" for a numeric facility, with
    /// LibreNMS's names; a facility Graylog already sent as a name is shown
    /// as it is.
    /// </summary>
    public static string FacilityText(string? facility)
    {
        if (string.IsNullOrWhiteSpace(facility))
        {
            return string.Empty;
        }

        if (!int.TryParse(facility, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return facility;
        }

        var name = number switch
        {
            0 => "kernel messages",
            1 => "user-level messages",
            2 => "mail-system",
            3 => "system daemons",
            4 => "security/authorization messages",
            5 => "messages generated internally by syslogd",
            6 => "line printer subsystem",
            7 => "network news subsystem",
            8 => "UUCP subsystem",
            9 => "clock daemon",
            10 => "security/authorization messages",
            11 => "FTP daemon",
            12 => "NTP subsystem",
            13 => "log audit",
            14 => "log alert",
            15 => "clock daemon (note 2)",
            >= 16 and <= 23 => $"local use {number - 16} (local{number - 16})",
            _ => null,
        };

        return name is null ? facility : $"({number.ToString(CultureInfo.InvariantCulture)}) {name}";
    }

    /// <summary>
    /// A Windows or IANA time zone name (LibreNMS takes IANA names, e.g.
    /// "Europe/London"), or null when blank or unknown.
    /// </summary>
    public static TimeZoneInfo? FindTimeZone(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>A message time in the configured zone, or this PC's local time when none is set.</summary>
    public static string FormatTimestamp(DateTimeOffset timestamp, TimeZoneInfo? zone)
    {
        var shown = zone is null ? timestamp.ToLocalTime() : TimeZoneInfo.ConvertTime(timestamp, zone);
        return shown.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}

/// <summary>A Spotlight card's Graylog query (see <see cref="GraylogQuery.ForSpotlight"/>).</summary>
/// <param name="Query">The query, or null when the Spotlight has no devices to search for.</param>
/// <param name="DevicesLeftOut">Devices that didn't fit under <see cref="GraylogQuery.MaxQueryLength"/>.</param>
public sealed record GraylogSpotlightQuery(string? Query, int DevicesLeftOut);
