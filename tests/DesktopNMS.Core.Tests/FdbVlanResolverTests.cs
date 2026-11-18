using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// Forwarding-table VLANs from both LibreNMS shapes (#86): internal ids
/// before 26.11, direct numbers from 26.11 (librenms#21152).
/// </summary>
public class FdbVlanResolverTests
{
    private static async Task<(IReadOnlyList<FdbEntry> Fdb, FdbVlanResolver Resolver)> Load(string release)
    {
        var fdb = await LibreNmsFixtures.Read<FdbEntry>($"{release}/fdb.json", "ports_fdb");
        var vlans = await LibreNmsFixtures.Read<Vlan>($"{release}/vlans.json", "vlans");
        return (fdb, new FdbVlanResolver(vlans));
    }

    private static FdbEntry Entry(IEnumerable<FdbEntry> fdb, int id) => fdb.Single(e => e.Id == id);

    [Fact]
    public async Task Older_servers_resolve_internal_ids_through_the_vlan_table()
    {
        var (fdb, resolver) = await Load("26.10.0");

        // Internal ids 50 and 51 are VLANs 2074 and 2099 - never 50 or 51.
        Assert.Equal(50, Entry(fdb, 101).VlanId);
        Assert.Null(Entry(fdb, 101).VlanNumber);
        Assert.Equal(2074, resolver.NumberOf(Entry(fdb, 101)));
        Assert.Equal("Cameras", resolver.NameOf(Entry(fdb, 101)));
        Assert.Equal(2099, resolver.NumberOf(Entry(fdb, 102)));
        Assert.Equal("Voice", resolver.NameOf(Entry(fdb, 102)));
    }

    [Theory]
    [InlineData(103)] // vlan_id 0: no VLAN recorded
    [InlineData(104)] // an id with no VLAN row
    [InlineData(105)] // an id whose row belongs to another device
    [InlineData(106)] // no vlan_id at all
    public async Task Older_servers_keep_unresolvable_ids_unknown(int id)
    {
        var (fdb, resolver) = await Load("26.10.0");

        Assert.Null(resolver.NumberOf(Entry(fdb, id)));
        Assert.Null(resolver.NameOf(Entry(fdb, id)));
        Assert.Null(resolver.Find(Entry(fdb, id)));
    }

    [Fact]
    public async Task Newer_servers_send_the_number_and_names_match_by_device_and_number()
    {
        var (fdb, resolver) = await Load("26.11.0");

        Assert.Null(Entry(fdb, 101).VlanId);
        Assert.Equal(2074, resolver.NumberOf(Entry(fdb, 101)));
        Assert.Equal("Cameras", resolver.NameOf(Entry(fdb, 101)));
        Assert.Equal(1, resolver.Find(Entry(fdb, 101))!.DeviceId);
        Assert.Equal(2099, resolver.NumberOf(Entry(fdb, 102)));
        Assert.Equal("Voice", resolver.NameOf(Entry(fdb, 102)));
    }

    [Fact]
    public async Task A_direct_number_needs_no_vlan_table_row()
    {
        var (fdb, resolver) = await Load("26.11.0");

        Assert.Equal(3010, resolver.NumberOf(Entry(fdb, 104)));
        Assert.Null(resolver.NameOf(Entry(fdb, 104)));
        Assert.Equal(3010, FdbVlanResolver.Empty.NumberOf(Entry(fdb, 104)));
        Assert.Equal(2074, FdbVlanResolver.Empty.NumberOf(Entry(fdb, 101)));
    }

    [Fact]
    public async Task A_direct_number_is_never_read_as_an_internal_id()
    {
        var (fdb, resolver) = await Load("26.11.0");

        // VLAN 50 is the number 50, not the row whose internal id is 50 (VLAN 2074).
        Assert.Equal(50, resolver.NumberOf(Entry(fdb, 105)));
        Assert.Null(resolver.NameOf(Entry(fdb, 105)));
    }

    [Theory]
    [InlineData(103)] // null: the switch did not say
    [InlineData(106)] // 0 is reserved
    [InlineData(107)] // 4095 is reserved
    public async Task Newer_servers_keep_missing_and_reserved_numbers_unknown(int id)
    {
        var (fdb, resolver) = await Load("26.11.0");

        Assert.Null(resolver.NumberOf(Entry(fdb, id)));
        Assert.Null(resolver.NameOf(Entry(fdb, id)));
    }

    [Fact]
    public async Task Without_the_vlan_table_older_ids_stay_unknown()
    {
        var (fdb, _) = await Load("26.10.0");

        Assert.All(fdb, e => Assert.Null(FdbVlanResolver.Empty.NumberOf(e)));
    }

    [Fact]
    public async Task Only_older_responses_need_the_vlan_table_for_numbers()
    {
        var (older, _) = await Load("26.10.0");
        var (newer, _) = await Load("26.11.0");

        Assert.True(FdbVlanResolver.NeedsVlanTable(older));
        Assert.False(FdbVlanResolver.NeedsVlanTable(newer));
    }

    [Fact]
    public void A_named_row_wins_over_an_unnamed_duplicate_for_the_same_number()
    {
        var resolver = new FdbVlanResolver(
        [
            new Vlan { VlanId = 1, DeviceId = 7, VlanNumber = 300, VlanName = null },
            new Vlan { VlanId = 2, DeviceId = 7, VlanNumber = 300, VlanName = " Printers " },
        ]);

        Assert.Equal("Printers", resolver.NameOf(new FdbEntry { DeviceId = 7, VlanNumber = 300 }));
        Assert.Null(resolver.NameOf(new FdbEntry { DeviceId = 8, VlanNumber = 300 }));
    }
}
