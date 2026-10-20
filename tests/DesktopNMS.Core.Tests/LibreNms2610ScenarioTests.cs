using System.Diagnostics;
using System.Net;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// The LibreNMS 26.10 scenarios from #10, end to end through Core against the
/// fixtures: grouping, notifications, badges, details, history, permissions
/// and device groups together, as the apps use them.
/// </summary>
public class LibreNms2610ScenarioTests
{
    private static readonly DateTime UtcNow = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LocalNow = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Local);

    private static readonly AlertWatchState FaultsSeenBefore = new(AlertSource.Fault, new Dictionary<AlertKey, AlertWatchEntry>());

    [Fact]
    public async Task Eight_cores_firing_together_are_one_group_one_notification_and_a_badge_of_one()
    {
        var cores = await Cores();
        var log = await Log();

        var result = AlertGroupChangeDetector.Detect(FaultsSeenBefore, AlertSource.Fault, cores, UtcNow);

        var change = Assert.Single(result.Changes);
        Assert.Equal(AlertChangeKind.New, change.Kind);
        Assert.Equal(8, change.OutstandingCount);

        var notification = Assert.Single(Plan(result.Changes).Notifications);
        Assert.Equal("CPU 0, CPU 1 and 6 more", notification.Detail);
        Assert.Equal(1, AlertCounting.CountGroups(cores, AlertSeverity.Ok, includeAcknowledged: false).Total);

        // Each core's details are its own.
        foreach (var core in cores)
        {
            var name = $"CPU {core.Id - 9200}";
            Assert.Contains(AlertFaultDetails.Resolve(core, log).Faults.SelectMany(f => f.Fields), f => f.Value == name);
            Assert.Equal(core.Id, AlertFaultDetails.LatestLog(log, core)!.FaultId);
        }
    }

    [Fact]
    public async Task Cores_recovering_one_by_one_keep_the_group_open_until_the_last()
    {
        var cores = await Cores();
        var state = AlertGroupChangeDetector.Detect(FaultsSeenBefore, AlertSource.Fault, cores, UtcNow).Next;

        for (var i = 0; i < cores.Count; i++)
        {
            cores[i].StateValue = (int)AlertState.Recovered;
            var poll = AlertGroupChangeDetector.Detect(state, AlertSource.Fault, cores, UtcNow);
            state = poll.Next;

            var change = Assert.Single(poll.Changes);
            if (i < cores.Count - 1)
            {
                Assert.Equal(AlertChangeKind.Updated, change.Kind);
                Assert.Equal(cores.Count - 1 - i, change.OutstandingCount);
                Assert.Equal(1, AlertCounting.CountGroups(cores, AlertSeverity.Ok, includeAcknowledged: false).Total);
            }
            else
            {
                Assert.Equal(AlertChangeKind.Recovered, change.Kind);
                Assert.Equal(0, AlertCounting.CountGroups(cores, AlertSeverity.Ok, includeAcknowledged: true).Total);
            }
        }
    }

    [Fact]
    public async Task A_new_core_failing_beside_acknowledged_ones_notifies_and_counts()
    {
        var cores = await Cores();
        cores.ForEach(c => c.StateValue = (int)AlertState.Acknowledged);
        var state = AlertGroupChangeDetector.Detect(FaultsSeenBefore, AlertSource.Fault, cores, UtcNow).Next;
        Assert.Equal(0, AlertCounting.CountGroups(cores, AlertSeverity.Ok, includeAcknowledged: false).Total);

        var ninth = new Alert { Id = 9208, DeviceId = 7, RuleId = 3, StateValue = 1, SeverityText = "critical", Source = AlertSource.Fault, EntityKey = "processor:308" };
        var poll = AlertGroupChangeDetector.Detect(state, AlertSource.Fault, cores.Append(ninth).ToList(), UtcNow);

        var change = Assert.Single(poll.Changes);
        Assert.Equal(AlertChangeKind.New, change.Kind);
        Assert.Equal(9208, change.Alert.Id);
        Assert.Single(Plan(poll.Changes).Notifications, n => n.IsProblem);
        Assert.Equal(1, AlertCounting.CountGroups(cores.Append(ninth), AlertSeverity.Ok, includeAcknowledged: false).Total);
    }

    [Fact]
    public async Task Upgrading_with_colliding_ids_neither_bursts_nor_leaves_stale_targets()
    {
        // Before the upgrade, legacy alert 9200 was open; afterwards fault 9200 is an unrelated core.
        var legacy = new Alert { Id = 9200, DeviceId = 30, RuleId = 50, StateValue = 1, Source = AlertSource.Alert };
        var before = AlertGroupChangeDetector.Detect(AlertWatchState.Empty, AlertSource.Alert, new[] { legacy }, UtcNow).Next;

        var after = AlertGroupChangeDetector.Detect(before, AlertSource.Fault, await Cores(), UtcNow);

        Assert.True(after.IsBaseline);
        Assert.Empty(after.Changes);
        Assert.DoesNotContain(new AlertKey(AlertSource.Alert, 9200), after.Next.Entries.Keys);
        Assert.Contains(new AlertKey(AlertSource.Fault, 9200), after.Next.Entries.Keys);

        // A notification for alert:9200 can't be mistaken for fault:9200.
        Assert.NotEqual(new AlertKey(AlertSource.Alert, 9200), new AlertKey(AlertSource.Fault, 9200));
    }

    [Fact]
    public async Task Mixed_history_counts_occurrences_not_fault_events()
    {
        var log = await Log();

        var occurrences = AlertHistory.Occurrences(log, 3, 7, serverTimestampsAreUtc: false);
        Assert.Equal(new[] { 1, 2, 8 }, occurrences.Select(o => o.FaultCount));

        var summary = AlertHistory.Summarise(log, 3, 7, LocalNow, serverTimestampsAreUtc: false);
        Assert.Equal(2, summary.Count);
        Assert.False(summary.IsIncomplete);

        // The same log, cut off at the limit asked for, doesn't reach back 30 days.
        var cut = AlertHistory.Summarise(log, 3, 7, LocalNow, serverTimestampsAreUtc: false, truncated: true);
        Assert.True(cut.IsIncomplete);
        Assert.StartsWith("Fired at least 2 times in the last 30 days", cut.Describe(LocalNow));
    }

    [Fact]
    public async Task One_denied_alert_doesnt_block_the_others()
    {
        var server = new LibreNmsFixtureServer()
            .Serve("faults?state=-1", "26.10.0/faults.json")
            .Serve("faults/9200", "26.10.0/forbidden-alert.json", HttpStatusCode.Forbidden);
        var permissions = new ApiPermissions();

        var ex = await Assert.ThrowsAsync<LibreNmsApiException>(() => new AlertsApi(server).AcknowledgeAsync(9200));
        permissions.Learn(HttpMethod.Put, "faults/9200", ex);

        Assert.False(permissions.IsRefused(ApiPermission.AcknowledgeAlerts));
        Assert.True(permissions.IsAcknowledgeRefused(new AlertKey(AlertSource.Fault, 9200)));
        Assert.False(permissions.IsAcknowledgeRefused(new AlertKey(AlertSource.Fault, 9201)));
    }

    [Fact]
    public async Task Empty_and_denied_device_groups_dont_break_membership()
    {
        var server = new LibreNmsFixtureServer()
            .Serve("devicegroups/1", "26.10.0/devicegroup-members.json")
            .Serve("devicegroups/2", "26.10.0/devicegroup-empty.json", HttpStatusCode.NotFound)
            .Serve("devicegroups/3", "26.10.0/forbidden-group.json", HttpStatusCode.Forbidden)
            .Serve("devicegroups", "26.10.0/devicegroups.json");
        var api = new DeviceGroupsApi(server);

        var groups = await api.ListAsync();
        var membership = await api.GetMembershipByDeviceAsync();
        var counts = await api.GetMemberCountsAsync(groups);

        Assert.Equal(new[] { "static", "dynamic", "static" }, groups.Select(g => g.Type));
        Assert.Equal(new[] { 7, 8 }, membership.Keys.OrderBy(k => k));
        Assert.All(membership.Values, names => Assert.Equal(new[] { "Core switches" }, names));
        Assert.Equal(2, counts[1]);
        Assert.Equal(0, counts[2]);
        Assert.Equal(0, counts[3]);
    }

    [Fact]
    public void Thousands_of_faults_group_and_compare_quickly()
    {
        var faults = Enumerable.Range(0, 6000).Select(i => new Alert
        {
            Id = 100_000 + i,
            DeviceId = i % 500,
            RuleId = 1 + (i % 3),
            StateValue = i % 7 == 0 ? (int)AlertState.Acknowledged : (int)AlertState.Active,
            SeverityText = i % 2 == 0 ? "critical" : "warning",
            Source = AlertSource.Fault,
            EntityKey = $"port:{i}",
        }).ToList();

        var timer = Stopwatch.StartNew();
        var groups = AlertGrouping.ByDeviceAndRule(faults);
        var rules = AlertGrouping.ByRule(groups);
        var first = AlertGroupChangeDetector.Detect(FaultsSeenBefore, AlertSource.Fault, faults, UtcNow);
        var second = AlertGroupChangeDetector.Detect(first.Next, AlertSource.Fault, faults, UtcNow);
        var badge = AlertCounting.CountGroups(groups, AlertSeverity.Ok, includeAcknowledged: false);
        timer.Stop();

        Assert.Equal(1500, groups.Count);
        Assert.Equal(3, rules.Count);
        Assert.Equal(1500, first.Changes.Count);
        Assert.Empty(second.Changes);
        Assert.Equal(1500, badge.Total);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Took {timer.Elapsed}");
    }

    private static async Task<List<Alert>> Cores() => (await LibreNmsFixtures.Faults("26.10.0/faults-eight-cores.json")).ToList();

    private static Task<IReadOnlyList<AlertLogEntry>> Log() => LibreNmsFixtures.Read<AlertLogEntry>("26.10.0/alertlog-eight-cores.json", "logs");

    private static AlertNotificationPlan Plan(IReadOnlyList<AlertChange> changes)
        => AlertNotificationPlanner.Plan(changes, isFirstPoll: false, new NotificationSettings(), new DateTime(2026, 10, 20, 12, 0, 0), a => "device-" + a.DeviceId);
}
