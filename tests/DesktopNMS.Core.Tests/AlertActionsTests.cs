using System.Net;
using System.Text.Json;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Acknowledging with the scope LibreNMS 26.10 applies (#6).</summary>
public class AlertActionsTests
{
    private static readonly AlertGroupKey Cores = new(7, 3);

    [Fact]
    public async Task Without_per_entity_one_write_acknowledges_the_whole_group()
    {
        var server = new FakeServer(perEntity: false, faults: 4);

        var result = await Actions(server).AcknowledgeGroupAsync(Cores, "checking");

        Assert.Single(server.Writes);
        Assert.Equal(4, result.Changed.Count);
        Assert.True(result.Succeeded);
        Assert.True(result.CanUndo);
        Assert.Equal("Acknowledged 4 faults", result.Summary);
        Assert.All(server.Faults, f => Assert.Equal(1, f.NoteCount));
    }

    [Fact]
    public async Task Per_entity_within_its_limit_writes_each_fault_once()
    {
        var server = new FakeServer(perEntity: true, maxEntities: 4, faults: 4);

        var result = await Actions(server).AcknowledgeGroupAsync(Cores, "checking");

        Assert.Equal(4, server.Writes.Count);
        Assert.Equal(4, server.Writes.Distinct().Count());
        Assert.Equal(4, result.Changed.Count);
        Assert.All(server.Faults, f => Assert.Equal(1, f.NoteCount));
    }

    [Fact]
    public async Task Only_faults_still_needing_it_are_written()
    {
        var server = new FakeServer(perEntity: true, maxEntities: 20, faults: 3);
        server.Faults[1].State = AlertState.Acknowledged;

        var result = await Actions(server).AcknowledgeGroupAsync(Cores, "checking");

        Assert.Equal(2, server.Writes.Count);
        Assert.Single(result.AlreadyDone);
        Assert.Equal("Acknowledged 2 faults", result.Summary);
    }

    [Theory]
    [InlineData(true, 3, 4, false)]   // limit below the open count
    [InlineData(true, 4, 4, true)]    // equal
    [InlineData(true, 5, 4, true)]    // above
    [InlineData(false, 20, 4, false)] // per-entity off
    public async Task A_single_fault_is_offered_only_when_LibreNMS_would_act_on_it_alone(bool perEntity, int limit, int faults, bool single)
    {
        var server = new FakeServer(perEntity, limit, faults);

        var scope = await Actions(server).GetFaultScopeAsync(server.Faults[0].ToAlert());

        Assert.Equal(single, scope.IsSingleFault);
    }

    [Fact]
    public async Task An_inherited_limit_cant_be_confirmed()
    {
        var server = new FakeServer(perEntity: true, maxEntities: null, faults: 3);

        var result = await Actions(server).AcknowledgeFaultAsync(server.Faults[0].ToAlert(), "checking");

        Assert.Empty(server.Writes);
        Assert.NotNull(result.RefusedScope);
        Assert.Contains("can't be confirmed", result.Summary);
        Assert.False(result.CanUndo);
    }

    [Fact]
    public async Task The_only_open_fault_can_always_be_acted_on_alone()
    {
        var server = new FakeServer(perEntity: false, faults: 1);

        var result = await Actions(server).AcknowledgeFaultAsync(server.Faults[0].ToAlert(), "checking");

        Assert.True(result.Succeeded);
        Assert.Equal("Acknowledged 1 fault", result.Summary);
    }

    [Fact]
    public async Task A_confirmed_single_fault_changes_only_that_fault()
    {
        var server = new FakeServer(perEntity: true, maxEntities: 20, faults: 3);

        var result = await Actions(server).AcknowledgeFaultAsync(server.Faults[1].ToAlert(), "checking");

        Assert.Equal(new[] { server.Faults[1].Id }, server.Writes);
        Assert.Equal(server.Faults[1].Id, Assert.Single(result.Changed).Id);
        Assert.Equal(AlertState.Active, server.Faults[0].State);
    }

    [Fact]
    public async Task Settings_changing_mid_action_report_what_actually_changed()
    {
        // The rule reads per-entity, but by the time the write lands it isn't.
        var server = new FakeServer(perEntity: true, maxEntities: 20, faults: 3) { ActsPerEntity = false };

        var result = await Actions(server).AcknowledgeFaultAsync(server.Faults[0].ToAlert(), "checking");

        Assert.Equal(3, result.Changed.Count);
        Assert.Equal("Acknowledged 3 faults", result.Summary);
    }

    [Fact]
    public async Task A_partial_failure_reports_both_halves()
    {
        var server = new FakeServer(perEntity: true, maxEntities: 20, faults: 3);
        server.FailWritesTo.Add(server.Faults[2].Id);

        var result = await Actions(server).AcknowledgeGroupAsync(Cores, "checking");

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Changed.Count);
        Assert.Single(result.Unchanged);
        Assert.StartsWith("Acknowledged 2 faults; 1 fault couldn't be acknowledged", result.Summary);
        Assert.True(result.CanUndo);
    }

    [Fact]
    public async Task A_fault_that_recovers_before_the_write_is_reported_as_gone()
    {
        var server = new FakeServer(perEntity: true, maxEntities: 20, faults: 1);
        var fault = server.Faults[0].ToAlert();
        server.RecoverBeforeWrite = true;

        var result = await Actions(server).AcknowledgeGroupAsync(Cores, "checking");

        Assert.Empty(result.Changed);
        Assert.Equal(fault.Id, Assert.Single(result.Gone).Id);
        Assert.False(result.CanUndo);
        Assert.Equal("1 fault no longer open", result.Summary);
    }

    [Fact]
    public async Task Unacknowledging_a_group_returns_the_acknowledged_faults()
    {
        var server = new FakeServer(perEntity: false, faults: 3);
        server.Faults[0].State = AlertState.Acknowledged;
        server.Faults[1].State = AlertState.Acknowledged;

        var result = await Actions(server).UnacknowledgeGroupAsync(Cores, "back");

        Assert.Single(server.Writes);
        Assert.Equal(2, result.Changed.Count);
        Assert.Equal("Returned to active 2 faults", result.Summary);
    }

    [Theory]
    [InlineData("No Alert by that ID")]
    [InlineData("No alert by that ID")]
    public async Task No_alert_by_that_ID_is_a_missing_target_not_success(string message)
    {
        var api = new AlertsApi(new MessageTransport(message));

        var ex = await Assert.ThrowsAsync<LibreNmsApiException>(() => api.AcknowledgeAsync(12, "x"));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        await Assert.ThrowsAsync<LibreNmsApiException>(() => api.UnmuteAsync(12, "x"));
    }

    [Fact]
    public async Task A_real_acknowledgement_still_succeeds()
        => await new AlertsApi(new MessageTransport("Alert has been acknowledged")).AcknowledgeAsync(12, "x");

    [Fact]
    public async Task Rules_read_their_per_entity_settings()
    {
        var rule = JsonSerializer.Deserialize<AlertRule>("{\"id\":3,\"notify_per_entity\":true,\"max_entities\":null}", Json.LibreNmsJson.Options)!;
        var legacy = JsonSerializer.Deserialize<AlertRule>("{\"id\":3}", Json.LibreNmsJson.Options)!;
        var numeric = JsonSerializer.Deserialize<AlertRule>("{\"id\":3,\"notify_per_entity\":1,\"max_entities\":\"8\"}", Json.LibreNmsJson.Options)!;

        Assert.True(rule.NotifyPerEntity);
        Assert.Null(rule.MaxEntities);
        Assert.Null(legacy.NotifyPerEntity);
        Assert.True(numeric.NotifyPerEntity);
        Assert.Equal(8, numeric.MaxEntities);
    }

    private static AlertActions Actions(FakeServer server) => new(server, server);

    /// <summary>LibreNMS 26.10's ack_alert and unmute_alert, after api_alert_fault_action_targets.</summary>
    private sealed class FakeServer : IAlertsApi, IAlertRulesApi
    {
        private readonly bool _perEntity;
        private readonly int? _maxEntities;

        public FakeServer(bool perEntity, int? maxEntities = 20, int faults = 4)
        {
            _perEntity = perEntity;
            _maxEntities = maxEntities;
            ActsPerEntity = perEntity;
            Faults = Enumerable.Range(0, faults).Select(i => new FakeFault { Id = 9101 + i }).ToList();
        }

        public List<FakeFault> Faults { get; }

        public List<int> Writes { get; } = new();

        public HashSet<int> FailWritesTo { get; } = new();

        /// <summary>Whether writes really act per entity - can differ from the rule as read.</summary>
        public bool ActsPerEntity { get; set; }

        public bool RecoverBeforeWrite { get; set; }

        public Task<AlertSource> GetSourceAsync(CancellationToken cancellationToken = default) => Task.FromResult(AlertSource.Fault);

        public Task<IReadOnlyList<Alert>> ListAsync(AlertQuery? query = null, CancellationToken cancellationToken = default)
        {
            var states = query?.States ?? new[] { AlertState.Active };
            return Task.FromResult<IReadOnlyList<Alert>>(Faults
                .Where(f => states.Contains(f.State) && (query?.RuleId is null || query.RuleId == 3))
                .Select(f => f.ToAlert())
                .ToList());
        }

        public Task<Alert?> GetAsync(int alertId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AcknowledgeAsync(int alertId, string? note = null, bool untilClear = true, CancellationToken cancellationToken = default)
            => Write(alertId, AlertState.Acknowledged);

        public Task UnmuteAsync(int alertId, string? note = null, CancellationToken cancellationToken = default)
            => Write(alertId, AlertState.Active);

        private Task Write(int id, AlertState state)
        {
            Writes.Add(id);
            if (FailWritesTo.Contains(id))
            {
                throw new LibreNmsApiException("Server error", HttpStatusCode.InternalServerError);
            }

            if (RecoverBeforeWrite)
            {
                Faults.ForEach(f => f.State = AlertState.Recovered);
            }

            var open = Faults.Where(f => f.State != AlertState.Recovered).ToList();
            var targets = ActsPerEntity && open.Count <= (_maxEntities ?? 20)
                ? open.Where(f => f.Id == id).ToList()
                : open;
            if (targets.Count == 0)
            {
                throw new LibreNmsApiException("No Alert by that ID", HttpStatusCode.NotFound, "No Alert by that ID");
            }

            foreach (var target in targets)
            {
                target.State = state;
                target.NoteCount++;
            }

            return Task.CompletedTask;
        }

        Task<IReadOnlyList<AlertRule>> IAlertRulesApi.ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        Task<AlertRule?> IAlertRulesApi.GetAsync(int ruleId, CancellationToken cancellationToken)
            => Task.FromResult<AlertRule?>(new AlertRule { Id = ruleId, NotifyPerEntity = _perEntity, MaxEntities = _maxEntities });

        public Task CreateAsync(AlertRuleWriteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateAsync(AlertRuleWriteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(int ruleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeFault
    {
        public int Id { get; init; }

        public AlertState State { get; set; } = AlertState.Active;

        public int NoteCount { get; set; }

        public Alert ToAlert() => new()
        {
            Id = Id,
            DeviceId = Cores.DeviceId,
            RuleId = Cores.RuleId,
            StateValue = (int)State,
            Source = AlertSource.Fault,
            EntityKey = $"processor:{Id}",
        };
    }

    /// <summary>Answers every write with HTTP 200 and the given message, as LibreNMS does.</summary>
    private sealed class MessageTransport : ILibreNmsTransport
    {
        private readonly string _message;

        public MessageTransport(string message) => _message = message;

        public LibreNmsConnection? Connection { get; } = new(new Uri("https://nms.example.net/"), "token");

        public Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());

        public Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default)
            => Task.FromResult(JsonDocument.Parse($"{{\"status\":\"ok\",\"message\":\"{_message}\"}}"));

        public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
