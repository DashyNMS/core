using System.Net;
using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>LibreNMS 26.10's per-entity faults, against fixtures from both releases (#3, #10).</summary>
public class AlertFaultsTests
{
    [Fact]
    public async Task A_server_with_faults_is_listed_from_faults_and_marks_each_row()
    {
        var transport = new FixtureTransport().Probe(found: true).Serve("faults?", "26.10.0/faults.json");
        var api = new AlertsApi(transport);

        var alerts = await api.ListAsync();

        Assert.Equal(AlertSource.Fault, await api.GetSourceAsync());
        Assert.StartsWith("faults?", transport.Requests[^1]);
        Assert.All(alerts, a => Assert.Equal(AlertSource.Fault, a.Source));
        Assert.Equal(new[] { "fault:9001", "fault:9002" }, alerts.Select(a => a.Identity.ToString()));

        var first = alerts[0];
        Assert.Equal("processor", first.EntityType);
        Assert.Equal(120, first.EntityId);
        Assert.Equal("processor:120", first.EntityKey);
        Assert.Equal(new DateTime(2026, 10, 19, 9, 40, 0), first.FirstSeen);
        Assert.Equal(97, first.Details!.Value.GetProperty("rule")[0].GetProperty("processor_usage").GetInt32());
        Assert.Equal(AlertSeverity.Critical, first.Severity);
    }

    [Fact]
    public async Task A_server_without_faults_is_listed_from_alerts()
    {
        var transport = new FixtureTransport().Probe(found: false).Serve("alerts?", "26.9.1.1/alerts.json");
        var api = new AlertsApi(transport);

        var alert = Assert.Single(await api.ListAsync());

        Assert.StartsWith("alerts?", transport.Requests[^1]);
        Assert.Equal(AlertSource.Alert, alert.Source);
        Assert.Equal("alert:412", alert.Identity.ToString());
        Assert.Null(alert.EntityKey);
        Assert.Null(alert.Details);
    }

    [Fact]
    public async Task A_refused_probe_is_thrown_and_never_falls_back_to_alerts()
    {
        var transport = new FixtureTransport().Fail(AlertsApi.ProbeUrl, HttpStatusCode.Forbidden);
        var api = new AlertsApi(transport);

        var ex = await Assert.ThrowsAsync<LibreNmsApiException>(() => api.ListAsync());

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.DoesNotContain(transport.Requests, r => r.StartsWith("alerts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_source_is_probed_once_per_connection()
    {
        var transport = new FixtureTransport().Probe(found: true).Serve("faults?", "26.10.0/faults.json");
        var api = new AlertsApi(transport);

        await api.ListAsync();
        await api.ListAsync();
        Assert.Equal(1, transport.Requests.Count(r => r == AlertsApi.ProbeUrl));

        transport.Reconnect();
        await api.ListAsync();
        Assert.Equal(2, transport.Requests.Count(r => r == AlertsApi.ProbeUrl));
    }

    [Fact]
    public async Task Actions_go_to_the_faults_routes_on_a_faults_server()
    {
        var transport = new FixtureTransport().Probe(found: true);
        var api = new AlertsApi(transport);

        await api.AcknowledgeAsync(9001, "checking");
        await api.UnmuteAsync(9001);

        Assert.Contains("PUT faults/9001", transport.Requests);
        Assert.Contains("PUT faults/unmute/9001", transport.Requests);
    }

    [Fact]
    public async Task Fault_rows_from_alerts_are_still_marked_as_faults()
    {
        // Belt and braces: the probe said legacy, but the rows carry fault columns.
        var transport = new FixtureTransport().Probe(found: false).Serve("alerts?", "26.10.0/faults.json");
        var api = new AlertsApi(transport);

        var alerts = await api.ListAsync();

        Assert.All(alerts, a => Assert.Equal(AlertSource.Fault, a.Source));
        Assert.Equal(AlertSource.Fault, await api.GetSourceAsync());
    }

    [Fact]
    public async Task The_alert_log_carries_fault_ids_from_26_10()
    {
        var legacy = await LibreNmsFixtures.Read<AlertLogEntry>("26.9.1.1/alertlog.json", "logs");
        var current = await LibreNmsFixtures.Read<AlertLogEntry>("26.10.0/alertlog.json", "logs");

        Assert.Null(Assert.Single(legacy).FaultId);
        Assert.Equal(new int?[] { 9002, 9001, null }, current.Select(e => e.FaultId));
    }

    [Theory]
    [InlineData("fault:12", AlertSource.Fault, 12)]
    [InlineData("alert:12", AlertSource.Alert, 12)]
    [InlineData("12", AlertSource.Alert, 12)]
    [InlineData(" fault:7 ", AlertSource.Fault, 7)]
    public void Alert_keys_round_trip(string text, AlertSource source, int id)
    {
        Assert.True(AlertKey.TryParse(text, out var key));
        Assert.Equal(new AlertKey(source, id), key);
        Assert.True(AlertKey.TryParse(key.ToString(), out var again));
        Assert.Equal(key, again);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fault:")]
    [InlineData("fault:0")]
    [InlineData("fault:-3")]
    [InlineData("rule:12")]
    [InlineData("fault:12x")]
    public void Bad_alert_keys_are_rejected(string? text) => Assert.False(AlertKey.TryParse(text, out _));

    [Fact]
    public void Old_alert_12_and_fault_12_are_different_alerts()
        => Assert.NotEqual(new AlertKey(AlertSource.Alert, 12), new AlertKey(AlertSource.Fault, 12));

    [Theory]
    [InlineData("faults/9001")]
    [InlineData("faults/unmute/9001")]
    public void Fault_actions_need_the_acknowledge_permission(string url)
        => Assert.Equal(ApiPermission.AcknowledgeAlerts, ApiPermissions.For(HttpMethod.Put, url));

    /// <summary>Serves fixture files by URL prefix and records each request.</summary>
    private sealed class FixtureTransport : ILibreNmsTransport
    {
        private readonly List<(string Prefix, string? File, HttpStatusCode? Failure)> _routes = new();

        public LibreNmsConnection? Connection { get; private set; } = new(new Uri("https://nms.example.net/"), "token");

        public List<string> Requests { get; } = new();

        public FixtureTransport Probe(bool found) => found
            ? Serve(AlertsApi.ProbeUrl, null)
            : Fail(AlertsApi.ProbeUrl, HttpStatusCode.NotFound);

        public FixtureTransport Serve(string prefix, string? file)
        {
            _routes.Add((prefix, file, null));
            return this;
        }

        public FixtureTransport Fail(string prefix, HttpStatusCode status)
        {
            _routes.Add((prefix, null, status));
            return this;
        }

        public void Reconnect() => Connection = new LibreNmsConnection(new Uri("https://nms.example.net/"), "token");

        public async Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default)
        {
            Requests.Add(relativeUrl);
            var route = _routes.FirstOrDefault(r => relativeUrl.StartsWith(r.Prefix, StringComparison.Ordinal));
            if (route.Failure is { } status)
            {
                throw new LibreNmsApiException("failed", status, status == HttpStatusCode.NotFound ? "This API route doesn't exist." : "Insufficient permissions");
            }

            if (route.Prefix is null)
            {
                throw new InvalidOperationException("No fixture for " + relativeUrl);
            }

            return route.File is null ? Array.Empty<T>() : await LibreNmsFixtures.Read<T>(route.File, collectionProperty);
        }

        public Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default)
        {
            Requests.Add($"{method} {relativeUrl}");
            return Task.FromResult(JsonDocument.Parse("{\"status\":\"ok\"}"));
        }

        public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
