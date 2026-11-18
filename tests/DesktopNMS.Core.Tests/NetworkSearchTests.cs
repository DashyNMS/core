using System.Net;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Search;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// Network Search end to end against synthetic LibreNMS 26.11 responses (#91):
/// each query kind, coverage states, conflicts, uplinks, paging, permission
/// failures and old and new forwarding-table shapes.
/// </summary>
public class NetworkSearchTests
{
    private const string Dir = "26.11.0/search/";
    private const string Host = "00005e005368";

    /// <summary>The network: a host on sw-access-01 port 19, seen through two uplinks and routed by fw-01.</summary>
    private static LibreNmsFixtureServer Network(Action<LibreNmsFixtureServer>? first = null)
    {
        var server = new LibreNmsFixtureServer();
        first?.Invoke(server);
        return server
            .Serve("devices", Dir + "devices.json")
            .Serve("resources/ip/arp/all", Dir + "arp-all.json")
            .Serve("resources/ip/arp/192.0.2.0/24", Dir + "arp-network.json")
            .Serve("resources/ip/arp/192.0.2.117", Dir + "arp-117.json")
            .Serve("resources/ip/arp/192.0.2.200", Dir + "arp-200.json")
            .Serve("resources/ip/arp/00:00:5e:00:53:68", Dir + "arp-117.json")
            .Serve("resources/ip/arp/00:00:5e:00:53:01", Dir + "arp-mac-301.json")
            .Serve("resources/ip/arp/00:00:5e:00:53:02", Dir + "arp-mac-302.json")
            .Serve("resources/ip/arp/", Dir + "arp-empty.json")
            .Serve($"resources/fdb/{Host}/detail", Dir + "vendor-368.json")
            .Serve($"resources/fdb/{Host}", Dir + "fdb-368.json")
            .Serve("resources/fdb/00005e005399/detail", Dir + "fdb-missing.json", HttpStatusCode.NotFound)
            .Serve("resources/fdb/00005e005399", Dir + "fdb-399.json")
            .Serve("resources/fdb/", Dir + "fdb-missing.json", HttpStatusCode.NotFound)
            .Serve($"ports/mac/{Host}", Dir + "ports-mac-368.json")
            .Serve("ports/mac/00005e005399", Dir + "ports-mac-399.json")
            .Serve("ports/mac/", Dir + "ports-mac-missing.json", HttpStatusCode.NotFound)
            .Serve("ports/3069", Dir + "port-3069.json")
            .Serve("ports/1100", Dir + "port-1100.json")
            .Serve("ports/1021", Dir + "port-1021.json")
            .Serve("ports/", Dir + "port-empty.json")
            .Serve("resources/links", Dir + "links.json")
            .Serve("resources/ip/networks/ipv4", Dir + "networks-ipv4.json")
            .Serve("resources/ip/networks/ipv6", Dir + "networks-ipv6.json", HttpStatusCode.NotFound)
            .Serve("resources/ip/networks/7/ip", Dir + "network-7.json")
            .Serve("resources/vlans", "26.10.0/vlans.json");
    }

    private static NetworkSearch Search(LibreNmsFixtureServer server, NetworkSearchOptions? options = null)
        => new(new LibreNmsResourceApis(server), options);

    [Fact]
    public async Task An_address_finds_one_endpoint_and_where_it_plugs_in()
    {
        var result = await Search(Network()).SearchAsync("192.0.2.117");

        Assert.Equal(NetworkSearchOutcome.Single, result.Outcome);
        var host = Assert.Single(result.Endpoints);
        Assert.True(host.IsDetailed);
        Assert.Equal("192.0.2.117", host.Title);
        Assert.Equal("ICANN, IANA Department", Assert.Single(host.Macs).Vendor);

        // Closest first: the access port with one MAC, then the uplinks.
        Assert.Equal([1019, 2037, 4001], host.Locations.Select(l => l.PortId));
        var best = host.BestLocation!;
        Assert.Equal(LocationKind.Direct, best.Kind);
        Assert.Equal("sw-access-01.example.net", best.Device!.Hostname);
        Assert.Equal(2069, best.VlanNumber);
        Assert.True(host.Locations[1].IsUplink);
        Assert.Equal(LocationKind.Uplink, host.Locations[2].Kind);
        Assert.Null(host.Locations[2].VlanNumber); // the switch didn't say: unknown, not VLAN 0

        var neighbour = Assert.Single(host.Neighbours);
        Assert.Equal("fw-01.example.net", neighbour.Device!.Hostname);
        Assert.Equal("lagg1.2069", neighbour.Port!.IfName);

        Assert.Equal("192.0.2.0/24", host.Subnet!.Network); // the most specific network, not the /16
        Assert.Equal(["192.0.2.1", "192.0.2.11"], host.Subnet.Interfaces.Select(i => i.Address));
        Assert.Equal("lagg1.2069", host.Subnet.Interfaces[0].Port!.IfName);
        Assert.False(host.IsMonitored);
        Assert.Empty(host.Conflicts);

        Assert.All(result.Coverage.Where(c => c.Source != SearchSource.Vendors), c => Assert.Equal(SourceState.Found, c.State));
        Assert.Null(result.CoverageOf(SearchSource.NeighbourDiscovery));
    }

