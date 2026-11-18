using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One row from /api/v0/resources/ip/networks/{ipv4|ipv6} - a subnet
/// LibreNMS has seen configured on a monitored interface. The two families
/// come from separate tables with their own field names.
/// </summary>
public sealed class IpNetwork
{
    [JsonPropertyName("ipv4_network_id")]
    public int? Ipv4NetworkId { get; set; }

    /// <summary>"192.0.2.0/24".</summary>
    [JsonPropertyName("ipv4_network")]
    public string? Ipv4Network { get; set; }

    [JsonPropertyName("ipv6_network_id")]
    public int? Ipv6NetworkId { get; set; }

    /// <summary>"2001:db8::/64".</summary>
    [JsonPropertyName("ipv6_network")]
    public string? Ipv6Network { get; set; }

    [JsonPropertyName("context_name")]
    public string? ContextName { get; set; }

    [JsonIgnore]
    public int Id => Ipv4NetworkId ?? Ipv6NetworkId ?? 0;

    /// <summary>The network in CIDR notation, whichever family this row is.</summary>
    [JsonIgnore]
    public string? Cidr => Ipv4Network ?? Ipv6Network;
}

/// <summary>
/// One row from /api/v0/resources/ip/networks/{id}/ip - an interface address
/// inside a network, on a monitored device's port.
/// </summary>
public sealed class IpNetworkAddress
{
    [JsonPropertyName("port_id")]
    public int PortId { get; set; }

    [JsonPropertyName("ipv4_address")]
    public string? Ipv4Address { get; set; }

    [JsonPropertyName("ipv4_prefixlen")]
    public int? Ipv4PrefixLength { get; set; }

    [JsonPropertyName("ipv6_address")]
    public string? Ipv6Address { get; set; }

    [JsonPropertyName("ipv6_compressed")]
    public string? Ipv6Compressed { get; set; }

    [JsonPropertyName("ipv6_prefixlen")]
    public int? Ipv6PrefixLength { get; set; }

    [JsonPropertyName("context_name")]
    public string? ContextName { get; set; }

    /// <summary>The address alone, compressed for IPv6.</summary>
    [JsonIgnore]
    public string? Address => Ipv4Address ?? Ipv6Compressed ?? Ipv6Address;

    [JsonIgnore]
    public int? PrefixLength => Ipv4Address is not null ? Ipv4PrefixLength : Ipv6PrefixLength;
}
