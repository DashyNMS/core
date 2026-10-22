using System.Reflection;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class SensorMonitorTests
{
    [Fact]
    public async Task A_slow_poll_skips_overlapping_refreshes_and_retains_readings_on_timeout()
    {
        var connection = new LibreNmsConnection(new Uri("https://nms.example.net/"), "example-token");
        var pending = new TaskCompletionSource<IReadOnlyList<Sensor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Func<Task<IReadOnlyList<Sensor>>> load = () => pending.Task;
        var api = Proxy<ISensorsApi>((_, _) => { calls++; return load(); });
        var client = Proxy<ILibreNmsClient>((method, _) => method.Name == "get_Connection" ? connection : api);
        var session = Proxy<IConnectionState>((_, _) => true);
        var devices = Proxy<IDeviceCache>((_, _) => Task.CompletedTask);
        var activity = Proxy<IAppActivity>((method, _) => method.Name == "get_IsInBackground" ? false : null);
        using var monitor = new SensorMonitor(client, session, null!, devices, null!, activity, NullLogger<SensorMonitor>.Instance);
        var results = new List<SensorPollResult>();
        monitor.Polled += (_, result) => results.Add(result);

        var first = monitor.PollOnceAsync(CancellationToken.None);
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Empty(results);
        pending.SetResult(new[] { new Sensor { SensorId = 1, DeviceId = 1 } });
        await first;
        var good = Assert.Single(results);
        Assert.True(good.Succeeded);
        Assert.NotNull(good.ObservedAt);

        load = () => Task.FromException<IReadOnlyList<Sensor>>(new LibreNmsApiException("Too slow") { IsTimeout = true });
        await monitor.PollOnceAsync(CancellationToken.None);
        var failed = results.Last();
        Assert.False(failed.Succeeded);
        Assert.True(failed.IsTimeout);
        Assert.Same(good.Sensors, failed.Sensors);
        Assert.Equal(good.ObservedAt, failed.ObservedAt);

        connection = new LibreNmsConnection(new Uri("https://other.example.net/"), "other-token");
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Empty(results.Last().Sensors);
        Assert.Null(results.Last().ObservedAt);
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)proxy).Handler = handler;
        return proxy;
    }
}
