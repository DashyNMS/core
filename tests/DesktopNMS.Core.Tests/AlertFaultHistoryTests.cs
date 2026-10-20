using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Fault details and history by fault_id (#7).</summary>
public class AlertFaultHistoryTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Eight_cores_going_into_alert_together_are_one_occurrence()
    {
        var log = Enumerable.Range(0, 8).Select(i => Row(100 + i, fault: 9100 + i, AlertState.Active, Now.AddHours(-1))).ToList();

        var occurrence = Assert.Single(AlertHistory.Occurrences(log, 3, 7, serverTimestampsAreUtc: false));
        Assert.Equal(8, occurrence.FaultCount);
        Assert.True(occurrence.IsOpen);

        var summary = AlertHistory.Summarise(log, 3, 7, Now, serverTimestampsAreUtc: false);
        Assert.Equal(0, summary.Count);
        Assert.Equal("No earlier alerts in the last 30 days", summary.Describe(Now));
    }

    [Fact]
    public void Cores_recovering_one_by_one_end_the_occurrence_with_the_last()
    {
        var log = new[]
        {
            Row(1, 9101, AlertState.Active, Now.AddDays(-3)),
            Row(2, 9102, AlertState.Active, Now.AddDays(-3).AddMinutes(1)),
            Row(3, 9103, AlertState.Active, Now.AddDays(-3).AddMinutes(2)),
            Row(4, 9101, AlertState.Recovered, Now.AddDays(-3).AddHours(1)),
            Row(5, 9103, AlertState.Recovered, Now.AddDays(-3).AddHours(2)),
            Row(6, 9102, AlertState.Recovered, Now.AddDays(-3).AddHours(3)),
            Row(7, 9104, AlertState.Active, Now.AddHours(-1)),
        };

        var occurrences = AlertHistory.Occurrences(log, 3, 7, serverTimestampsAreUtc: false);

        Assert.Equal(2, occurrences.Count);
        Assert.Equal(Now.AddDays(-3), occurrences[0].Started);
        Assert.Equal(Now.AddDays(-3).AddHours(3), occurrences[0].Ended);
        Assert.Equal(3, occurrences[0].FaultCount);
        Assert.Equal("Fired once in the last 30 days · last 3 days ago", AlertHistory.Summarise(log, 3, 7, Now, false).Describe(Now));
    }

    [Fact]
    public void Legacy_and_fault_rows_mix_after_an_upgrade()
    {
        var log = new[]
        {
            Row(1, null, AlertState.Active, Now.AddDays(-10)),
            Row(2, null, AlertState.Acknowledged, Now.AddDays(-10).AddMinutes(5)),
            Row(3, null, AlertState.Recovered, Now.AddDays(-9)),
            Row(4, 9101, AlertState.Active, Now.AddDays(-2)),
            Row(5, 9102, AlertState.Active, Now.AddDays(-2)),
            Row(6, null, AlertState.Worse, Now.AddDays(-2)), // 26.10's rule-level row - not a firing
            Row(7, 9101, AlertState.Recovered, Now.AddDays(-1)),
            Row(8, null, 5, Now.AddDays(-1)),                 // "changed"
            Row(9, 9102, AlertState.Recovered, Now.AddHours(-20)),
            Row(10, 9103, AlertState.Active, Now.AddHours(-1)),
        };

        var occurrences = AlertHistory.Occurrences(log, 3, 7, serverTimestampsAreUtc: false);

        Assert.Equal(new[] { 1, 2, 1 }, occurrences.Select(o => o.FaultCount));
        Assert.Equal(Now.AddHours(-20), occurrences[1].Ended);
        Assert.Equal(2, AlertHistory.Summarise(log, 3, 7, Now, false).Count);
    }

    [Fact]
    public void A_truncated_log_reports_a_minimum()
    {
        var log = new[]
        {
            Row(1, 9101, AlertState.Active, Now.AddDays(-2)),
            Row(2, 9101, AlertState.Recovered, Now.AddDays(-2).AddHours(1)),
            Row(3, 9102, AlertState.Active, Now.AddHours(-1)),
        };

        var summary = AlertHistory.Summarise(log, 3, 7, Now, false, truncated: true);

        Assert.True(summary.IsIncomplete);
        Assert.Equal("Fired at least once in the last 30 days · last 2 days ago", summary.Describe(Now));
        Assert.False(AlertHistory.Summarise(log, 3, 7, Now, false).IsIncomplete);
    }

    [Fact]
    public void Recoveries_from_before_the_log_are_one_occurrence_of_unknown_start()
    {
        var log = new[]
        {
            Row(1, 9101, AlertState.Recovered, Now.AddDays(-5)),
            Row(2, 9102, AlertState.Recovered, Now.AddDays(-5).AddMinutes(3)),
            Row(3, 9103, AlertState.Active, Now.AddHours(-1)),
        };

        var occurrences = AlertHistory.Occurrences(log, 3, 7, serverTimestampsAreUtc: false);

        Assert.Equal(2, occurrences.Count);
        Assert.True(occurrences[0].StartedBeforeHistory);
        Assert.Equal(2, occurrences[0].FaultCount);
        Assert.Equal(Now.AddDays(-5).AddMinutes(3), occurrences[0].Ended);

        var summary = AlertHistory.Summarise(log, 3, 7, Now, false);
        Assert.True(summary.IsIncomplete);
        Assert.Equal("No earlier alerts in the history available", summary.Describe(Now));
    }

    [Fact]
    public void A_legacy_alert_firing_again_without_a_logged_recovery_is_a_new_occurrence()
    {
        var log = new[]
        {
            Row(1, null, AlertState.Active, Now.AddDays(-4)),
            Row(2, null, AlertState.Active, Now.AddDays(-2)),
        };

        var occurrences = AlertHistory.Occurrences(log, 3, 7, serverTimestampsAreUtc: false);

        Assert.Equal(2, occurrences.Count);
        Assert.True(occurrences[0].RecoveryUnseen);
        Assert.False(occurrences[0].IsOpen);
        Assert.True(occurrences[1].IsOpen);
    }

    [Fact]
    public async Task A_faults_details_come_from_its_own_log_not_the_newest_for_the_rule()
    {
        var log = await LibreNmsFixtures.Read<AlertLogEntry>("26.10.0/alertlog.json", "logs");
        var first = new Alert { Id = 9001, DeviceId = 7, RuleId = 3, Source = AlertSource.Fault, EntityKey = "processor:120" };

        Assert.Equal(7701, AlertFaultDetails.LatestLog(log, first)!.Id);
        Assert.Equal(new[] { 7701 }, AlertFaultDetails.History(log, first).Select(e => e.Id));
    }

    [Fact]
    public async Task A_fault_with_no_log_of_its_own_shows_none_rather_than_a_siblings()
    {
        var log = await LibreNmsFixtures.Read<AlertLogEntry>("26.10.0/alertlog.json", "logs");
        var other = new Alert { Id = 9050, DeviceId = 7, RuleId = 3, Source = AlertSource.Fault, EntityKey = "processor:200" };

        Assert.Null(AlertFaultDetails.LatestLog(log, other));
    }

    [Fact]
    public async Task A_fault_about_the_whole_device_can_fall_back_to_legacy_rows()
    {
        var log = await LibreNmsFixtures.Read<AlertLogEntry>("26.10.0/alertlog.json", "logs");
        var carried = new Alert { Id = 9060, DeviceId = 7, RuleId = 3, Source = AlertSource.Fault, EntityKey = string.Empty };

        Assert.Equal(5501, AlertFaultDetails.LatestLog(log, carried)!.Id);
    }

    [Fact]
    public async Task A_legacy_alert_uses_the_newest_for_its_device_and_rule()
    {
        var log = await LibreNmsFixtures.Read<AlertLogEntry>("26.10.0/alertlog.json", "logs");
        var legacy = new Alert { Id = 412, DeviceId = 7, RuleId = 3, Source = AlertSource.Alert };

        Assert.Equal(7702, AlertFaultDetails.LatestLog(log, legacy)!.Id);
    }

    [Fact]
    public async Task Current_details_come_from_the_faults_own_payload()
    {
        var faults = await LibreNmsFixtures.Faults("26.10.0/faults-grouped.json");
        var core = faults.Single(f => f.Id == 9101);

        var detail = AlertFaultDetails.Resolve(core, Array.Empty<AlertLogEntry>());

        Assert.Contains(detail.Faults.SelectMany(f => f.Fields), field => field.Value.Contains("CPU 0"));
        Assert.Null(AlertFaultDetails.Current(new Alert { Id = 1, DeviceId = 1, RuleId = 1 }));
    }

    private static AlertLogEntry Row(int id, int? fault, AlertState state, DateTime at) => Row(id, fault, (int)state, at);

    private static AlertLogEntry Row(int id, int? fault, int state, DateTime at) => new()
    {
        Id = id,
        RuleId = 3,
        DeviceId = 7,
        FaultId = fault,
        StateValue = state,
        TimeLogged = DateTime.SpecifyKind(at, DateTimeKind.Unspecified),
    };
}
