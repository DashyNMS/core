using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Grouping LibreNMS 26.10 faults by device and rule, and by rule (#4).</summary>
public class AlertGroupsTests
{
    private static Task<IReadOnlyList<Alert>> Grouped() => LibreNmsFixtures.Faults("26.10.0/faults-grouped.json");

    [Fact]
    public async Task Faults_group_by_device_and_rule()
    {
        var groups = AlertGrouping.ByDeviceAndRule(await Grouped());

        Assert.Equal(6, groups.Count);
        var cores = Assert.Single(groups, g => g.Key == new AlertGroupKey(7, 3));
        Assert.Equal(4, cores.FaultCount);
        Assert.False(cores.IsSingle);
        Assert.All(groups.Where(g => g.Key != cores.Key), g => Assert.True(g.IsSingle));
    }

    [Fact]
    public async Task A_group_is_active_while_any_fault_is_unacknowledged()
    {
        var cores = AlertGrouping.ByDeviceAndRule(await Grouped()).Single(g => g.Key == new AlertGroupKey(7, 3));

        Assert.Equal(AlertState.Active, cores.State);
        Assert.Equal(3, cores.UnacknowledgedCount);
        Assert.Equal(1, cores.AcknowledgedCount);
        Assert.True(cores.IsPartlyAcknowledged);
        Assert.Equal(AlertSeverity.Critical, cores.Severity);

        // Unacknowledged first, then by entity.
        Assert.Equal(new[] { 9101, 9102, 9104, 9103 }, cores.Faults.Select(f => f.Id));
    }

    [Fact]
    public async Task A_fully_acknowledged_group_reads_as_acknowledged()
    {
        var other = AlertGrouping.ByDeviceAndRule(await Grouped()).Single(g => g.Key == new AlertGroupKey(8, 3));

        Assert.Equal(AlertState.Acknowledged, other.State);
        Assert.False(other.IsPartlyAcknowledged);
    }

    [Fact]
    public async Task A_group_of_one_reads_like_its_alert()
    {
        var single = AlertGrouping.ByDeviceAndRule(await Grouped()).Single(g => g.Key == new AlertGroupKey(31, 37));

        Assert.True(single.IsSingle);
        Assert.Equal(single.Primary.State, single.State);
        Assert.Equal(single.Primary.Severity, single.Severity);
        Assert.Equal(single.Primary.DisplayRuleName, single.DisplayRuleName);
    }

    [Fact]
    public async Task Groups_order_worst_first_then_most_recent()
    {
        var groups = AlertGrouping.ByDeviceAndRule(await Grouped());

        Assert.Equal(AlertSeverity.Warning, groups[^1].Severity);
        Assert.Equal(new AlertGroupKey(7, 3), groups[0].Key); // critical, changed 07:55
    }

    [Fact]
    public async Task Rules_roll_up_their_devices()
    {
        var rules = AlertGrouping.ByRule(AlertGrouping.ByDeviceAndRule(await Grouped()));

        Assert.Equal(new[] { 3, 68, 50, 37 }, rules.Select(r => r.RuleId));
        var cpu = rules[0];
        Assert.Equal(2, cpu.DeviceCount);
        Assert.Equal(5, cpu.FaultCount);
        Assert.Equal(2, rules.Single(r => r.RuleId == 68).DeviceCount);
        Assert.True(rules.Single(r => r.RuleId == 50).IsSingle);
    }

    [Fact]
    public async Task Badges_count_each_device_and_rule_once()
    {
        var faults = await Grouped();

        var groups = AlertCounting.CountGroups(faults, AlertSeverity.Ok, includeAcknowledged: true);
        Assert.Equal(new AlertCount(6, 5, 1), groups);

        // Without acknowledged: device 8 and the carried-over device are fully acknowledged.
        var active = AlertCounting.CountGroups(faults, AlertSeverity.Ok, includeAcknowledged: false);
        Assert.Equal(new AlertCount(4, 3, 1), active);

        // Faults are still countable separately.
        Assert.Equal(9, AlertCounting.Count(faults, AlertSeverity.Ok, includeAcknowledged: true).Total);
    }

    [Fact]
    public async Task A_new_fault_beside_acknowledged_ones_still_counts()
    {
        var faults = (await Grouped()).Where(f => f.Id is 9103 or 9104).ToList(); // one acknowledged, one active

        var count = AlertCounting.CountGroups(faults, AlertSeverity.Ok, includeAcknowledged: false);

        Assert.Equal(1, count.Total);
    }

    [Fact]
    public async Task Before_26_10_group_counts_equal_alert_counts()
    {
        var alerts = await LibreNmsFixtures.Read<Alert>("26.9.1.1/alerts.json", "alerts");

        Assert.Equal(
            AlertCounting.Count(alerts, AlertSeverity.Ok, includeAcknowledged: true),
            AlertCounting.CountGroups(alerts, AlertSeverity.Ok, includeAcknowledged: true));
    }

    [Theory]
    [InlineData(9101, "CPU 0")]
    [InlineData(9109, "Gi1/0/24")]
    public async Task Entities_are_named_from_the_fault_details(int id, string expected)
    {
        var fault = (await Grouped()).Single(f => f.Id == id);

        Assert.Contains(expected, AlertGrouping.EntityName(fault));
    }

    [Fact]
    public async Task A_location_fault_is_named_by_its_location()
    {
        var fault = (await Grouped()).Single(f => f.Id == 9106);

        var name = AlertGrouping.EntityName(fault);

        Assert.NotNull(name);
        Assert.DoesNotContain("ap-lobby", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_fault_carried_over_from_an_old_alert_is_about_the_whole_device()
    {
        var fault = (await Grouped()).Single(f => f.Id == 9108);

        Assert.Null(AlertGrouping.EntityName(fault));
    }

    [Fact]
    public void A_fault_with_nothing_to_name_it_uses_its_type_and_id()
    {
        var fault = new Alert { Id = 1, DeviceId = 1, RuleId = 1, EntityType = "storage_pool", EntityId = 12, EntityKey = "storage_pool:12" };

        Assert.Equal("Storage pool 12", AlertGrouping.EntityName(fault));
    }

    [Fact]
    public void Group_keys_name_the_device_and_rule()
        => Assert.Equal("device:7/rule:3", new AlertGroupKey(7, 3).ToString());
}
