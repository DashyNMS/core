using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Notification state and change detection by device and rule (#5).</summary>
public class AlertWatchTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Eight_cores_firing_at_once_are_one_change()
    {
        var cores = Enumerable.Range(0, 8).Select(i => Fault(100 + i, entity: $"processor:{i}", name: $"CPU {i}")).ToList();

        var result = Detect(AlertWatchState.Empty, cores);

        var change = Assert.Single(result.Changes);
        Assert.Equal(AlertChangeKind.New, change.Kind);
        Assert.Equal(8, change.OutstandingCount);
        Assert.Equal("device:7/rule:3", change.NotificationTag);
        Assert.Equal("CPU 0, CPU 1 and 6 more", AlertNotificationPlanner.Detail(change));
    }

    [Fact]
    public void More_faults_on_a_firing_group_update_it_silently()
    {
        var before = Detect(AlertWatchState.Empty, new[] { Fault(100), Fault(101) }).Next;

        var change = Assert.Single(Detect(before, new[] { Fault(100), Fault(101), Fault(102), Fault(103) }).Changes);

        Assert.Equal(AlertChangeKind.Updated, change.Kind);
        Assert.Equal(2, change.AddedCount);
        Assert.Equal("2 more faults, 4 open", AlertNotificationPlanner.Detail(change));

        var notification = Assert.Single(Plan(change).Notifications);
        Assert.True(notification.IsUpdate);
        Assert.False(notification.PlaySound);
        Assert.False(notification.IsProblem);
        Assert.Equal("device:7/rule:3", notification.Tag);
    }

    [Fact]
    public void Updates_never_tip_a_poll_into_a_summary()
    {
        var settings = new NotificationSettings { MaxToastsPerPoll = 1 };
        var before = Detect(AlertWatchState.Empty, new[] { Fault(100, device: 1), Fault(200, device: 2) }).Next;

        var result = Detect(before, new[] { Fault(100, device: 1), Fault(101, device: 1), Fault(200, device: 2), Fault(201, device: 2), Fault(300, device: 3) });

        Assert.Equal(3, result.Changes.Count);
        var plan = Plan(result.Changes, settings);
        Assert.False(plan.IsSummary);
        Assert.Equal(3, plan.Notifications.Count);
        Assert.Single(plan.Notifications, n => !n.IsUpdate);
    }

    [Fact]
    public void Cores_recovering_one_by_one_recover_the_group_once()
    {
        var state = Detect(AlertWatchState.Empty, new[] { Fault(100), Fault(101), Fault(102) }).Next;

        var first = Detect(state, new[] { Fault(100, state: AlertState.Recovered), Fault(101), Fault(102) });
        var update = Assert.Single(first.Changes);
        Assert.Equal(AlertChangeKind.Updated, update.Kind);
        Assert.Equal("1 cleared, 2 open", AlertNotificationPlanner.Detail(update));

        // The list no longer has 100, and 101 is seen recovering.
        var second = Detect(first.Next, new[] { Fault(101, state: AlertState.Recovered), Fault(102) });
        Assert.Equal(AlertChangeKind.Updated, Assert.Single(second.Changes).Kind);

        var last = Detect(second.Next, new[] { Fault(102, state: AlertState.Recovered) });
        var recovered = Assert.Single(last.Changes);
        Assert.Equal(AlertChangeKind.Recovered, recovered.Kind);
        Assert.Equal(0, recovered.OutstandingCount);
    }

    [Fact]
    public void All_faults_clearing_together_is_one_recovery()
    {
        var state = Detect(AlertWatchState.Empty, new[] { Fault(100), Fault(101) }).Next;

        var change = Assert.Single(Detect(state, new[] { Fault(100, state: AlertState.Recovered), Fault(101, state: AlertState.Recovered) }).Changes);

        Assert.Equal(AlertChangeKind.Recovered, change.Kind);
        Assert.Equal("All 2 faults cleared", AlertNotificationPlanner.Detail(change));
    }

    [Fact]
    public void A_fault_that_goes_missing_is_never_a_recovery()
    {
        var state = Detect(AlertWatchState.Empty, new[] { Fault(100) }).Next;

        var result = Detect(state, Array.Empty<Alert>());

        Assert.Empty(result.Changes);
        Assert.Empty(result.Next.Entries);
    }

    [Fact]
    public void A_failed_lookup_mid_incident_keeps_the_group_open()
    {
        var state = Detect(AlertWatchState.Empty, new[] { Fault(100), Fault(101) }).Next;

        // 101 couldn't be looked up: kept as it was, so nothing recovers or reopens.
        var failed = Detect(state, new[] { Fault(100, state: AlertState.Recovered) }, unresolved: new[] { Key(101) });
        var update = Assert.Single(failed.Changes);
        Assert.Equal(AlertChangeKind.Updated, update.Kind);
        Assert.Equal(1, update.OutstandingCount);
        Assert.True(failed.Next.Entries[Key(101)].IsOutstanding);

        // Next poll it's found recovered: now the group recovers.
        var change = Assert.Single(Detect(failed.Next, new[] { Fault(101, state: AlertState.Recovered) }).Changes);
        Assert.Equal(AlertChangeKind.Recovered, change.Kind);
    }

    [Fact]
    public void A_failed_poll_changes_nothing_and_the_next_one_carries_on()
    {
        // A failed or denied poll never reaches the detector, so the state it
        // compares against is the last good one - no burst when it recovers.
        var state = Detect(AlertWatchState.Empty, new[] { Fault(100), Fault(101) }).Next;

        Assert.Empty(Detect(state, new[] { Fault(100), Fault(101) }).Changes);
    }

    [Fact]
    public void Firing_again_soon_after_recovering_reads_as_again()
    {
        var open = Detect(AlertWatchState.Empty, new[] { Fault(100) }).Next;
        var recovered = Detect(open, new[] { Fault(100, state: AlertState.Recovered) }).Next;

        // A new fault row for the same device and rule, with the recovered one no longer listed.
        var change = Assert.Single(Detect(recovered, new[] { Fault(150) }, at: Now.AddHours(1)).Changes);

        Assert.Equal(AlertChangeKind.Reopened, change.Kind);
        Assert.Equal(AlertState.Recovered, change.PreviousState);
    }

    [Fact]
    public void A_recovery_is_forgotten_after_a_day()
    {
        var open = Detect(AlertWatchState.Empty, new[] { Fault(100) }).Next;
        var recovered = Detect(open, new[] { Fault(100, state: AlertState.Recovered) }).Next;

        var later = Detect(recovered, Array.Empty<Alert>(), at: Now.AddDays(2));

        Assert.Empty(later.Next.Entries);
        Assert.Equal(AlertChangeKind.New, Assert.Single(Detect(later.Next, new[] { Fault(150) }, at: Now.AddDays(2)).Changes).Kind);
    }

    [Fact]
    public void Acknowledging_every_fault_acknowledges_the_group()
    {
        var state = Detect(AlertWatchState.Empty, new[] { Fault(100), Fault(101) }).Next;

        var partly = Detect(state, new[] { Fault(100, state: AlertState.Acknowledged), Fault(101) });
        Assert.Empty(partly.Changes);

        var change = Assert.Single(Detect(partly.Next, new[] { Fault(100, state: AlertState.Acknowledged), Fault(101, state: AlertState.Acknowledged) }).Changes);
        Assert.Equal(AlertChangeKind.Acknowledged, change.Kind);
    }

    [Fact]
    public void A_new_fault_beside_acknowledged_ones_is_news()
    {
        var state = Detect(AlertWatchState.Empty, new[] { Fault(100, state: AlertState.Acknowledged) }).Next;

        var change = Assert.Single(Detect(state, new[] { Fault(100, state: AlertState.Acknowledged), Fault(101) }).Changes);

        Assert.Equal(AlertChangeKind.New, change.Kind);
        Assert.True(change.IsProblem);
        Assert.Equal(101, change.Alert.Id);
    }

    [Fact]
    public void An_acknowledgement_dropping_reads_as_unacknowledged()
    {
        var state = Detect(AlertWatchState.Empty, new[] { Fault(100, state: AlertState.Acknowledged) }).Next;

        var change = Assert.Single(Detect(state, new[] { Fault(100) }).Changes);

        Assert.Equal(AlertChangeKind.Unacknowledged, change.Kind);
    }

    [Fact]
    public void Acknowledging_one_fault_of_a_group_from_the_app_is_not_announced()
    {
        var settings = new NotificationSettings { NotifyOnAcknowledge = true };
        var state = Detect(AlertWatchState.Empty, new[] { Fault(100), Fault(101) }).Next;
        var change = Assert.Single(Detect(state, new[] { Fault(100, state: AlertState.Acknowledged), Fault(101, state: AlertState.Acknowledged) }).Changes);
        var asked = new List<int>();

        var plan = Plan(new[] { change }, settings, c =>
        {
            asked.Add(c.Alert.Id);
            return c.Alert.Id == 101;
        });

        Assert.Empty(plan.Notifications);
        Assert.Equal(new[] { 100, 101 }, asked);
    }

    [Fact]
    public void Upgrading_to_faults_with_colliding_ids_is_a_quiet_baseline()
    {
        // Legacy alert 12 was open; LibreNMS 26.10 now has an unrelated fault 12, and more.
        var legacy = Detect(AlertWatchState.Empty, new[] { Fault(12, source: AlertSource.Alert) }, source: AlertSource.Alert).Next;

        var result = Detect(legacy, new[] { Fault(12), Fault(13, device: 9) });

        Assert.True(result.IsBaseline);
        Assert.Empty(result.Changes);
        Assert.Equal(AlertSource.Fault, result.Next.Source);
        Assert.Equal(new[] { Key(12), Key(13) }, result.Next.Entries.Keys.OrderBy(k => k.Id));

        // And from there it carries on as normal.
        Assert.Equal(AlertChangeKind.New, Assert.Single(Detect(result.Next, new[] { Fault(12), Fault(13, device: 9), Fault(14, device: 10) }).Changes).Kind);
    }

    [Fact]
    public void State_from_an_older_DashyNMS_is_a_quiet_baseline()
    {
        var old = NotificationStateStore.Read("{\"SavedAtUtc\":\"2026-10-01T00:00:00Z\",\"Alerts\":{\"12\":1,\"40\":2}}");

        Assert.True(old.NeedsBaseline);
        var result = Detect(old, new[] { Fault(12), Fault(40) });
        Assert.True(result.IsBaseline);
        Assert.Empty(result.Changes);
        Assert.False(result.Next.NeedsBaseline);
    }

    [Fact]
    public void An_empty_old_file_needs_no_baseline()
        => Assert.False(NotificationStateStore.Read("{\"Alerts\":{}}").NeedsBaseline);

    [Fact]
    public void The_state_round_trips_through_the_file()
    {
        var open = Detect(AlertWatchState.Empty, new[] { Fault(100), Fault(101, state: AlertState.Acknowledged) }).Next;
        var state = Detect(open, new[] { Fault(100, state: AlertState.Recovered), Fault(101, state: AlertState.Acknowledged) }).Next;

        var read = NotificationStateStore.Read(NotificationStateStore.Write(state, Now));

        Assert.Equal(AlertSource.Fault, read.Source);
        Assert.False(read.NeedsBaseline);
        Assert.Equal(state.Entries.OrderBy(e => e.Key.Id), read.Entries.OrderBy(e => e.Key.Id));
        Assert.Equal(Now, read.Entries[Key(100)].ClearedAtUtc);
    }

    [Fact]
    public void One_fault_is_asked_for_by_id_in_any_state()
    {
        var url = new AlertQuery { Id = 9001, States = AlertQuery.All.States, OrderBy = null }.ToRelativeUrl("faults");

        Assert.Equal("faults/9001?state=0%2C1%2C2", url);
    }

    private static AlertWatchResult Detect(
        AlertWatchState previous,
        IReadOnlyList<Alert> current,
        AlertSource source = AlertSource.Fault,
        IReadOnlyCollection<AlertKey>? unresolved = null,
        DateTime? at = null)
        => AlertGroupChangeDetector.Detect(previous, source, current, at ?? Now, unresolved);

    private static AlertNotificationPlan Plan(AlertChange change) => Plan(new[] { change }, new NotificationSettings());

    private static AlertNotificationPlan Plan(IReadOnlyList<AlertChange> changes, NotificationSettings settings, Func<AlertChange, bool>? selfInitiated = null)
        => AlertNotificationPlanner.Plan(changes, isFirstPoll: false, settings, new DateTime(2026, 10, 20, 12, 0, 0), a => "device-" + a.DeviceId, wasSelfInitiated: selfInitiated);

    private static AlertKey Key(int id) => new(AlertSource.Fault, id);

    private static Alert Fault(
        int id,
        int device = 7,
        int rule = 3,
        AlertState state = AlertState.Active,
        string? entity = null,
        string? name = null,
        AlertSource source = AlertSource.Fault)
        => new()
        {
            Id = id,
            DeviceId = device,
            RuleId = rule,
            StateValue = (int)state,
            SeverityText = "critical",
            RuleName = "Processor usage",
            Source = source,
            EntityType = entity is null ? null : "processor",
            EntityKey = entity ?? (source == AlertSource.Fault ? $"processor:{id}" : null),
            Details = name is null ? null : System.Text.Json.JsonDocument.Parse($"{{\"rule\":[{{\"processor_descr\":\"{name}\"}}]}}").RootElement,
        };
}
