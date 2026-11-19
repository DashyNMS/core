using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Search;

/// <summary>Where Network Search looks.</summary>
public enum SearchSource
{
    /// <summary>Routers' IPv4 ARP tables (<c>resources/ip/arp</c>).</summary>
    Arp,

    /// <summary>IPv6 neighbour discovery. LibreNMS keeps it, but its API doesn't expose it.</summary>
    NeighbourDiscovery,

    /// <summary>Switches' forwarding tables (<c>resources/fdb</c>, <c>ports/mac</c>).</summary>
    ForwardingTable,

    /// <summary>Monitored interface addresses and their networks (<c>resources/ip/networks</c>).</summary>
    Networks,

    /// <summary>The device list, for names and management addresses.</summary>
    Devices,

    /// <summary>Discovered neighbour links, which mark uplinks (<c>resources/links</c>).</summary>
    Links,

    /// <summary>LibreNMS's OUI table, naming a MAC's vendor.</summary>
    Vendors,
}

/// <summary>What a source said - kept apart so "not found" is never claimed when a source couldn't be asked.</summary>
public enum SourceState
{
    /// <summary>It answered and had something.</summary>
    Found,

    /// <summary>It answered and had nothing: a real negative.</summary>
    NoEntry,

    /// <summary>Not asked for this query - not relevant, or left for opening one result.</summary>
    NotQueried,

    /// <summary>The account may not read it.</summary>
    PermissionDenied,

    /// <summary>The server doesn't offer it: an older LibreNMS, or not in the API at all.</summary>
    Unavailable,

    /// <summary>Asking failed - a timeout or server error.</summary>
    Failed,
}

/// <summary>One source's part in a search.</summary>
/// <param name="Source">The source.</param>
/// <param name="State">What it said.</param>
/// <param name="Detail">Why, in a sentence, where the state alone doesn't say.</param>
/// <param name="Shown">How many of its entries the result uses, when it had to stop short.</param>
/// <param name="Total">How many it had, when known.</param>
public sealed record SourceCoverage(SearchSource Source, SourceState State, string? Detail = null, int? Shown = null, int? Total = null)
{
    /// <summary>The source had more than the result shows.</summary>
    public bool IsTruncated => Shown is { } shown && Total is { } total && total > shown;

    /// <summary>The source's answer can be trusted as complete for what it covers.</summary>
    public bool IsComplete => State is SourceState.Found or SourceState.NoEntry && !IsTruncated;
}

/// <summary>How a forwarding-table location relates to the endpoint.</summary>
public enum LocationKind
{
    /// <summary>The only MAC on an access port: the endpoint plugs in here.</summary>
    Direct,

    /// <summary>One of several MACs on an access port: behind a hub, phone or small switch.</summary>
    Shared,

    /// <summary>A link to another switch or an aggregate: seen in passing, not connected here.</summary>
    Uplink,

    /// <summary>An access port whose MAC count couldn't be read, so direct and shared can't be told apart.</summary>
    Unknown,
}

/// <summary>A switch port that has learned one of the endpoint's MACs.</summary>
/// <param name="Mac">Which MAC, twelve hex digits.</param>
/// <param name="DeviceId">The switch.</param>
/// <param name="PortId">The port.</param>
/// <param name="Device">The switch, when the account can see it.</param>
/// <param name="Port">The port, when the account can see it.</param>
/// <param name="VlanNumber">The 802.1Q VLAN it was learned on; null when unknown.</param>
/// <param name="VlanName">The VLAN's name, when known.</param>
/// <param name="MacCount">How many MACs the port has learned in all; null when unknown.</param>
/// <param name="IsUplink">The port has a discovered neighbour link (LLDP, CDP and so on).</param>
/// <param name="IsAggregate">The port is a link aggregate.</param>
/// <param name="UpdatedAt">When the switch last reported it.</param>
/// <param name="IsStale">Older than a day behind the endpoint's newest sighting.</param>
public sealed record EndpointLocation(
    string Mac,
    int DeviceId,
    int PortId,
    Device? Device,
    Port? Port,
    int? VlanNumber,
    string? VlanName,
    int? MacCount,
    bool IsUplink,
    bool IsAggregate,
    DateTime? UpdatedAt,
    bool IsStale)
{
    public LocationKind Kind => IsUplink || IsAggregate ? LocationKind.Uplink
        : MacCount switch { null => LocationKind.Unknown, 1 => LocationKind.Direct, _ => LocationKind.Shared };
}

/// <summary>A router interface that has resolved the endpoint's address to a MAC.</summary>
/// <param name="Source">"ARP".</param>
/// <param name="Address">The endpoint's address in that entry.</param>
/// <param name="Mac">The MAC it resolved to.</param>
/// <param name="DeviceId">The router.</param>
/// <param name="PortId">Its interface.</param>
/// <param name="Device">The router, when visible.</param>
/// <param name="Port">The interface, when visible.</param>
public sealed record EndpointNeighbour(string Source, string Address, string Mac, int DeviceId, int PortId, Device? Device, Port? Port);

/// <summary>One of the endpoint's MAC addresses.</summary>
/// <param name="Hex">Twelve lowercase hex digits; format with <see cref="MacAddresses.Format"/>.</param>
/// <param name="Vendor">The OUI vendor, when LibreNMS knows it and it was asked.</param>
/// <param name="LastSeen">The newest forwarding-table sighting; ARP has no timestamps.</param>
/// <param name="IsStale">Last seen more than a day before the endpoint's newest MAC.</param>
public sealed record EndpointMac(string Hex, string? Vendor, DateTime? LastSeen, bool IsStale);

