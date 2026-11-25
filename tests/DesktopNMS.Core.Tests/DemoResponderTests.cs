using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Demo;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>The demo's example network (#119), driven through Core's own client with no listener and no network.</summary>
public sealed class DemoResponderTests : IDisposable
{
    private readonly DemoResponder _demo = new(time: new FixedTime());
    private readonly LibreNmsTransport _transport = new(NullLogger<LibreNmsTransport>.Instance);
    private readonly LibreNmsClient _client;

    public DemoResponderTests()
    {
        _client = new LibreNmsClient(_transport, NullLogger<LibreNmsClient>.Instance);
        _client.Connect(_demo.Connection());
    }

    public void Dispose()
    {
        _client.Dispose();
        _transport.Dispose();
    }

    [Fact]
    public async Task Alerts_come_from_alerts_because_faults_answers_404()
    {
        var alerts = await _client.Alerts.ListAsync();

        Assert.Equal(AlertSource.Alert, await _client.Alerts.GetSourceAsync());
        Assert.Equal(10, alerts.Count);
        Assert.Equal(2, alerts.Count(alert => alert.State == AlertState.Acknowledged));
        Assert.Equal(404, _demo.Respond("GET", "/api/v0/faults").Status);
    }

    [Fact]
    public async Task Every_area_a_demo_shows_has_example_data()
    {
        var devices = await _client.Devices.ListAsync();
        var core = devices.Single(device => device.DeviceId == _demo.FeaturedDeviceId);

        Assert.Equal(24, devices.Count);
        Assert.Equal(_demo.DeviceCount, devices.Count);
        Assert.All(devices, device => Assert.EndsWith(".example.net", device.Hostname!, StringComparison.Ordinal));
        Assert.Contains(devices, device => device.Status == false);
        Assert.NotEmpty(await _client.Ports.ListForDeviceAsync(core.DeviceId));
        Assert.NotEmpty(await _client.Links.ListAllAsync());
        Assert.NotEmpty(await _client.Sensors.ListAsync());
        Assert.Equal(4, (await _client.Locations.ListAsync()).Count);
        Assert.Equal(3, (await _client.DeviceGroups.ListAsync()).Count);
        Assert.Equal(10, (await _client.Rules.ListAsync()).Count);
        Assert.Equal(DemoResponder.ServerVersion, (await _client.System.GetAsync()).LocalVersion);
        Assert.Equal(5, _demo.PinnedDevices.Count);
        Assert.All(_demo.PinnedSensors, sensor => Assert.Contains(devices, device => device.DeviceId == sensor.DeviceId));
    }

