using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Search;

/// <summary>What one search term looks like.</summary>
public enum SearchTermKind
{
    Ipv4Address,
    Ipv6Address,
    Ipv4Network,
    Ipv6Network,
    MacAddress,

    /// <summary>Anything else: a hostname, device name or sysName.</summary>
    Name,
}

/// <summary>One thing to look up, classified.</summary>
/// <param name="Text">The term as cleaned, for showing back.</param>
/// <param name="Kind">What it looks like.</param>
/// <param name="Address">The address, or the network's base address, for IP kinds.</param>
/// <param name="PrefixLength">The prefix length for network kinds.</param>
/// <param name="Mac">Twelve lowercase hex digits for <see cref="SearchTermKind.MacAddress"/>.</param>
public sealed record SearchTerm(string Text, SearchTermKind Kind, IPAddress? Address = null, int? PrefixLength = null, string? Mac = null)
{
    public bool IsAddress => Kind is SearchTermKind.Ipv4Address or SearchTermKind.Ipv6Address;

    public bool IsNetwork => Kind is SearchTermKind.Ipv4Network or SearchTermKind.Ipv6Network;
}

/// <summary>
/// A Network Search query (#91), read the way LibreNMS 26.11's Network Search
/// page reads one (librenms#21113): an IPv4 or IPv6 address, a CIDR network,
/// a MAC address in any common form, a name, or several addresses, MACs or
/// hostnames pasted at once. Text copied from logs and configs is cleaned
/// first - quotes, brackets, "ip=" or "mac:" prefixes, a port after an address.
/// An empty query is valid: it asks for a bounded overview.
/// </summary>
public sealed partial class NetworkSearchQuery
{
    /// <summary>The most terms one pasted list is read as; the rest are counted in <see cref="IgnoredTerms"/>.</summary>
    public const int MaxTerms = 50;

    private NetworkSearchQuery(string text, IReadOnlyList<SearchTerm> terms, int ignoredTerms)
    {
        Text = text;
        Terms = terms;
        IgnoredTerms = ignoredTerms;
    }

    /// <summary>The query as typed, trimmed.</summary>
    public string Text { get; }

    /// <summary>What to look up, in the order given. Empty for an overview.</summary>
    public IReadOnlyList<SearchTerm> Terms { get; }

    /// <summary>Terms past <see cref="MaxTerms"/> in a pasted list, which aren't looked up.</summary>
    public int IgnoredTerms { get; }

    public bool IsEmpty => Terms.Count == 0;

    public static NetworkSearchQuery Parse(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        var tokens = Splitter().Split(trimmed).Where(t => t.Length > 0).ToList();
        var terms = tokens.Select(ParseTerm).Where(t => t.Text.Length > 0).ToList();

        // A list only when every piece is unmistakably one thing; otherwise
        // "core switch" is one name with a space in it.
        if (terms.Count > 1 && terms.All(t => t.Kind != SearchTermKind.Name || LooksLikeHostname(t.Text)))
        {
            var distinct = terms.DistinctBy(Identity).ToList();
            return new NetworkSearchQuery(trimmed, distinct.Take(MaxTerms).ToList(), Math.Max(0, distinct.Count - MaxTerms));
        }

        var single = ParseTerm(trimmed);
        return new NetworkSearchQuery(trimmed, single.Text.Length == 0 ? [] : [single], 0);
    }

    /// <summary>One term, classified.</summary>
    public static SearchTerm ParseTerm(string text)
    {
        var value = Clean(text);
        if (value.Length == 0)
        {
            return new SearchTerm(value, SearchTermKind.Name);
        }

        if (MacAddresses.Normalise(value) is { } mac)
        {
            return new SearchTerm(value, SearchTermKind.MacAddress, Mac: mac);
        }

        var slash = value.IndexOf('/');
        if (slash > 0 && int.TryParse(value[(slash + 1)..], out var prefix) && TryAddress(value[..slash], out var network))
        {
            var max = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (prefix >= 0 && prefix <= max)
            {
                var kind = network.AddressFamily == AddressFamily.InterNetwork ? SearchTermKind.Ipv4Network : SearchTermKind.Ipv6Network;
                return new SearchTerm(value, kind, NetworkBase(network, prefix), prefix);
            }
        }

        if (TryAddress(value, out var address))
        {
            var kind = address.AddressFamily == AddressFamily.InterNetwork ? SearchTermKind.Ipv4Address : SearchTermKind.Ipv6Address;
            return new SearchTerm(address.ToString(), kind, address);
        }

        return new SearchTerm(value, SearchTermKind.Name);
    }

