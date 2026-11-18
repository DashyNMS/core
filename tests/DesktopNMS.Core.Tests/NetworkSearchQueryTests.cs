using DesktopNMS.Core.Search;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Reading Network Search queries and MAC addresses (#91).</summary>
public class NetworkSearchQueryTests
{
    [Theory]
    [InlineData("00:00:5e:00:53:68")]
    [InlineData("00-00-5E-00-53-68")]
    [InlineData("0000.5e00.5368")]
    [InlineData("00005e005368")]
    [InlineData("0x00005E005368")]
    [InlineData("0:0:5e:0:53:68")]
    public void Every_common_mac_form_reads_as_one(string text)
    {
        Assert.Equal("00005e005368", MacAddresses.Normalise(text));
        var term = Assert.Single(NetworkSearchQuery.Parse(text).Terms);
        Assert.Equal(SearchTermKind.MacAddress, term.Kind);
        Assert.Equal("00005e005368", term.Mac);
    }

    [Theory]
    [InlineData("00:00:5e:00:53")] // too short
    [InlineData("00:00-5e:00:53:68")] // mixed separators
    [InlineData("cafe")] // a word that happens to be hex
    [InlineData("0000.5e00.53")]
    public void Partial_or_mixed_macs_are_not_macs(string text) => Assert.Null(MacAddresses.Normalise(text));

    [Theory]
    [InlineData(MacAddressFormat.Colon, "00:00:5e:00:53:68")]
    [InlineData(MacAddressFormat.Hyphen, "00-00-5e-00-53-68")]
    [InlineData(MacAddressFormat.Dotted, "0000.5e00.5368")]
    [InlineData(MacAddressFormat.Bare, "00005e005368")]
    public void Macs_format_each_way(MacAddressFormat format, string expected)
        => Assert.Equal(expected, MacAddresses.Format("00:00:5E:00:53:68", format));

    [Theory]
    [InlineData("ffffffffffff")]
    [InlineData("000000000000")]
    [InlineData("01005e000001")] // multicast
    public void Broadcast_zero_and_multicast_are_never_a_host(string hex) => Assert.True(MacAddresses.IsNotAHost(hex));

    [Theory]
    [InlineData("192.0.2.117", SearchTermKind.Ipv4Address, "192.0.2.117")]
    [InlineData("ip=192.0.2.117", SearchTermKind.Ipv4Address, "192.0.2.117")]
    [InlineData("\"192.0.2.117:443\"", SearchTermKind.Ipv4Address, "192.0.2.117")]
    [InlineData("2001:db8::5", SearchTermKind.Ipv6Address, "2001:db8::5")]
    [InlineData("[2001:db8::5]:443", SearchTermKind.Ipv6Address, "2001:db8::5")]
    [InlineData("192.0.2.77/24", SearchTermKind.Ipv4Network, "192.0.2.0")]
    [InlineData("2001:db8:1::/48", SearchTermKind.Ipv6Network, "2001:db8:1::")]
    [InlineData("fw-01.example.net", SearchTermKind.Name, null)]
    [InlineData("10", SearchTermKind.Name, null)] // not shorthand for 0.0.0.10
    public void Terms_are_classified(string text, SearchTermKind kind, string? address)
    {
        var term = Assert.Single(NetworkSearchQuery.Parse(text).Terms);
        Assert.Equal(kind, term.Kind);
        Assert.Equal(address, term.Address?.ToString());
    }

    [Fact]
    public void A_network_keeps_its_prefix_and_contains_its_addresses()
    {
        var term = NetworkSearchQuery.ParseTerm("192.0.2.0/25");

        Assert.Equal(25, term.PrefixLength);
        Assert.True(NetworkSearchQuery.Contains(term.Address!, 25, System.Net.IPAddress.Parse("192.0.2.117")));
        Assert.False(NetworkSearchQuery.Contains(term.Address!, 25, System.Net.IPAddress.Parse("192.0.2.200")));
    }

    [Fact]
    public void A_pasted_list_becomes_separate_terms_without_repeats()
    {
        var query = NetworkSearchQuery.Parse("192.0.2.117, 00:00:5e:00:53:68\nfw-01.example.net 192.0.2.117");

        Assert.Equal([SearchTermKind.Ipv4Address, SearchTermKind.MacAddress, SearchTermKind.Name], query.Terms.Select(t => t.Kind));
    }

    [Fact]
    public void Text_with_spaces_stays_one_name()
    {
        var term = Assert.Single(NetworkSearchQuery.Parse("lobby display").Terms);

        Assert.Equal(SearchTermKind.Name, term.Kind);
        Assert.Equal("lobby display", term.Text);
    }

    [Fact]
    public void A_long_list_stops_at_the_limit_and_says_how_many_were_left()
    {
        var text = string.Join(' ', Enumerable.Range(1, 60).Select(i => $"192.0.2.{i}"));

        var query = NetworkSearchQuery.Parse(text);

        Assert.Equal(NetworkSearchQuery.MaxTerms, query.Terms.Count);
        Assert.Equal(10, query.IgnoredTerms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("\"\"")]
    public void An_empty_query_asks_for_the_overview(string? text) => Assert.True(NetworkSearchQuery.Parse(text).IsEmpty);
}