    [Fact]
    public async Task Graphs_are_svg_the_same_each_time_and_drop_their_legend_when_asked()
    {
        var id = _demo.FeaturedDeviceId;
        var first = await _client.Graphs.GetSvgAsync(id, "device_bits", GraphTimeRange.LastDay, 600, 240);
        var again = await _client.Graphs.GetSvgAsync(id, "device_bits", GraphTimeRange.LastDay, 600, 240);
        var bare = await _client.Graphs.GetSvgAsync(id, "device_bits", GraphTimeRange.LastDay, 600, 240, legend: false);

        Assert.StartsWith("<svg", first, StringComparison.Ordinal);
        Assert.Contains("width=\"600\" height=\"240\"", first, StringComparison.Ordinal);
        Assert.Equal(first, again);
        Assert.Contains("class=\"legend\"", first, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"legend\"", bare, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_search_finds_an_endpoint_and_the_address_two_macs_answer_for()
    {
        var search = new NetworkSearch(_client);

        var one = await search.SearchAsync("192.0.2.117");
        var conflict = await search.SearchAsync("192.0.2.200");

        Assert.Equal(NetworkSearchOutcome.Single, one.Outcome);
        Assert.NotEmpty(one.Endpoints);
        Assert.NotEmpty(conflict.Endpoints);
    }

    [Theory]
    [InlineData("POST", "/api/v0/devices/1/maintenance")]
    [InlineData("PUT", "/api/v0/alerts/500")]
    [InlineData("DELETE", "/api/v0/devices/1")]
    public void Writes_are_accepted_and_change_nothing(string method, string path)
    {
        var before = _demo.Respond("GET", "/api/v0/devices").Body;

        Assert.Equal(200, _demo.Respond(method, path, "{\"note\":\"x\"}").Status);
        Assert.Equal(before, _demo.Respond("GET", "/api/v0/devices").Body);
    }

    [Theory]
    [InlineData("/api/v0/devices/9999", 404, false)]
    [InlineData("/api/v0/not-a-thing", 200, true)]
    [InlineData("/elsewhere", 404, false)]
    [InlineData("/api/v0/devices/1/health/processor/notanumber", 400, true)]
    [InlineData("/api/v0/ports/999999", 404, false)]
    [InlineData("/api/v0/resources/ip/networks/999/ip", 404, false)]
    [InlineData("/api/v0/resources/ip/arp/192.0.2.0/-1", 422, false)]
    [InlineData("/api/v0/devices/core-sw-01.example.net", 200, false)]
    public void Unknown_paths_answer_as_LibreNMS_would_and_say_so(string path, int status, bool unhandled)
    {
        var answer = _demo.Respond("GET", path);

        Assert.Equal(status, answer.Status);
        Assert.Equal(unhandled, answer.Unhandled);
        Assert.Equal("application/json", answer.ContentType);
        using var _ = JsonDocument.Parse(answer.Body);
    }

    [Fact]
    public void Only_documentation_names_and_addresses_appear()
    {
        foreach (var path in new[] { "/api/v0/devices", "/api/v0/resources/links", "/api/v0/resources/ip/arp/all", "/api/v0/logs/eventlog" })
        {
            using var json = JsonDocument.Parse(_demo.Respond("GET", path).Body);
            foreach (var (name, text) in Strings(json.RootElement, string.Empty))
            {
                if (name is "ip" or "ipv4_address")
                {
                    Assert.True(text.StartsWith("192.0.2.", StringComparison.Ordinal) || text.StartsWith("198.51.100.", StringComparison.Ordinal)
                        || text.StartsWith("203.0.113.", StringComparison.Ordinal) || text.StartsWith("2001:db8:", StringComparison.Ordinal), $"{path}: {text}");
                }

                if (text.EndsWith(".net", StringComparison.Ordinal) || text.EndsWith(".org", StringComparison.Ordinal) || text.EndsWith(".com", StringComparison.Ordinal))
                {
                    Assert.True(text.EndsWith("example.net", StringComparison.Ordinal) || text.EndsWith("example.org", StringComparison.Ordinal), $"{path}: {text}");
                }
            }
        }
    }

    [Fact]
    public async Task A_larger_fleet_is_opt_in_the_same_for_the_same_seed_and_in_IPv6_documentation_space()
    {
        var big = new DemoResponder(new DemoFleetOptions(ExtraDevices: 2000), new FixedTime());
        var same = new DemoResponder(new DemoFleetOptions(ExtraDevices: 2000), new FixedTime());
        _client.Connect(big.Connection());

        var devices = await _client.Devices.ListAsync();
        var ports = await _client.Ports.ListAllStatusAsync();

        Assert.Equal(2024, devices.Count);
        Assert.True(ports.Count > 20_000);
        Assert.Contains(devices, device => device.Status == false && device.Hostname!.StartsWith("lab-sw-", StringComparison.Ordinal));
        Assert.All(devices.Where(device => device.Hostname!.StartsWith("lab-sw-", StringComparison.Ordinal)), device => Assert.StartsWith("2001:db8:", device.Ip!, StringComparison.Ordinal));
        Assert.Equal(big.Respond("GET", "/api/v0/alerts").Body, same.Respond("GET", "/api/v0/alerts").Body);
        Assert.Equal(24, new DemoResponder().DeviceCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DemoResponder(new DemoFleetOptions(ExtraDevices: -1)));
    }

    [Fact]
    public void A_larger_fleet_leaves_the_default_network_as_it_is_and_keeps_its_switches_within_limits()
    {
        var big = new DemoResponder(new DemoFleetOptions(ExtraDevices: 500), new FixedTime());
        using var all = JsonDocument.Parse(big.Respond("GET", "/api/v0/devices").Body);
        using var sensors = JsonDocument.Parse(big.Respond("GET", "/api/v0/resources/sensors").Body);
        var defaultIds = Enumerable.Range(1, 24).ToHashSet();

        // The default devices, their sensors and their alerts answer exactly as without the extra switches.
        Assert.Equal(Only(_demo, "/api/v0/devices", "devices", defaultIds), Only(big, "/api/v0/devices", "devices", defaultIds));
        Assert.Equal(Only(_demo, "/api/v0/resources/sensors", "sensors", defaultIds), Only(big, "/api/v0/resources/sensors", "sensors", defaultIds));
        Assert.Equal(_demo.PinnedSensors, big.PinnedSensors);

        var extra = sensors.RootElement.GetProperty("sensors").EnumerateArray().Where(s => s.GetProperty("device_id").GetInt32() > 24).ToList();
        Assert.Equal(1000, extra.Count);
        var over = extra.Where(s => s.GetProperty("sensor_current").ValueKind == JsonValueKind.Number
            && s.GetProperty("sensor_current").GetDouble() >= s.GetProperty("sensor_limit").GetDouble()).ToList();
        Assert.InRange(over.Count, 1, 10);
        using var alerts = JsonDocument.Parse(big.Respond("GET", "/api/v0/alerts").Body);
        Assert.Equal(over.Count, alerts.RootElement.GetProperty("alerts").EnumerateArray().Count(a => a.GetProperty("rule_id").GetInt32() == 2 && a.GetProperty("device_id").GetInt32() > 24));
        Assert.All(over, s => Assert.Equal("Inlet", s.GetProperty("sensor_descr").GetString()));
        Assert.All(all.RootElement.GetProperty("devices").EnumerateArray(),
            d => Assert.True(string.CompareOrdinal(d.GetProperty("inserted").GetString(), "2026-11-02") < 0));
        Assert.Equal(
            big.Respond("GET", "/api/v0/ports").Body.Split("ifPhysAddress").Length - 1,
            JsonDocument.Parse(big.Respond("GET", "/api/v0/ports").Body).RootElement.GetProperty("ports").EnumerateArray().Select(p => p.GetProperty("ifPhysAddress").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task The_connection_test_answers_from_the_demo_too()
    {
        var result = await _client.TestAsync(_demo.Connection());

        Assert.True(result.Succeeded);
        Assert.Equal(DemoResponder.ServerVersion, result.SystemInfo!.LocalVersion);
    }

    [Fact]
    public void A_connection_keeps_its_handler_through_a_new_timeout_or_certificate_and_another_never_has_one()
    {
        var demo = _demo.Connection();

        Assert.NotNull(demo.WithTimeout(60).Handler);
        Assert.NotNull(demo.WithTrustedCertificate("AB").Handler);
        Assert.Null(new LibreNmsConnection(DemoResponder.Address, DemoResponder.Token).Handler);
        Assert.Null(demo.WithHandler(null).Handler);
    }

    [Fact]
    public async Task The_transport_keeps_the_demo_through_a_rebuild_and_drops_it_for_another_connection()
    {
        _client.SetTimeout(60);
        _client.TrustCertificate("AB");
        Assert.Equal(24, (await _client.Devices.ListAsync()).Count);

        // Another connection, answered by another in-process network, gets that one's answers.
        var bigger = new DemoResponder(new DemoFleetOptions(ExtraDevices: 10), new FixedTime());
        _client.Connect(bigger.Connection());
        Assert.Equal(34, (await _client.Devices.ListAsync()).Count);

        Assert.Null(new LibreNmsConnection(DemoResponder.Address, "x", backupWebRoot: new Uri("https://backup.example.net/")).WithHandler(() => new DemoMessageHandler(_demo)).BackupWebRoot);
    }

    [Fact]
    public async Task Requests_can_be_answered_at_once()
    {
        var answers = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() => _demo.Respond("GET", i % 2 == 0 ? "/api/v0/devices" : "/api/v0/devices/1/device_bits?from=-1d&width=300&height=120"))));

        Assert.All(answers, answer => Assert.Equal(200, answer.Status));
        Assert.Single(answers.Where((_, i) => i % 2 == 0).Select(a => a.Body).Distinct());
        Assert.All(answers.Where((_, i) => i % 2 == 1), answer => Assert.Equal("image/svg+xml", answer.ContentType));
        Assert.Single(answers.Where((_, i) => i % 2 == 1).Select(a => a.Body).Distinct());
    }

    private static List<string> Only(DemoResponder demo, string path, string property, HashSet<int> deviceIds)
    {
        using var json = JsonDocument.Parse(demo.Respond("GET", path).Body);
        return json.RootElement.GetProperty(property).EnumerateArray()
            .Where(item => deviceIds.Contains(item.GetProperty("device_id").GetInt32()))
            .Select(item => item.GetRawText())
            .ToList();
    }

    [Fact]
    public async Task The_handler_never_needs_a_server_whatever_address_it_is_given()
    {
        using var http = new HttpClient(new DemoMessageHandler(_demo));

        var answer = await http.GetStringAsync(new Uri("https://unreachable.invalid/api/v0/devices"));

        Assert.Contains("core-sw-01", answer, StringComparison.Ordinal);
    }

    private static IEnumerable<(string Name, string Text)> Strings(JsonElement element, string name) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Strings(property.Value, property.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(item => Strings(item, name)),
        JsonValueKind.String => [(name, element.GetString()!)],
        _ => [],
    };

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 2, 14, 30, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
