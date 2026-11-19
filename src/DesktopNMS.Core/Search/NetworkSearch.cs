using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Search;

/// <summary>Limits for one search. The defaults suit an interactive page.</summary>
public sealed record NetworkSearchOptions
{
    /// <summary>Endpoints per page.</summary>
    public int PageSize { get; init; } = 25;

    /// <summary>API calls in flight at once, so one search can't swamp the server.</summary>
    public int MaxConcurrentRequests { get; init; } = 4;

    /// <summary>MACs looked up in full for one endpoint; more means a shared address, and the rest are counted, not fetched.</summary>
    public int MaxMacsPerEndpoint { get; init; } = 8;

    /// <summary>Subnet interfaces named per endpoint; the rest are counted for "+N more".</summary>
    public int MaxSubnetInterfaces { get; init; } = 10;

    /// <summary>Router and switch ports named per endpoint (each is one request).</summary>
    public int MaxNamedPorts { get; init; } = 40;

    /// <summary>A sighting more than this behind the endpoint's newest is stale, as upstream's one day.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromDays(1);
}

/// <summary>
/// Network Search (#91): finds an endpoint by address, network, MAC or name
/// and reports where it sits, the way LibreNMS 26.11's Network Search page
/// does (librenms#21113, librenms#21159), composed from the API Core already
/// uses. UI-free and shared, so Desktop and Mobile match results alike.
/// </summary>
/// <remarks>
/// <para>
/// One endpoint is one host: its IPs and MACs, the router interfaces that
/// resolved it (ARP), the switch ports that learned it (forwarding tables)
/// with uplinks marked, its subnet and the monitored interfaces on it, and
/// conflicts. When a query matches several endpoints the list's rows carry
/// what ARP said; opening one (searching its address) looks it up in full.
/// </para>
/// <para>
/// Every source reports its own <see cref="SourceCoverage"/>: a source that
/// was denied, unavailable or failed is never shown as "nothing found".
/// IPv6 neighbour discovery is always <see cref="SourceState.Unavailable"/>:
/// LibreNMS's API has no route for it, so IPv6 endpoints are found through
/// monitored addresses only.
/// </para>
/// <para>
/// A search makes a bounded number of requests, at most
/// <see cref="NetworkSearchOptions.MaxConcurrentRequests"/> at a time, and
/// honours cancellation. It raises no events and touches no UI: the caller
/// awaits the result and marshals it as it needs. An expired or revoked
/// token is thrown (<see cref="LibreNmsApiException.IsAuthenticationFailure"/>)
/// rather than recorded, so an app can send the user to sign in. Paging
/// re-runs the search: each page asks the sources again, so a page is as
/// current as its request.
/// </para>
/// </remarks>
public sealed class NetworkSearch
{
    private const string NoNeighbourDiscovery =
        "LibreNMS's API doesn't expose IPv6 neighbour discovery, so IPv6 hosts are found only through monitored addresses.";

    private readonly IDevicesApi _devices;
    private readonly IArpApi _arp;
    private readonly IFdbApi _fdb;
    private readonly IPortsApi _ports;
    private readonly IVlansApi _vlans;
    private readonly ILinksApi _links;
    private readonly IIpNetworksApi _networks;
    private readonly NetworkSearchOptions _options;

    public NetworkSearch(ILibreNmsClient client, NetworkSearchOptions? options = null)
        : this(NotNull(client).Devices, client.Arp, client.Fdb, client.Ports, client.Vlans, client.Links, client.Networks, options)
    {
    }

    /// <summary>Over Core's resource APIs on any transport - how a host's tests answer from recorded JSON.</summary>
    public NetworkSearch(LibreNmsResourceApis apis, NetworkSearchOptions? options = null)
        : this(NotNull(apis).Devices, apis.Arp, apis.Fdb, apis.Ports, apis.Vlans, apis.Links, apis.Networks, options)
    {
    }

