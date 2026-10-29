using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Device fields against LibreNMS 26.10's device resource (#8, librenms#20595).</summary>
public class DeviceResourceTests
{
    /// <summary>Fields the 26.10 resource leaves out, each read as unknown rather than false.</summary>
    private static readonly HashSet<string> NotInResource = new() { "override_sysContact" };

    [Fact]
    public async Task Every_device_field_Core_reads_is_in_the_26_10_resource()
    {
        var keys = await FixtureKeys("26.10.0/devices.json");

        var read = typeof(Device).GetProperties()
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .OfType<string>()
            .ToList();

        var missing = read.Where(name => !keys.Contains(name) && !NotInResource.Contains(name)).ToList();
        Assert.True(missing.Count == 0, "Not in the 26.10 device resource: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task The_sysContact_override_is_unknown_on_26_10_not_off()
    {
        var current = await LibreNmsFixtures.Read<Device>("26.10.0/devices.json", "devices");
        var legacy = Assert.Single(await LibreNmsFixtures.Read<Device>("26.9.1.1/devices.json", "devices"));

        Assert.All(current, d =>
        {
            Assert.Null(d.OverrideSysContact);
            Assert.False(d.IsSysContactOverrideKnown);
        });
        Assert.True(legacy.OverrideSysContact);
        Assert.True(legacy.IsSysContactOverrideKnown);
    }

    [Fact]
    public async Task Devices_read_the_same_from_both_releases()
    {
        var current = (await LibreNmsFixtures.Read<Device>("26.10.0/devices.json", "devices")).Single(d => d.DeviceId == 7);
        var legacy = Assert.Single(await LibreNmsFixtures.Read<Device>("26.9.1.1/devices.json", "devices"));

        foreach (var device in new[] { current, legacy })
        {
            Assert.Equal("core-sw-01", device.SysName);
            Assert.Equal(DeviceState.Up, device.State);
            Assert.Equal("noc@example.net", device.Contact);
            Assert.Equal("Rack 3", device.Location);
            Assert.Equal(51.5, device.Latitude);
            Assert.Equal(8640000, device.Uptime);
            Assert.NotNull(device.LastPolled);
            Assert.False(device.OverrideSysLocation);
        }
    }

    [Fact]
    public async Task A_ping_only_device_reads_its_state_and_parent()
    {
        var camera = (await LibreNmsFixtures.Read<Device>("26.10.0/devices.json", "devices")).Single(d => d.DeviceId == 22);

        Assert.Equal(DeviceState.Down, camera.State);
        Assert.Equal(0, camera.Uptime);
        Assert.Equal("core-sw-01.example.net", camera.DependencyParentHostname);
        Assert.Equal("cam-gate.example.net", camera.BestName);
    }

    [Fact]
    public void Omitted_device_readings_remain_unknown_and_round_trip_without_defaults()
    {
        var device = JsonSerializer.Deserialize<Device>("""{"device_id":7,"hostname":"example.net"}""", Json.LibreNmsJson.Options)!;
        Assert.Null(device.ReportedStatus);
        Assert.Null(device.ReportedDisabled);
        Assert.Null(device.ReportedIgnore);
        Assert.Null(device.ReportedUptime);
        Assert.Null(device.ReportedIgnoreStatus);
        Assert.Null(device.ReportedOverrideSysLocation);
        Assert.Null(device.ReportedPollerGroup);
        Assert.Equal(DeviceState.Unknown, device.ReportedState);
        var copy = JsonSerializer.Deserialize<Device>(JsonSerializer.Serialize(device, Json.LibreNmsJson.Options), Json.LibreNmsJson.Options)!;
        Assert.Null(copy.ReportedStatus);
        Assert.Null(copy.ReportedUptime);
    }

    [Fact]
    public void Explicit_false_and_zero_are_known_and_legacy_setters_keep_them_known()
    {
        var device = JsonSerializer.Deserialize<Device>("""{"device_id":7,"status":0,"disabled":"0","ignore":false,"uptime":0}""", Json.LibreNmsJson.Options)!;
        Assert.Equal(false, device.ReportedStatus);
        Assert.Equal(0L, device.ReportedUptime);
        Assert.Equal(DeviceState.Down, device.ReportedState);
        device.Status = true;
        device.Uptime = 60;
        Assert.Equal(true, device.ReportedStatus);
        Assert.Equal(60L, device.ReportedUptime);
        Assert.Equal(DeviceState.Up, device.ReportedState);
    }

    [Fact]
    public void Null_flags_are_unknown_and_known_disabling_flags_take_precedence()
    {
        var device = JsonSerializer.Deserialize<Device>("""{"device_id":7,"status":null,"disabled":null,"ignore":null,"uptime":null}""", Json.LibreNmsJson.Options)!;
        Assert.Equal(DeviceState.Unknown, device.ReportedState);
        Assert.Null(device.ReportedUptime);
        device.Disabled = true;
        Assert.Equal(DeviceState.Disabled, device.ReportedState);
        device.Disabled = false;
        device.Ignore = true;
        Assert.Equal(DeviceState.Ignored, device.ReportedState);
    }

    private static async Task<HashSet<string>> FixtureKeys(string file)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LibreNMS", file)));
        return document.RootElement.GetProperty("devices")
            .EnumerateArray()
            .SelectMany(d => d.EnumerateObject().Select(p => p.Name))
            .ToHashSet();
    }
}