    [Fact]
    public async Task A_mac_in_any_form_finds_the_same_endpoint()
    {
        var result = await Search(Network()).SearchAsync("0000.5E00.5368");

        var host = Assert.Single(result.Endpoints);
        Assert.Equal(["192.0.2.117"], host.Addresses);
        Assert.Equal(1019, host.BestLocation!.PortId);
    }

    [Fact]
    public async Task A_name_finds_the_monitored_device()
    {
        var result = await Search(Network()).SearchAsync("fw-01");

        var device = Assert.Single(result.Endpoints);
        Assert.Equal("fw-01.example.net", device.MonitoredDevice!.Hostname);
        Assert.Contains("fw-01", device.Names);
        Assert.Equal(["192.0.2.1"], device.Addresses);
        Assert.Equal(3069, device.MonitoredInterface!.PortId);
        Assert.Equal(SourceState.NotQueried, result.CoverageOf(SearchSource.ForwardingTable)!.State);
    }

    [Fact]
    public async Task A_network_lists_every_endpoint_in_it_and_pages()
    {
        var search = Search(Network(), new NetworkSearchOptions { PageSize = 2 });

        var first = await search.SearchAsync("192.0.2.0/24");
        var second = await search.SearchAsync("192.0.2.0/24", offset: 2);

        Assert.Equal(NetworkSearchOutcome.List, first.Outcome);
        Assert.Equal(6, first.Total); // two ARP hosts and four monitored devices
        Assert.Equal(["192.0.2.1", "192.0.2.2"], first.Endpoints.Select(e => e.Title));
        Assert.Equal(4, first.Remaining);
        Assert.Equal(["192.0.2.11", "192.0.2.14"], second.Endpoints.Select(e => e.Title));
        Assert.All(first.Endpoints, e => Assert.False(e.IsDetailed));
        Assert.Equal(SourceState.NotQueried, first.CoverageOf(SearchSource.ForwardingTable)!.State);
    }

    [Fact]
    public async Task An_empty_query_is_a_bounded_overview_not_an_error()
    {
        var result = await Search(Network(), new NetworkSearchOptions { PageSize = 3 }).SearchAsync("  ");

        Assert.Equal(NetworkSearchOutcome.Overview, result.Outcome);
        Assert.Equal(4, result.Total);
        Assert.Equal(["192.0.2.117", "192.0.2.200", "198.51.100.9"], result.Endpoints.Select(e => e.Title));
        Assert.Equal(1, result.Remaining);
    }

    [Fact]
    public async Task One_address_with_two_macs_is_a_conflict()
    {
        var result = await Search(Network()).SearchAsync("192.0.2.200");

        var host = Assert.Single(result.Endpoints);
        var conflict = Assert.Single(host.Conflicts);
        Assert.Equal(EndpointConflictKind.AddressWithSeveralMacs, conflict.Kind);
        Assert.Equal(["00005e005301", "00005e005302"], conflict.Values.Order());

        // Neither MAC is in a forwarding table: a real negative.
        Assert.Equal(SourceState.NoEntry, result.CoverageOf(SearchSource.ForwardingTable)!.State);
        Assert.Empty(host.Locations);
    }

    [Fact]
    public async Task One_mac_on_two_access_ports_is_a_conflict()
    {
        var result = await Search(Network()).SearchAsync("00:00:5e:00:53:99");

        var host = Assert.Single(result.Endpoints);
        var conflict = Assert.Single(host.Conflicts);
        Assert.Equal(EndpointConflictKind.MacOnSeveralPorts, conflict.Kind);
        Assert.Equal(["1019", "1021"], conflict.Values.Order());
        Assert.Equal(SourceState.NoEntry, result.CoverageOf(SearchSource.Vendors)!.State);
    }

    [Fact]
    public async Task Too_many_macs_are_counted_not_fetched()
    {
        var result = await Search(Network(), new NetworkSearchOptions { MaxMacsPerEndpoint = 1 }).SearchAsync("192.0.2.200");

        var fdb = result.CoverageOf(SearchSource.ForwardingTable)!;
        Assert.True(fdb.IsTruncated);
        Assert.Equal((1, 2), (fdb.Shown, fdb.Total));
    }

    [Fact]
    public async Task A_denied_forwarding_table_is_reported_never_as_not_found()
    {
        var server = Network(s => s.Serve("resources/fdb/", Dir + "forbidden.json", HttpStatusCode.Forbidden));

        var result = await Search(server).SearchAsync("192.0.2.117");

        Assert.Equal(NetworkSearchOutcome.Single, result.Outcome);
        Assert.Equal(SourceState.PermissionDenied, result.CoverageOf(SearchSource.ForwardingTable)!.State);
        Assert.Single(result.Endpoints[0].Neighbours);
    }