    private NetworkSearch(
        IDevicesApi devices, IArpApi arp, IFdbApi fdb, IPortsApi ports, IVlansApi vlans, ILinksApi links, IIpNetworksApi networks, NetworkSearchOptions? options)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        _arp = arp;
        _fdb = fdb;
        _ports = ports;
        _vlans = vlans;
        _links = links;
        _networks = networks;
        _options = options ?? new NetworkSearchOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.PageSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxConcurrentRequests, 1);
    }

    /// <summary>Searches for <paramref name="query"/> (see <see cref="NetworkSearchQuery"/>), returning the page starting at <paramref name="offset"/>.</summary>
    public Task<NetworkSearchResult> SearchAsync(string? query, int offset = 0, CancellationToken cancellationToken = default)
        => SearchAsync(NetworkSearchQuery.Parse(query), offset, cancellationToken);

    public async Task<NetworkSearchResult> SearchAsync(NetworkSearchQuery query, int offset = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var run = new Run(_options, cancellationToken);
        var devices = await run.Ask(SearchSource.Devices, ct => _devices.ListAsync(ct), []).ConfigureAwait(false);
        run.Devices = devices.ToDictionary(d => d.DeviceId);

        var candidates = new List<Candidate>();
        if (query.IsEmpty)
        {
            var arp = Clean(await run.Ask(SearchSource.Arp, ct => _arp.ListAllAsync(ct), []).ConfigureAwait(false));
            candidates.AddRange(FromArp(arp));
            run.NotQueried(SearchSource.ForwardingTable, "Search for an address or MAC to see where it is connected.");
        }
        else
        {
            foreach (var term in query.Terms)
            {
                candidates.AddRange(await FindAsync(run, term, devices).ConfigureAwait(false));
            }
        }

        // Pasted terms past the limit are reported by query.IgnoredTerms, not as coverage.
        cancellationToken.ThrowIfCancellationRequested();
        candidates = Merge(candidates);
        cancellationToken.ThrowIfCancellationRequested();

        var page = candidates.Skip(offset).Take(_options.PageSize).ToList();
        IReadOnlyList<EndpointRecord> records;
        if (candidates.Count == 1 && offset == 0)
        {
            var detail = await DetailAsync(run, page[0]).ConfigureAwait(false);

            // A MAC nobody has seen anywhere is not an endpoint, however it was typed.
            if (!HasEvidence(detail))
            {
                candidates.Clear();
            }

            records = candidates.Count == 0 ? [] : [detail];
        }
        else
        {
            records = page.Select(c => Summary(run, c)).ToList();
            if (candidates.Count > 1)
            {
                run.NotQueried(SearchSource.ForwardingTable, "Open a result to see the switch ports it is learned on.");
            }
        }

        var outcome = query.IsEmpty ? NetworkSearchOutcome.Overview
            : candidates.Count == 0 ? NetworkSearchOutcome.NotFound
            : candidates.Count == 1 ? NetworkSearchOutcome.Single
            : NetworkSearchOutcome.List;

        return new NetworkSearchResult
        {
            Query = query,
            Outcome = outcome,
            Endpoints = records,
            Total = candidates.Count,
            Offset = offset,
            Coverage = run.Coverage(),
        };
    }

    /// <summary>The endpoints one term points at: addresses and MACs, not yet looked up in full.</summary>
    private async Task<IReadOnlyList<Candidate>> FindAsync(Run run, SearchTerm term, IReadOnlyList<Device> devices)
    {
        switch (term.Kind)
        {
            case SearchTermKind.Ipv4Address:
            {
                var arp = Clean(await run.Ask(SearchSource.Arp, ct => _arp.FindByAddressAsync(term.Address!, ct), []).ConfigureAwait(false));
                var found = FromArp(arp).ToList();
                if (found.Count == 0 && (IsKnownAddress(devices, term.Address!) || await IsMonitoredAddressAsync(run, term.Address!).ConfigureAwait(false)))
                {
                    found.Add(new Candidate([term.Address!.ToString()], [], [], null));
                }

                return found;
            }

            case SearchTermKind.Ipv6Address:
                run.Unavailable(SearchSource.NeighbourDiscovery, NoNeighbourDiscovery);
                return IsKnownAddress(devices, term.Address!) || await IsMonitoredAddressAsync(run, term.Address!).ConfigureAwait(false)
                    ? [new Candidate([Compressed(term.Address!)], [], [], null)]
                    : [];

            case SearchTermKind.Ipv4Network:
            {
                var arp = Clean(await run.Ask(SearchSource.Arp, ct => _arp.FindInNetworkAsync(term.Address!, term.PrefixLength!.Value, ct), []).ConfigureAwait(false));
                var inNetwork = FromArp(arp).ToList();
                inNetwork.AddRange(DevicesIn(devices, term).Where(d => inNetwork.All(c => !c.Addresses.Contains(d.Ip!))).Select(d => new Candidate([d.Ip!], [], [], d)));
                await AddMonitoredInterfacesAsync(run, term, inNetwork).ConfigureAwait(false);
                return inNetwork.OrderBy(c => AddressKey(c.Addresses.FirstOrDefault())).ToList();
            }

            case SearchTermKind.Ipv6Network:
            {
                run.Unavailable(SearchSource.NeighbourDiscovery, NoNeighbourDiscovery);
                var inNetwork = DevicesIn(devices, term).Select(d => new Candidate([Compressed(IPAddress.Parse(d.Ip!))], [], [], d)).ToList();
                await AddMonitoredInterfacesAsync(run, term, inNetwork).ConfigureAwait(false);
                return inNetwork.OrderBy(c => AddressKey(c.Addresses.FirstOrDefault())).ToList();
            }

            case SearchTermKind.MacAddress:
            {
                var arp = Clean(await run.Ask(SearchSource.Arp, ct => _arp.FindByMacAsync(term.Mac!, ct), []).ConfigureAwait(false));

                // A MAC no router resolved is only an endpoint if a switch has learned it.
                if (arp.Count == 0
                    && (await LearnedAsync(run, term.Mac!).ConfigureAwait(false)).Count == 0
                    && (await LearnedPortsAsync(run, term.Mac!).ConfigureAwait(false)).Count == 0)
                {
                    return [];
                }

                var addresses = arp.Select(a => a.Ipv4Address).OfType<string>().Distinct().ToList();
                return [new Candidate(addresses, [term.Mac!], arp.ToList(), null)];
            }

            default:
            {
                var matches = MatchNames(devices, term.Text);
                return matches.Select(d => new Candidate(d.Ip is { Length: > 0 } ip ? [ip] : [], [], [], d)).ToList();
            }
        }
    }

    /// <summary>A list row: what ARP and the device list said, without the per-endpoint lookups.</summary>
    private static EndpointRecord Summary(Run run, Candidate candidate)
    {
        var neighbours = candidate.Arp
            .Where(a => a.MacAddress is not null && a.Ipv4Address is not null)
            .Select(a => new EndpointNeighbour("ARP", a.Ipv4Address!, a.MacAddress!, a.DeviceId, a.PortId, run.DeviceOf(a.DeviceId), null))
            .ToList();
        var device = candidate.Device ?? candidate.Addresses.Select(run.DeviceAt).FirstOrDefault(d => d is not null);
        var macs = candidate.Macs.Concat(neighbours.Select(n => n.Mac)).Distinct().ToList();

        return new EndpointRecord
        {
            Title = Title(candidate.Addresses, macs, device),
            Addresses = candidate.Addresses,
            Macs = macs.Select(m => new EndpointMac(m, null, null, false)).ToList(),
            Names = Names(device),
            MonitoredDevice = device,
            MonitoredInterfacePortId = candidate.InterfacePortId,
            Locations = [],
            Neighbours = neighbours,
            Conflicts = AddressConflicts(neighbours),
        };
    }

    /// <summary>Everything about one endpoint.</summary>
    private async Task<EndpointRecord> DetailAsync(Run run, Candidate candidate)
    {
        var addresses = candidate.Addresses.ToList();
        var arp = candidate.Arp.ToList();

        // Found by name or management address alone: ask ARP for it, so the
        // record is the same however the endpoint was found.
        if (arp.Count == 0 && candidate.Macs.Count == 0 && addresses.Count > 0
            && IPAddress.TryParse(addresses[0], out var first) && first.AddressFamily == AddressFamily.InterNetwork)
        {
            arp.AddRange(Clean(await run.Ask(SearchSource.Arp, ct => _arp.FindByAddressAsync(first, ct), []).ConfigureAwait(false)));
        }

        // A MAC search already knows its addresses; an address search asks ARP
        // again for each MAC's other addresses, as upstream's single-host view does.
        var known = candidate.Macs.Concat(arp.Select(a => a.MacAddress).OfType<string>()).Distinct().ToList();
        var macs = known.Where(m => !MacAddresses.IsNotAHost(m)).ToList();
        var lookedUp = macs.Take(_options.MaxMacsPerEndpoint).ToList();

        var perMac = await Task.WhenAll(lookedUp.Select(async mac =>
        {
            var fdb = LearnedAsync(run, mac);
            var ports = LearnedPortsAsync(run, mac);
            var vendor = run.Ask(SearchSource.Vendors, ct => _fdb.FindVendorAsync(mac, ct), null);
            var others = candidate.Macs.Contains(mac) ? Task.FromResult<IReadOnlyList<ArpEntry>>([])
                : run.Ask(SearchSource.Arp, ct => _arp.FindByMacAsync(mac, ct), []);
            await Task.WhenAll(fdb, ports, vendor, others).ConfigureAwait(false);
            return (Mac: mac, Fdb: fdb.Result, Ports: ports.Result, Vendor: vendor.Result, Others: Clean(others.Result));
        })).ConfigureAwait(false);

        foreach (var entry in perMac.SelectMany(m => m.Others))
        {
            if (arp.All(a => a.Id != entry.Id))
            {
                arp.Add(entry);
            }
        }

        // Only when one MAC answers for the address are its other addresses this host's.
        if (arp.Select(a => a.MacAddress).OfType<string>().Distinct().Count() <= 1)
        {
            addresses.AddRange(arp.Select(a => a.Ipv4Address).OfType<string>().Where(a => !addresses.Contains(a)));
        }

        var fdbEntries = perMac.SelectMany(m => m.Fdb).ToList();
        var portsById = perMac.SelectMany(m => m.Ports).GroupBy(p => p.PortId).ToDictionary(g => g.Key, g => g.First());
        if (fdbEntries.Count > 0 || portsById.Count > 0)
        {
            run.Found(SearchSource.ForwardingTable);
        }
        else if (lookedUp.Count > 0)
        {
            run.NoEntry(SearchSource.ForwardingTable);
        }
        else
        {
            run.NotQueried(SearchSource.ForwardingTable, known.Count > 0
                ? "Its MAC is broadcast or multicast, which no single host owns."
                : "No MAC address is known for this endpoint.");
        }

        if (macs.Count > lookedUp.Count)
        {
            run.Note(SearchSource.ForwardingTable, $"Only {lookedUp.Count} of {macs.Count} MACs were looked up.", lookedUp.Count, macs.Count);
        }

        // The VLAN table gives older servers' numbers and every server's names.
        // Without it, 26.11's direct numbers still stand.
        var resolver = FdbVlanResolver.Empty;
        if (fdbEntries.Count > 0)
        {
            var vlans = await run.Once("vlans", () => run.Quietly<IReadOnlyList<Vlan>>(ct => _vlans.ListAsync(ct))).ConfigureAwait(false);
            if (vlans is null && FdbVlanResolver.NeedsVlanTable(fdbEntries))
            {
                run.Note(SearchSource.ForwardingTable, "The VLAN table couldn't be read, so older servers' VLANs are unknown.");
            }

            resolver = new FdbVlanResolver(vlans ?? []);
        }

        var links = fdbEntries.Count == 0 && portsById.Count == 0 ? []
            : await run.Ask(SearchSource.Links, ct => _links.ListAllAsync(ct), []).ConfigureAwait(false);
        var linked = links.Select(l => l.LocalPortId).ToHashSet();

        var subnet = await SubnetAsync(run, addresses).ConfigureAwait(false);
        var interfacePortId = subnet.Monitored?.PortId;

        // Name the router and switch ports nobody has described yet, within budget.
        var wanted = arp.Select(a => a.PortId)
            .Concat(fdbEntries.Select(f => f.PortId))
            .Concat(subnet.Interfaces.Select(i => i.PortId))
            .Concat(interfacePortId is { } id ? [id] : [])
            .Where(p => p > 0 && !portsById.ContainsKey(p))
            .Distinct().Take(_options.MaxNamedPorts).ToList();
        var named = await Task.WhenAll(wanted.Select(p => run.Quietly(ct => _ports.GetAsync(p, ct)))).ConfigureAwait(false);
        foreach (var port in named.OfType<Port>())
        {
            portsById.TryAdd(port.PortId, port);
        }

        Port? PortOf(int id) => portsById.GetValueOrDefault(id);

        var locations = fdbEntries
            .Where(f => MacAddresses.Normalise(f.MacAddress) is not null)
            .GroupBy(f => (f.PortId, Mac: MacAddresses.Normalise(f.MacAddress)!))
            .Select(g => g.OrderByDescending(f => f.UpdatedAt).First())
            .Select(f =>
            {
                var port = PortOf(f.PortId);
                return new EndpointLocation(
                    MacAddresses.Normalise(f.MacAddress)!, f.DeviceId, f.PortId, run.DeviceOf(f.DeviceId), port,
                    resolver.NumberOf(f), resolver.NameOf(f), port?.FdbEntriesCount,
                    linked.Contains(f.PortId), string.Equals(port?.IfType, "ieee8023adLag", StringComparison.OrdinalIgnoreCase),
                    f.UpdatedAt, IsStale: false);
            })
            .ToList();

        // A port that learned the MAC (ports/mac) when its forwarding-table
        // row couldn't be read is still a sighting, with its VLAN and time unknown.
        foreach (var (mac, port) in perMac.SelectMany(m => m.Ports.Select(p => (m.Mac, p))))
        {
            if (locations.All(l => l.PortId != port.PortId || l.Mac != mac))
            {
                locations.Add(new EndpointLocation(
                    mac, port.DeviceId, port.PortId, run.DeviceOf(port.DeviceId), PortOf(port.PortId) ?? port,
                    null, null, port.FdbEntriesCount, linked.Contains(port.PortId),
                    string.Equals(port.IfType, "ieee8023adLag", StringComparison.OrdinalIgnoreCase), null, IsStale: false));
            }
        }

        // Stale per sighting: a MAC that moved is current on its new port and stale on the old one.
        var newest = locations.Max(l => l.UpdatedAt);
        bool Stale(DateTime? seen) => seen is { } s && newest is { } n && n - s > _options.StaleAfter;
        var macRecords = macs.Select(mac =>
        {
            var seen = locations.Where(l => l.Mac == mac).Max(l => l.UpdatedAt);
            return new EndpointMac(mac, perMac.FirstOrDefault(m => m.Mac == mac).Vendor, seen, Stale(seen));
        }).ToList();

        locations = locations
            .Select(l => l with { IsStale = Stale(l.UpdatedAt) })
            .OrderBy(l => l.IsStale)
            .ThenBy(l => l.Kind == LocationKind.Uplink)
            .ThenBy(l => l.MacCount ?? int.MaxValue)
            .ThenByDescending(l => l.UpdatedAt)
            .ToList();

        var neighbours = arp
            .Where(a => a.MacAddress is not null && a.Ipv4Address is not null)
            .Select(a => new EndpointNeighbour("ARP", a.Ipv4Address!, a.MacAddress!, a.DeviceId, a.PortId, run.DeviceOf(a.DeviceId), PortOf(a.PortId)))
            .ToList();

        var monitoredInterface = interfacePortId is { } portId ? PortOf(portId) : null;
        var device = candidate.Device
                     ?? addresses.Select(run.DeviceAt).FirstOrDefault(d => d is not null)
                     ?? (monitoredInterface is not null ? run.DeviceOf(monitoredInterface.DeviceId) : null);

        var conflicts = AddressConflicts(neighbours).ToList();
        foreach (var mac in macs)
        {
            var access = locations.Where(l => l.Mac == mac && !l.IsStale && l.Kind != LocationKind.Uplink).Select(l => l.PortId).Distinct().ToList();
            if (access.Count > 1)
            {
                conflicts.Add(new EndpointConflict(EndpointConflictKind.MacOnSeveralPorts, mac, access.Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList()));
            }
        }

        return new EndpointRecord
        {
            Title = Title(addresses, macs, device),
            Addresses = addresses,
            Macs = macRecords,
            Names = Names(device),
            MonitoredDevice = device,
            MonitoredInterface = monitoredInterface,
            MonitoredInterfacePortId = interfacePortId,
            Locations = locations,
            Neighbours = neighbours,
            Subnet = subnet.Network is null ? null : new EndpointSubnet(
                subnet.Network,
                subnet.Interfaces.Take(_options.MaxSubnetInterfaces)
                    .Select(i => i with { Port = PortOf(i.PortId), Device = PortOf(i.PortId) is { } p ? run.DeviceOf(p.DeviceId) : null })
                    .ToList(),
                subnet.Interfaces.Count),
            Conflicts = conflicts,
            IsDetailed = true,
        };
    }

    /// <summary>The most specific monitored network holding the endpoint's first address, its interfaces, and the one that is the endpoint itself.</summary>
    private async Task<(string? Network, IReadOnlyList<SubnetInterface> Interfaces, SubnetInterface? Monitored)> SubnetAsync(Run run, IReadOnlyList<string> addresses)
    {
        if (addresses.Count == 0 || !IPAddress.TryParse(addresses[0], out var address))
        {
            run.NotQueried(SearchSource.Networks, "No IP address is known for this endpoint.");
            return (null, [], null);
        }

        var networks = await NetworksAsync(run, address.AddressFamily).ConfigureAwait(false);
        var best = networks
            .Select(n => NetworkSearchQuery.TryParseNetwork(n.Cidr, out var b, out var p) ? (Row: n, Base: b, Prefix: p) : default)
            .Where(n => n.Row is not null && NetworkSearchQuery.Contains(n.Base, n.Prefix, address))
            .OrderByDescending(n => n.Prefix)
            .FirstOrDefault();
        if (best.Row is null)
        {
            return (null, [], null);
        }

        var members = await MembersAsync(run, best.Row.Id, address.AddressFamily).ConfigureAwait(false);
        var interfaces = members
            .Where(m => m.Address is not null)
            .Select(m => new SubnetInterface(m.Address!, m.PrefixLength, m.PortId, null, null))
            .OrderBy(i => AddressKey(i.Address))
            .ToList();
        var self = interfaces.FirstOrDefault(i => SameAddress(i.Address, address));
        return ($"{best.Base}/{best.Prefix}", interfaces.Where(i => i != self).ToList(), self);
    }

    private async Task<bool> IsMonitoredAddressAsync(Run run, IPAddress address)
    {
        var subnet = await SubnetAsync(run, [Compressed(address)]).ConfigureAwait(false);
        return subnet.Monitored is not null;
    }

    private static T NotNull<T>(T? value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null)
        where T : class
        => value ?? throw new ArgumentNullException(name);

    /// <summary>
    /// ARP rows with their MACs in the one stored form, so the same MAC in two
    /// spellings can't count as two (and raise a false conflict). Rows with no
    /// readable MAC are dropped: they say nothing about who holds the address.
    /// </summary>
    private static IReadOnlyList<ArpEntry> Clean(IReadOnlyList<ArpEntry> arp)
        => arp.Select(a => MacAddresses.Normalise(a.MacAddress) is { } mac
                ? new ArpEntry { Id = a.Id, PortId = a.PortId, DeviceId = a.DeviceId, MacAddress = mac, Ipv4Address = a.Ipv4Address }
                : null)
            .OfType<ArpEntry>()
            .ToList();

    /// <summary>
    /// Whether a looked-up endpoint was seen anywhere at all. A vendor name
    /// isn't evidence: it comes from the MAC's prefix, not from the network.
    /// </summary>
    private static bool HasEvidence(EndpointRecord record)
        => record.Neighbours.Count > 0 || record.Locations.Count > 0 || record.IsMonitored || record.Subnet is not null;

    private Task<IReadOnlyList<FdbEntry>> LearnedAsync(Run run, string mac)
        => run.Once("fdb:" + mac, () => run.Ask(SearchSource.ForwardingTable, ct => _fdb.FindByMacAsync(mac, ct), []));

    private Task<IReadOnlyList<Port>> LearnedPortsAsync(Run run, string mac)
        => run.Once("ports:" + mac, () => run.Ask(SearchSource.ForwardingTable, ct => _ports.FindByLearnedMacAsync(mac, ct), []));

    private Task<IReadOnlyList<IpNetwork>> NetworksAsync(Run run, AddressFamily family)
        => run.Once($"networks:{family}", () => run.Ask(SearchSource.Networks, ct => _networks.ListAsync(family, ct), []));

    private Task<IReadOnlyList<IpNetworkAddress>> MembersAsync(Run run, int networkId, AddressFamily family)
        => run.Once($"network:{family}:{networkId}", () => run.Ask(SearchSource.Networks, ct => _networks.ListAddressesAsync(networkId, family, ct), []));

    /// <summary>The most networks a CIDR search reads the interfaces of.</summary>
    private const int MaxNetworksPerSearch = 20;

    /// <summary>
    /// Monitored interface addresses inside a searched network that ARP and
    /// the device list didn't already give - a router's own interfaces, or
    /// IPv6 addresses, which have no ARP.
    /// </summary>
    private async Task AddMonitoredInterfacesAsync(Run run, SearchTerm term, List<Candidate> found)
    {
        var networks = await NetworksAsync(run, term.Address!.AddressFamily).ConfigureAwait(false);
        var overlapping = networks
            .Select(n => NetworkSearchQuery.TryParseNetwork(n.Cidr, out var b, out var p) ? (Row: n, Base: b, Prefix: p) : default)
            .Where(n => n.Row is not null
                        && (NetworkSearchQuery.Contains(term.Address, term.PrefixLength!.Value, n.Base) || NetworkSearchQuery.Contains(n.Base, n.Prefix, term.Address)))
            .ToList();
        if (overlapping.Count > MaxNetworksPerSearch)
        {
            run.Note(SearchSource.Networks, $"Only {MaxNetworksPerSearch} of {overlapping.Count} networks' interfaces were read.", MaxNetworksPerSearch, overlapping.Count);
        }

        var members = await Task.WhenAll(overlapping.Take(MaxNetworksPerSearch).Select(n => MembersAsync(run, n.Row.Id, term.Address.AddressFamily))).ConfigureAwait(false);
        foreach (var member in members.SelectMany(m => m))
        {
            if (member.Address is { } text && IPAddress.TryParse(text, out var ip)
                && NetworkSearchQuery.Contains(term.Address, term.PrefixLength!.Value, ip)
                && found.All(c => !c.Addresses.Any(a => SameAddress(a, ip))))
            {
                found.Add(new Candidate([Compressed(ip)], [], [], run.DeviceAt(Compressed(ip))) { InterfacePortId = member.PortId });
            }
        }
    }

    private static IEnumerable<Candidate> FromArp(IEnumerable<ArpEntry> arp)
        => arp.Where(a => !string.IsNullOrWhiteSpace(a.Ipv4Address))
            .GroupBy(a => a.Ipv4Address!)
            .OrderBy(g => AddressKey(g.Key))
            .Select(g => new Candidate([g.Key], [], g.ToList(), null));

    /// <summary>Candidates that share an address or MAC are one endpoint (a pasted list naming the same host twice).</summary>
    private static List<Candidate> Merge(List<Candidate> candidates)
    {
        // Transitive: a MAC shared by two pasted addresses joins all three into one.
        var merged = new List<Candidate>();
        foreach (var candidate in candidates)
        {
            var joined = candidate;
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                var m = merged[i];
                if (m.Addresses.Intersect(joined.Addresses).Any()
                    || m.Macs.Intersect(joined.Macs).Any()
                    || (m.Device is not null && m.Device.DeviceId == joined.Device?.DeviceId))
                {
                    joined = new Candidate(
                        m.Addresses.Union(joined.Addresses).ToList(),
                        m.Macs.Union(joined.Macs).ToList(),
                        m.Arp.Concat(joined.Arp).DistinctBy(a => a.Id).ToList(),
                        m.Device ?? joined.Device) { InterfacePortId = m.InterfacePortId ?? joined.InterfacePortId };
                    merged.RemoveAt(i);
                }
            }

            merged.Add(joined);
        }

        return merged;
    }

    private static IReadOnlyList<EndpointConflict> AddressConflicts(IEnumerable<EndpointNeighbour> neighbours)
        => neighbours
            .Where(n => !(IPAddress.TryParse(n.Address, out var ip) && ip.IsIPv6LinkLocal))
            .GroupBy(n => n.Address)
            .Where(g => g.Select(n => n.Mac).Distinct().Count() > 1)
            .Select(g => new EndpointConflict(EndpointConflictKind.AddressWithSeveralMacs, g.Key, g.Select(n => n.Mac).Distinct().ToList()))
            .ToList();

    /// <summary>Devices named <paramref name="text"/>: exact hostname, sysName or display name first, then any containing it.</summary>
    private static IReadOnlyList<Device> MatchNames(IReadOnlyList<Device> devices, string text)
    {
        // Every exact match: two devices can report the same sysName.
        var exact = devices.Where(d => SameAddress(d.Ip, text) || Names(d).Any(n => n.Equals(text, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        if (DeviceAddresses.Find(devices, text) is { } shortName)
        {
            return [shortName];
        }

        return devices
            .Where(d => Names(d).Any(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<Device> DevicesIn(IEnumerable<Device> devices, SearchTerm network)
        => devices.Where(d => IPAddress.TryParse(d.Ip, out var ip) && NetworkSearchQuery.Contains(network.Address!, network.PrefixLength!.Value, ip));

    private static bool IsKnownAddress(IEnumerable<Device> devices, IPAddress address)
        => devices.Any(d => SameAddress(d.Ip, address));

    private static IReadOnlyList<string> Names(Device? device)
        => device is null ? []
            : new[] { device.Hostname, device.SysName, device.Display }
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string Title(IReadOnlyList<string> addresses, IReadOnlyList<string> macs, Device? device)
        => addresses.FirstOrDefault() ?? (macs.Count > 0 ? MacAddresses.Format(macs[0], MacAddressFormat.Colon) : null) ?? Names(device).FirstOrDefault() ?? string.Empty;

    private static bool SameAddress(string? text, string other)
        => IPAddress.TryParse(other, out var parsed) && SameAddress(text, parsed);

    private static bool SameAddress(string? text, IPAddress address)
        => IPAddress.TryParse(text, out var parsed) && (parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed).Equals(address);

    private static string Compressed(IPAddress address) => address.ToString();

    /// <summary>Sorts addresses numerically, IPv4 before IPv6, text last.</summary>
    private static string AddressKey(string? text)
        => IPAddress.TryParse(text, out var ip)
            ? (ip.AddressFamily == AddressFamily.InterNetwork ? "4" : "6") + Convert.ToHexString(ip.GetAddressBytes())
            : "9" + text;

    /// <summary>What a term found before the full lookup.</summary>
    private sealed record Candidate(IReadOnlyList<string> Addresses, IReadOnlyList<string> Macs, IReadOnlyList<ArpEntry> Arp, Device? Device)
    {
        /// <summary>The monitored interface a network search found this address on, so a list row still knows it's monitored.</summary>
        public int? InterfacePortId { get; init; }
    }

    /// <summary>One search's requests and coverage.</summary>
    private sealed class Run
    {
        private readonly CancellationToken _cancellationToken;
        private readonly SemaphoreSlim _gate;
        private readonly ConcurrentDictionary<SearchSource, SourceCoverage> _coverage = new();
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _once = new();

        public Run(NetworkSearchOptions options, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _gate = new SemaphoreSlim(options.MaxConcurrentRequests);
        }

        public IReadOnlyDictionary<int, Device> Devices { get; set; } = new Dictionary<int, Device>();

        public Device? DeviceOf(int deviceId) => Devices.GetValueOrDefault(deviceId);

        public Device? DeviceAt(string address)
            => IPAddress.TryParse(address, out var ip) ? Devices.Values.FirstOrDefault(d => SameAddress(d.Ip, ip)) : null;

        /// <summary>
        /// Calls one source, recording what it said. A denial, missing route
        /// or failure is recorded and <paramref name="fallback"/> returned, so
        /// the rest of the search goes on; cancellation is not swallowed.
        /// </summary>
        public async Task<T> Ask<T>(SearchSource source, Func<CancellationToken, Task<T>> call, T fallback)
        {
            await _gate.WaitAsync(_cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await call(_cancellationToken).ConfigureAwait(false);
                Record(source, IsEmpty(result) ? SourceState.NoEntry : SourceState.Found);
                return result;
            }
            catch (LibreNmsApiException ex) when (ex.IsPermissionDenied && !ex.IsAuthenticationFailure)
            {
                Record(source, SourceState.PermissionDenied, "This account may not read it.");
                return fallback;
            }
            catch (LibreNmsApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Record(source, SourceState.Unavailable, "This LibreNMS server doesn't offer it.");
                return fallback;
            }
            catch (LibreNmsApiException ex) when (!ex.IsAuthenticationFailure)
            {
                Record(source, SourceState.Failed, ex.Message);
                return fallback;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>A naming call whose failure costs only a port name, not coverage.</summary>
        public async Task<T?> Quietly<T>(Func<CancellationToken, Task<T?>> call)
        {
            await _gate.WaitAsync(_cancellationToken).ConfigureAwait(false);
            try
            {
                return await call(_cancellationToken).ConfigureAwait(false);
            }
            catch (LibreNmsApiException ex) when (!ex.IsAuthenticationFailure)
            {
                return default;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Found(SearchSource source) => Record(source, SourceState.Found);

        public void NoEntry(SearchSource source) => Record(source, SourceState.NoEntry);

        public void NotQueried(SearchSource source, string detail) => Record(source, SourceState.NotQueried, detail);

        public void Unavailable(SearchSource source, string detail) => Record(source, SourceState.Unavailable, detail);

        /// <summary>
        /// Adds a note (and a truncation count) to what a source said, without
        /// changing its state - call it after the source has been asked. A
        /// source that wasn't asked stays <see cref="SourceState.NotQueried"/>.
        /// </summary>
        public void Note(SearchSource source, string detail, int? shown = null, int? total = null)
            => _coverage.AddOrUpdate(source,
                _ => new SourceCoverage(source, SourceState.NotQueried, detail, shown, total),
                (_, existing) => existing with { Detail = detail, Shown = shown ?? existing.Shown, Total = total ?? existing.Total });

        /// <summary>Runs <paramref name="call"/> once per <paramref name="key"/> in this search - the network list a pasted list would otherwise fetch per term.</summary>
        public async Task<T> Once<T>(string key, Func<Task<T>> call)
            => (T)await _once.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => (await call().ConfigureAwait(false))!)).Value.ConfigureAwait(false);

        public IReadOnlyList<SourceCoverage> Coverage() => _coverage.Values.OrderBy(c => c.Source).ToList();

        /// <summary>
        /// The worst of what a source said across its calls wins: one denied
        /// call means its answer is incomplete, and one hit beats "no entry".
        /// </summary>
        private void Record(SearchSource source, SourceState state, string? detail = null)
            => _coverage.AddOrUpdate(source,
                _ => new SourceCoverage(source, state, detail),
                (_, existing) => Rank(state) > Rank(existing.State) ? existing with { State = state, Detail = detail } : existing);

        private static int Rank(SourceState state) => state switch
        {
            SourceState.NotQueried => 0,
            SourceState.NoEntry => 1,
            SourceState.Found => 2,
            SourceState.Unavailable => 3,
            SourceState.Failed => 4,
            SourceState.PermissionDenied => 5,
            _ => 0,
        };

        private static bool IsEmpty<T>(T result) => result switch
        {
            null => true,
            System.Collections.ICollection { Count: 0 } => true,
            string s => string.IsNullOrWhiteSpace(s),
            _ => false,
        };
    }
}