    /// <summary>
    /// Strips what comes along when copying from logs, configs or tickets:
    /// quotes, brackets, a field name (ip=, mac:), a trailing port.
    /// </summary>
    public static string Clean(string text)
    {
        var value = text.Trim().Trim('"', '\'', '`', '(', ')', '{', '}', '<', '>', ',', ';');
        value = FieldName().Replace(value, string.Empty);
        value = BracketedIpv6().Replace(value, "$1");
        value = value.Trim().Trim('"', '\'', '`', '[', ']');
        return Ipv4WithPort().Replace(value, "$1");
    }

    /// <summary>Whether <paramref name="address"/> falls inside <paramref name="network"/>/<paramref name="prefixLength"/>.</summary>
    public static bool Contains(IPAddress network, int prefixLength, IPAddress address)
    {
        if (network.AddressFamily != address.AddressFamily)
        {
            return false;
        }

        return NetworkBase(address, prefixLength).Equals(NetworkBase(network, prefixLength));
    }

    /// <summary>Reads "192.0.2.0/24" or "2001:db8::/64".</summary>
    public static bool TryParseNetwork(string? cidr, out IPAddress network, out int prefixLength)
    {
        network = IPAddress.None;
        prefixLength = 0;
        var slash = cidr?.IndexOf('/') ?? -1;
        if (slash <= 0 || !int.TryParse(cidr![(slash + 1)..], out prefixLength) || !TryAddress(cidr[..slash], out var parsed))
        {
            return false;
        }

        network = NetworkBase(parsed, prefixLength);
        return true;
    }

    private static bool TryAddress(string text, out IPAddress address)
    {
        // IPAddress.TryParse accepts "1" or "10.1" as shorthand; a search means
        // a dotted quad, or an address with colons.
        address = IPAddress.None;
        if (!(Ipv4().IsMatch(text) || (text.Contains(':') && !text.Contains(' '))) || !IPAddress.TryParse(text, out var parsed))
        {
            return false;
        }

        address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            address.ScopeId = 0;
        }

        return true;
    }

    private static IPAddress NetworkBase(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefixLength - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }

        return new IPAddress(bytes);
    }

    private static string Identity(SearchTerm term) => term.Kind switch
    {
        SearchTermKind.MacAddress => "mac:" + term.Mac,
        SearchTermKind.Name => "name:" + term.Text.ToLowerInvariant(),
        _ => $"ip:{term.Address}/{term.PrefixLength}",
    };

    private static bool LooksLikeHostname(string text) => Hostname().IsMatch(text);

    [GeneratedRegex(@"[\s,;]+")]
    private static partial Regex Splitter();

    [GeneratedRegex(@"^(ip|ipv4|ipv6|addr|address|mac|hwaddr|ether|lladdr|host|hostname)\s*[=:]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex FieldName();

    [GeneratedRegex(@"^\[([0-9a-fA-F:.]+)\](:\d+)?$")]
    private static partial Regex BracketedIpv6();

    [GeneratedRegex(@"^(\d{1,3}(?:\.\d{1,3}){3}):\d+$")]
    private static partial Regex Ipv4WithPort();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")]
    private static partial Regex Ipv4();

    /// <summary>A dotted name, the kind someone pastes a list of.</summary>
    [GeneratedRegex(@"^(?=.*[a-zA-Z])[a-zA-Z0-9-]+(\.[a-zA-Z0-9-]+)+$")]
    private static partial Regex Hostname();
}
