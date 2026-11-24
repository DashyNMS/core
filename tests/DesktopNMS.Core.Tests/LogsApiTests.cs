using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class LogsApiTests
{
    [Fact]
    public async Task Event_log_for_one_device_puts_the_device_in_the_path()
    {
        var transport = new RecordingTransport();

        await new LogsApi(transport).ListEventLogAsync(42, 25);

        Assert.Equal("logs/eventlog/42?limit=25&sortorder=DESC", transport.Url);
    }

    [Fact]
    public async Task Event_log_for_every_device_leaves_the_device_out()
    {
        var transport = new RecordingTransport();

        await new LogsApi(transport).ListEventLogAsync(null, 25);

        Assert.Equal("logs/eventlog?limit=25&sortorder=DESC", transport.Url);
    }

    [Fact]
    public async Task Alert_log_for_every_device_leaves_the_device_out()
    {
        var transport = new RecordingTransport();

        await new LogsApi(transport).ListAlertLogAsync(null, 10);

        Assert.Equal("logs/alertlog?limit=10&sortorder=DESC", transport.Url);
    }

    [Theory]
    [InlineData(42, "logs/eventlog/42?limit=1000&sortorder=DESC&to=2026-10-01%2008%3A12%3A41")]
    [InlineData(null, "logs/eventlog?limit=1000&sortorder=DESC&to=2026-10-01%2008%3A12%3A41")]
    public async Task Event_log_up_to_a_time_passes_the_time_back_as_it_came(int? deviceId, string expected)
    {
        var transport = new RecordingTransport();
        var entry = JsonSerializer.Deserialize<EventLogEntry>("""{"event_id":901,"datetime":"2026-10-01 08:12:41"}""", LibreNmsJson.Options)!;

        await new LogsApi(transport).ListEventLogUpToAsync(deviceId, entry.Timestamp!.Value, 1000);

        Assert.Equal(expected, transport.Url);
    }

    [Theory]
    [InlineData("2026-10-01 08:12:41.500")]
    [InlineData("2026-10-01 08:12:42Z")]
    public async Task A_time_up_to_is_rounded_up_to_a_second_and_never_shifted(string time)
    {
        var transport = new RecordingTransport();
        var entry = JsonSerializer.Deserialize<EventLogEntry>($$"""{"event_id":901,"datetime":"{{time}}"}""", LibreNmsJson.Options)!;

        await new LogsApi(transport).ListEventLogUpToAsync(null, entry.Timestamp!.Value, 10);

        Assert.Equal("logs/eventlog?limit=10&sortorder=DESC&to=2026-10-01%2008%3A12%3A42", transport.Url);
    }

    [Theory]
    [InlineData(7, "logs/alertlog/7?limit=1&sortorder=DESC&to=2026-10-01%2008%3A12%3A41")]
    [InlineData(null, "logs/alertlog?limit=1&sortorder=DESC&to=2026-10-01%2008%3A12%3A41")]
    public async Task Alert_log_up_to_a_time_passes_the_time_back_as_it_came(int? deviceId, string expected)
    {
        var transport = new RecordingTransport();
        var entry = JsonSerializer.Deserialize<AlertLogEntry>("""{"id":901,"time_logged":"2026-10-01 08:12:41"}""", LibreNmsJson.Options)!;

        // A limit below one is one, as for the other log reads.
        await new LogsApi(transport).ListAlertLogUpToAsync(deviceId, entry.TimeLogged!.Value, 0);

        Assert.Equal(expected, transport.Url);
    }

    [Fact]
    public async Task The_last_time_there_is_can_be_asked_for()
    {
        var transport = new RecordingTransport();

        await new LogsApi(transport).ListEventLogUpToAsync(null, DateTime.MaxValue, 10);

        Assert.Equal("logs/eventlog?limit=10&sortorder=DESC&to=9999-12-31%2023%3A59%3A59", transport.Url);
    }

    [Fact]
    public async Task An_implementation_that_predates_reading_up_to_a_time_says_it_cannot()
    {
        ILogsApi logs = new PlainLogs();

        Assert.False(logs.CanReadUpTo);
        Assert.True(((ILogsApi)new LogsApi(new RecordingTransport())).CanReadUpTo);
        await Assert.ThrowsAsync<NotSupportedException>(() => logs.ListEventLogUpToAsync(null, DateTime.UnixEpoch, 1));
        await Assert.ThrowsAsync<NotSupportedException>(() => logs.ListAlertLogUpToAsync(null, DateTime.UnixEpoch, 1));
    }

    [Fact]
    public void A_fleet_wide_event_log_entry_names_its_device()
    {
        // The shape LibreNMS's list_logs returns with no device: the eventlog
        // row plus the joined hostname and sysName.
        const string json = """
            {"hostname":"sw-core-01.example.net","sysName":"sw-core-01","host":7,"event_id":901,"device_id":7,
             "datetime":"2026-10-01 08:12:41","message":"Interface went down","type":"interface","username":"","severity":4}
            """;

        var entry = JsonSerializer.Deserialize<EventLogEntry>(json, LibreNmsJson.Options)!;

        Assert.Equal(7, entry.DeviceId);
        Assert.Equal("sw-core-01.example.net", entry.Hostname);
        Assert.Equal("sw-core-01", entry.SysName);
    }

    /// <summary>An implementation written before CanReadUpTo, such as a host's bounded wrapper.</summary>
    private sealed class PlainLogs : ILogsApi
    {
        public Task<IReadOnlyList<AlertLogEntry>> ListAlertLogAsync(int? deviceId, int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlertLogEntry>>([]);

        public Task<AlertLogEntry?> GetLatestForRuleAsync(int deviceId, int ruleId, int searchDepth = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<AlertLogEntry?>(null);

        public Task<IReadOnlyList<EventLogEntry>> ListEventLogAsync(int? deviceId, int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EventLogEntry>>([]);
    }

    private sealed class RecordingTransport : ILibreNmsTransport
    {
        public string? Url { get; private set; }

        public LibreNmsConnection? Connection => null;

        public Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default)
            => Task.FromResult(JsonDocument.Parse("{\"status\":\"ok\"}"));

        public Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default)
        {
            Url = relativeUrl;
            return Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
        }

        public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }
}