    [Fact]
    public async Task Nothing_found_with_a_denied_source_is_not_a_confirmed_absence()
    {
        var denied = Network(s => s.Serve("resources/ip/arp/", Dir + "forbidden.json", HttpStatusCode.Forbidden));
        var open = Network();

        var blind = await Search(denied).SearchAsync("192.0.2.99");
        var clear = await Search(open).SearchAsync("192.0.2.99");

        Assert.Equal(NetworkSearchOutcome.NotFound, blind.Outcome);
        Assert.Equal(SourceState.PermissionDenied, blind.CoverageOf(SearchSource.Arp)!.State);
        Assert.False(blind.IsConfirmedAbsent);
        Assert.Equal(NetworkSearchOutcome.NotFound, clear.Outcome);
        Assert.Equal(SourceState.NoEntry, clear.CoverageOf(SearchSource.Arp)!.State);
        Assert.True(clear.IsConfirmedAbsent);
    }

    [Fact]
    public async Task Ipv6_says_neighbour_discovery_is_unavailable()
    {
        var result = await Search(Network()).SearchAsync("2001:db8::5");

        Assert.Equal(NetworkSearchOutcome.NotFound, result.Outcome);
        Assert.Equal(SourceState.Unavailable, result.CoverageOf(SearchSource.NeighbourDiscovery)!.State);
        Assert.Equal(SourceState.NoEntry, result.CoverageOf(SearchSource.Networks)!.State); // "none" is a 404 upstream
        Assert.False(result.IsConfirmedAbsent);
    }

    [Fact]
    public async Task An_older_server_without_a_route_is_unavailable_not_empty()
    {
        var server = Network(s => s.Serve("ports/mac/", Dir + "route-missing.json", HttpStatusCode.NotFound));

        var result = await Search(server).SearchAsync("192.0.2.117");

        Assert.Equal(SourceState.Unavailable, result.CoverageOf(SearchSource.ForwardingTable)!.State);
        Assert.Equal([1019, 2037, 4001], result.Endpoints[0].Locations.Select(l => l.PortId).Order());
        Assert.All(result.Endpoints[0].Locations, l => Assert.Null(l.MacCount));
    }

    [Fact]
    public async Task Legacy_forwarding_tables_resolve_vlans_through_the_vlan_table()
    {
        var server = Network(s => s.Serve($"resources/fdb/{Host}/detail", Dir + "vendor-368.json")
            .Serve($"resources/fdb/{Host}", Dir + "fdb-368-legacy.json"));

        var result = await Search(server).SearchAsync("192.0.2.117");

        var location = Assert.Single(result.Endpoints[0].Locations);
        Assert.Equal(2074, location.VlanNumber); // internal id 50, never shown as VLAN 50
        Assert.Equal("Cameras", location.VlanName);
        Assert.Contains("resources/vlans", server.Requests);
    }

    [Fact]
    public async Task New_forwarding_tables_skip_the_fleet_vlan_table()
    {
        var server = Network();

        await Search(server).SearchAsync("192.0.2.117");

        Assert.DoesNotContain("resources/vlans", server.Requests);
    }

    [Fact]
    public async Task A_pasted_list_naming_one_host_twice_is_one_endpoint()
    {
        var result = await Search(Network()).SearchAsync("192.0.2.117 00:00:5e:00:53:68");

        Assert.Equal(NetworkSearchOutcome.Single, result.Outcome);
        Assert.Equal(1019, result.Endpoints[0].BestLocation!.PortId);
    }

    [Fact]
    public async Task Cancellation_stops_the_search()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Search(Network()).SearchAsync("192.0.2.117", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Export_writes_one_row_per_port_and_defuses_formulas()
    {
        var result = await Search(Network()).SearchAsync("192.0.2.117");

        var csv = NetworkSearchExport.ToCsv(result.Endpoints, MacAddressFormat.Dotted).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, csv.Length);
        Assert.StartsWith("Address,MAC,Vendor", csv[0], StringComparison.Ordinal);
        Assert.Contains("192.0.2.117,0000.5e00.5368,\"ICANN, IANA Department\",,sw-access-01.example.net,", csv[1], StringComparison.Ordinal);
        Assert.Contains(",2069,Direct,", csv[1], StringComparison.Ordinal);
        Assert.Contains(",Uplink,", csv[3], StringComparison.Ordinal);

        var planted = new EndpointRecord
        {
            Title = "x", Addresses = ["192.0.2.5"], Macs = [], Names = ["=HYPERLINK(\"http://example.net\")"],
            Locations = [], Neighbours = [], Conflicts = [],
        };
        Assert.Contains("\"'=HYPERLINK(\"\"http://example.net\"\")\"", NetworkSearchExport.ToCsv([planted]), StringComparison.Ordinal);
    }
}