/// <summary>A monitored interface address on the endpoint's subnet - a gateway or a neighbour.</summary>
public sealed record SubnetInterface(string Address, int? PrefixLength, int PortId, Device? Device, Port? Port);

/// <summary>The network the endpoint's address sits in, and the monitored interfaces on it.</summary>
/// <param name="Network">"192.0.2.0/24".</param>
/// <param name="Interfaces">Up to the page limit, gateways first (the lowest address).</param>
/// <param name="TotalInterfaces">All of them, for "+N more".</param>
public sealed record EndpointSubnet(string Network, IReadOnlyList<SubnetInterface> Interfaces, int TotalInterfaces);

/// <summary>Something about the endpoint that doesn't add up.</summary>
public enum EndpointConflictKind
{
    /// <summary>One IP address resolves to several MACs: a duplicate address, failover pair or spoofing.</summary>
    AddressWithSeveralMacs,

    /// <summary>One MAC is learned on several access ports at once: a loop, a moved host or a cloned MAC.</summary>
    MacOnSeveralPorts,
}

/// <summary>A conflict, with the values involved (MACs for an address, port ids for a MAC).</summary>
public sealed record EndpointConflict(EndpointConflictKind Kind, string Subject, IReadOnlyList<string> Values);

/// <summary>
/// One endpoint: everything Network Search found about one host, keyed by
/// its addresses and MACs, the way LibreNMS 26.11 builds its host record.
/// </summary>
public sealed class EndpointRecord
{
    /// <summary>The first address, else the first MAC, else the device name.</summary>
    public required string Title { get; init; }

    /// <summary>IP addresses, compressed, the searched one first.</summary>
    public required IReadOnlyList<string> Addresses { get; init; }

    public required IReadOnlyList<EndpointMac> Macs { get; init; }

    /// <summary>Names it is monitored under - hostname, sysName, display name.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>The monitored device whose address this is, if any.</summary>
    public Device? MonitoredDevice { get; init; }

    /// <summary>The monitored interface that holds this address, if any and if the account may read it.</summary>
    public Port? MonitoredInterface { get; init; }

    /// <summary>
    /// The port id of the monitored interface holding this address - set even
    /// when the port itself couldn't be read, so the endpoint still counts as monitored.
    /// </summary>
    public int? MonitoredInterfacePortId { get; init; }

    /// <summary>Switch ports that learned its MACs, closest first: access ports before uplinks, fewest MACs first, newest first.</summary>
    public required IReadOnlyList<EndpointLocation> Locations { get; init; }

    /// <summary>Router interfaces whose ARP tables hold it.</summary>
    public required IReadOnlyList<EndpointNeighbour> Neighbours { get; init; }

    public EndpointSubnet? Subnet { get; init; }

    public required IReadOnlyList<EndpointConflict> Conflicts { get; init; }

    /// <summary>Whether this record was looked up in full. A list's rows aren't: open one to see its switch ports and subnet.</summary>
    public bool IsDetailed { get; init; }

    /// <summary>The port the endpoint most likely plugs into: the first current location that isn't an uplink.</summary>
    public EndpointLocation? BestLocation => Locations.FirstOrDefault(l => !l.IsStale && l.Kind != LocationKind.Uplink);

    public bool IsMonitored => MonitoredDevice is not null || MonitoredInterface is not null || MonitoredInterfacePortId is not null;
}

/// <summary>What kind of answer a search gave.</summary>
public enum NetworkSearchOutcome
{
    /// <summary>No query: a bounded list of known endpoints.</summary>
    Overview,

    /// <summary>The query identified one endpoint.</summary>
    Single,

    /// <summary>Several endpoints matched.</summary>
    List,

    /// <summary>Nothing matched in the sources that could be asked - check <see cref="NetworkSearchResult.Coverage"/> before calling it absent.</summary>
    NotFound,
}

/// <summary>The answer to one search.</summary>
public sealed class NetworkSearchResult
{
    public required NetworkSearchQuery Query { get; init; }

    public required NetworkSearchOutcome Outcome { get; init; }

    /// <summary>This page of endpoints.</summary>
    public required IReadOnlyList<EndpointRecord> Endpoints { get; init; }

    /// <summary>How many endpoints matched in all, for "+N more" and paging.</summary>
    public required int Total { get; init; }

    /// <summary>Where this page starts.</summary>
    public int Offset { get; init; }

    /// <summary>What each source said. A source missing here wasn't relevant.</summary>
    public required IReadOnlyList<SourceCoverage> Coverage { get; init; }

    /// <summary>Endpoints beyond this page.</summary>
    public int Remaining => Math.Max(0, Total - Offset - Endpoints.Count);

    public SourceCoverage? CoverageOf(SearchSource source) => Coverage.FirstOrDefault(c => c.Source == source);

    /// <summary>
    /// True when nothing was found and every relevant source answered - a
    /// trustworthy "not on the network". False when a source was denied,
    /// unavailable or failed, so the absence may just be a blind spot.
    /// </summary>
    public bool IsConfirmedAbsent => Outcome == NetworkSearchOutcome.NotFound
                                     && Query.IgnoredTerms == 0
                                     && Coverage.Where(c => c.State != SourceState.NotQueried || c.IsTruncated)
                                         .All(c => c.IsComplete);
}
