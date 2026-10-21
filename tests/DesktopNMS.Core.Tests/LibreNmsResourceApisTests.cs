using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Core's resource APIs over another host's transport.</summary>
public class LibreNmsResourceApisTests
{
    [Fact]
    public async Task The_real_apis_answer_from_any_transport()
    {
        var server = new LibreNmsFixtureServer()
            .Serve("faults?state=-1", "26.10.0/faults.json")
            .Serve("faults?", "26.10.0/faults.json")
            .Serve("devices", "26.10.0/devices.json");
        var apis = new LibreNmsResourceApis(server);

        var faults = await apis.Alerts.ListAsync();
        var devices = await apis.Devices.ListAsync();

        Assert.All(faults, f => Assert.Equal(AlertSource.Fault, f.Source));
        Assert.Equal(2, devices.Count);
        Assert.Contains(server.Requests, r => r.StartsWith("faults?", StringComparison.Ordinal));
    }
}
