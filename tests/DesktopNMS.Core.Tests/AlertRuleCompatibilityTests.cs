using System.Text.Json;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Per-entity rule settings and the columns LibreNMS 26.10 and 26.11 changed (#11, #87).</summary>
public class AlertRuleCompatibilityTests
{
    private static readonly Version V26_9 = new(26, 9, 1);
    private static readonly Version V26_10 = new(26, 10, 0);
    private static readonly Version V26_11 = new(26, 11, 0);

    private static HashSet<string> Offered(Version? version)
        => AlertRuleCompatibility.FieldGroups(version).SelectMany(g => g.Fields).Select(f => f.Field).ToHashSet();

    [Fact]
    public void New_conditions_offer_each_release_its_own_fields()
    {
        var v9 = Offered(V26_9);
        var v10 = Offered(V26_10);
        var v11 = Offered(V26_11);

        Assert.Contains("ports_fdb.vlan_id", v10);
        Assert.DoesNotContain("ports_fdb.vlan", v10);
        Assert.Contains("ports_fdb.vlan", v11);
        Assert.DoesNotContain("ports_fdb.vlan_id", v11);

        Assert.Contains("devices.community", v10);
        Assert.DoesNotContain("devices.community", v11);
        Assert.DoesNotContain("devices.snmpver", v11);
        Assert.Contains("device_polling_methods.method_type", v11);
        Assert.DoesNotContain("device_polling_methods.method_type", v10);

        Assert.Contains("proxmox.vmid", v10);
        Assert.DoesNotContain("proxmox.vmid", v11);
        Assert.Contains("vminfo.cluster_id", v11);
        Assert.DoesNotContain("vminfo.cluster_id", v10);
        Assert.DoesNotContain("processors.entPhysicalIndex", v11);
        Assert.Contains("processors.processor_perc_warn_custom", v11);

        Assert.DoesNotContain("alert_faults.state", v9);
        Assert.Contains("alert_faults.state", v10);

        // Stored credentials are never a rule field.
        Assert.DoesNotContain(v11, f => f.StartsWith("secrets.", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_version_offers_only_fields_every_release_has()
    {
        var unknown = Offered(null);

        Assert.DoesNotContain("ports_fdb.vlan_id", unknown);
        Assert.DoesNotContain("ports_fdb.vlan", unknown);
        Assert.DoesNotContain("devices.community", unknown);
        Assert.DoesNotContain("device_polling_methods.method_type", unknown);
        Assert.DoesNotContain("alert_faults.state", unknown);
        Assert.Contains("ports_fdb.mac_address", unknown);
        Assert.Contains("devices.uptime", unknown);

        Assert.NotNull(AlertRuleCompatibility.Removal("ports_fdb.vlan_id", null));
    }

    [Fact]
    public void Removed_and_added_fields_are_all_in_the_catalogue()
    {
        Assert.All(AlertRuleCompatibility.RemovedFields, r => Assert.NotNull(AlertConditionFields.Find(r.Field)));
        Assert.All(AlertRuleCompatibility.AddedFields.Keys, a => Assert.NotNull(AlertConditionFields.Find(a)));
    }

    [Fact]
    public void The_vlan_change_is_flagged_with_its_value_warning_and_never_rewritten()
    {
        var removed = AlertRuleCompatibility.Removal("ports_fdb.vlan_id", V26_11)!;

        Assert.Equal("ports_fdb.vlan", removed.Replacement);
        Assert.Contains("VLAN number", removed.Advice, StringComparison.Ordinal);
        Assert.Null(AlertRuleCompatibility.Removal("ports_fdb.vlan_id", V26_10));

        const string sql = "SELECT * FROM devices,ports_fdb WHERE (devices.device_id = ? AND ports_fdb.vlan_id = 50)";
        var rule = new AlertRule { Id = 9, Query = sql };
        Assert.Equal("ports_fdb.vlan_id", Assert.Single(AlertRuleCompatibility.FindRemovedColumns(rule, V26_11)).Field);
        Assert.Equal(sql, rule.Query);
    }

    [Fact]
    public void Nested_builder_conditions_find_26_11_removals()
    {
        const string builder = """
        {"condition":"AND","rules":[
          {"id":"devices.os","field":"devices.os","type":"string","input":"text","operator":"equal","value":"ios"},
          {"condition":"OR","rules":[
            {"id":"devices.snmpver","field":"devices.snmpver","type":"string","input":"text","operator":"equal","value":"v2c"},
            {"condition":"AND","rules":[
              {"id":"proxmox.vmid","field":"proxmox.vmid","type":"string","input":"text","operator":"equal","value":"100"}
            ]}
          ]}
        ],"valid":true}
        """;

        Assert.Equal(["devices.snmpver", "proxmox.vmid"], AlertRuleCompatibility.FindRemovedColumns(builder, null, V26_11).Select(f => f.Field).Order());
        Assert.Empty(AlertRuleCompatibility.FindRemovedColumns(builder, null, V26_10));
    }

    [Theory]
    [InlineData("SELECT * FROM devices WHERE devices.community = 'public'", "devices.community")]
    [InlineData("SELECT * FROM `devices` WHERE `devices`.`snmpver` = 'v3'", "devices.snmpver")]
    [InlineData("SELECT * FROM devices WHERE authlevel = 'authPriv'", "devices.authlevel")]
    [InlineData("SELECT * FROM devices,ports_fdb WHERE `ports_fdb`.`vlan_id` = 12", "ports_fdb.vlan_id")]
    [InlineData("SELECT * FROM devices,ports_fdb WHERE vlan_id = 12", "ports_fdb.vlan_id")]
    [InlineData("SELECT vlan_id FROM ports_fdb;", "ports_fdb.vlan_id")] // a terminated statement
    [InlineData("SELECT vlan_id FROM ports_fdb\nAS f WHERE vlan_id = 12", "ports_fdb.vlan_id")] // a newline before the alias
    [InlineData("SELECT vlan_id FROM\tports_fdb WHERE vlan_id = 12", "ports_fdb.vlan_id")]
    [InlineData("SELECT * FROM devices LEFT JOIN proxmox ON proxmox.device_id = devices.device_id WHERE proxmox.vmid = 100", "proxmox.vmid")]
    [InlineData("SELECT * FROM devices,processors WHERE processors.entPhysicalIndex = 1", "processors.entPhysicalIndex")]
    public void Raw_sql_using_26_11_removals_is_found(string sql, string field)
        => Assert.Equal(field, Assert.Single(AlertRuleCompatibility.FindRemovedColumns(null, sql, V26_11)).Field);

    [Theory]
    [InlineData("SELECT * FROM devices,vlans WHERE vlans.vlan_id = 12")] // another table's vlan_id
    [InlineData("SELECT * FROM devices,ports_fdb,vlans WHERE vlan_id = 12")] // bare and ambiguous
    [InlineData("SELECT * FROM devices,vminfo WHERE vminfo.id = 4")] // another table's id
    [InlineData("SELECT * FROM devices,ports WHERE id = 4 AND ports.port_id = 1")] // a bare id with no proxmox table
    [InlineData("SELECT * FROM devices,ports_fdb WHERE ports_fdb.vlan = 12")] // the new field
    [InlineData("SELECT * FROM devices WHERE devices.os = 'timeout' AND devices.hardware LIKE \"%community%\"")] // values, not columns
    [InlineData("SELECT * FROM devices,services WHERE services.service_desc = 'transport'")]
    [InlineData("SELECT * FROM devices JOIN ports AS port ON port.device_id = devices.device_id")] // an alias, not devices.port
    [InlineData("SELECT * FROM devices JOIN ports port ON port.device_id = devices.device_id")]
    public void Sql_naming_unrelated_columns_is_clean_on_26_11(string sql)
        => Assert.Empty(AlertRuleCompatibility.FindRemovedColumns(null, sql, V26_11));

    [Theory]
    [InlineData("26.10.1", 26, 10, 1)]
    [InlineData("26.10.0-12-gabc1234", 26, 10, 0)]
    [InlineData("v26.9", 26, 9, 0)]
    public void Server_versions_parse(string text, int year, int month, int patch)
        => Assert.Equal(new Version(year, month, patch), LibreNmsVersion.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("master")]
    public void Unreadable_versions_are_null(string? text) => Assert.Null(LibreNmsVersion.Parse(text));

    [Fact]
    public void New_conditions_on_26_10_dont_offer_removed_columns()
    {
        var current = AlertRuleCompatibility.FieldGroups(V26_10).SelectMany(g => g.Fields).Select(f => f.Field).ToHashSet();
        var older = AlertRuleCompatibility.FieldGroups(V26_9).SelectMany(g => g.Fields).Select(f => f.Field).ToHashSet();

        Assert.DoesNotContain("devices.last_ping", current);
        Assert.DoesNotContain("devices.agent_uptime", current);
        Assert.Contains("device_stats.ping_rtt_last", current);
        Assert.Contains("devices.uptime", current);
        Assert.Contains("devices.last_ping", older);
        Assert.DoesNotContain("devices.last_ping", AlertRuleCompatibility.FieldGroups(null).SelectMany(g => g.Fields).Select(f => f.Field));

        // An existing condition still opens.
        Assert.Equal("devices.last_ping", AlertConditionFields.Resolve("devices.last_ping").Field);
    }

    [Fact]
    public void Latency_points_at_its_replacement()
    {
        var removed = AlertRuleCompatibility.Removal("devices.last_ping_timetaken", V26_10);

        Assert.Equal("device_stats.ping_rtt_last", removed!.Replacement);
        Assert.Null(AlertRuleCompatibility.Removal("devices.last_ping_timetaken", V26_9));
        Assert.Null(AlertRuleCompatibility.Removal("devices.uptime", V26_10));
    }

    [Fact]
    public void Built_conditions_using_removed_columns_are_found()
    {
        const string builder = """
        {"condition":"AND","rules":[
          {"id":"devices.status","field":"devices.status","type":"string","input":"text","operator":"equal","value":"0"},
          {"condition":"OR","rules":[
            {"id":"devices.last_ping_timetaken","field":"devices.last_ping_timetaken","type":"string","input":"text","operator":"greater","value":"200"}
          ]}
        ],"valid":true}
        """;

        var found = AlertRuleCompatibility.FindRemovedColumns(builder, sql: null);

        Assert.Equal("devices.last_ping_timetaken", Assert.Single(found).Field);
    }

    [Theory]
    [InlineData("SELECT * FROM devices WHERE devices.last_ping < DATE_SUB(NOW(), INTERVAL 5 MINUTE)", "devices.last_ping")]
    [InlineData("SELECT * FROM `devices` WHERE `devices`.`agent_uptime` < 300", "devices.agent_uptime")]
    [InlineData("SELECT * FROM devices WHERE last_poll_attempted IS NULL", "devices.last_poll_attempted")]
    [InlineData("SELECT * FROM devices WHERE devices.last_ping_timetaken > 100", "devices.last_ping_timetaken")]
    public void Raw_sql_using_removed_columns_is_found(string sql, string field)
        => Assert.Equal(field, Assert.Single(AlertRuleCompatibility.FindRemovedColumns(null, sql)).Field);

    [Theory]
    [InlineData("SELECT * FROM devices WHERE devices.status = 0 AND devices.uptime < 300")]
    [InlineData("SELECT * FROM devices,device_stats WHERE device_stats.ping_rtt_last > 100")]
    [InlineData("SELECT * FROM applications WHERE applications.app_state = 'last_pinged'")]
    public void Sql_without_removed_columns_is_clean(string sql)
        => Assert.Empty(AlertRuleCompatibility.FindRemovedColumns(null, sql));

    [Fact]
    public void A_version_limits_what_counts_as_removed()
    {
        var rule = new AlertRule { Id = 1, Query = "SELECT * FROM devices WHERE devices.last_ping IS NULL" };

        Assert.Empty(AlertRuleCompatibility.FindRemovedColumns(rule, V26_9));
        Assert.Single(AlertRuleCompatibility.FindRemovedColumns(rule, V26_10));
    }

    [Fact]
    public void Unrelated_edits_never_send_the_per_entity_settings()
    {
        var rule = new AlertRule { Id = 3, Name = "CPU", NotifyPerEntity = true, MaxEntities = null };

        var json = Write(AlertRuleWriteRequest.FromRule(rule));

        Assert.False(json.TryGetProperty("notify_per_entity", out _));
        Assert.False(json.TryGetProperty("max_entities", out _));
    }

    [Fact]
    public void An_inherited_limit_stays_inherited()
    {
        var rule = new AlertRule { Id = 3, NotifyPerEntity = false, MaxEntities = null };

        var json = Write(AlertRuleWriteRequest.FromRule(rule).SetPerEntity(rule, notifyPerEntity: true, maxEntities: null));

        Assert.True(json.GetProperty("notify_per_entity").GetBoolean());
        Assert.False(json.TryGetProperty("max_entities", out _));
    }

    [Fact]
    public void Changing_the_limit_sends_it_and_clearing_it_sends_null()
    {
        var rule = new AlertRule { Id = 3, NotifyPerEntity = true, MaxEntities = 8 };

        var set = Write(AlertRuleWriteRequest.FromRule(rule).SetPerEntity(rule, true, 12));
        var cleared = Write(AlertRuleWriteRequest.FromRule(rule).SetPerEntity(rule, true, null));

        Assert.False(set.TryGetProperty("notify_per_entity", out _));
        Assert.Equal(12, set.GetProperty("max_entities").GetInt32());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("max_entities").ValueKind);
    }

    [Fact]
    public void A_new_rule_sends_only_what_differs_from_the_defaults()
    {
        var plain = Write(new AlertRuleWriteRequest().SetPerEntity(null, false, null));
        var perEntity = Write(new AlertRuleWriteRequest().SetPerEntity(null, true, 0));

        Assert.False(plain.TryGetProperty("notify_per_entity", out _));
        Assert.False(plain.TryGetProperty("max_entities", out _));
        Assert.True(perEntity.GetProperty("notify_per_entity").GetBoolean());
        Assert.Equal(1, perEntity.GetProperty("max_entities").GetInt32()); // LibreNMS's own minimum
    }

    [Fact]
    public async Task Rules_from_both_releases_read_and_round_trip()
    {
        var legacy = Assert.Single(await LibreNmsFixtures.Read<AlertRule>("26.9.1.1/rules.json", "rules"));
        var current = await LibreNmsFixtures.Read<AlertRule>("26.10.0/rules.json", "rules");

        Assert.Null(legacy.NotifyPerEntity);
        Assert.Equal("devices.last_ping_timetaken", Assert.Single(AlertRuleCompatibility.FindRemovedColumns(legacy, V26_10)).Field);

        var cpu = current.Single(r => r.Id == 3);
        Assert.True(cpu.NotifyPerEntity);
        Assert.Null(cpu.MaxEntities);
        Assert.Equal(8, current.Single(r => r.Id == 50).MaxEntities);
        Assert.All(current, r => Assert.Empty(AlertRuleCompatibility.FindRemovedColumns(r, V26_10)));

        // Renaming a rule leaves both settings to the server.
        var rename = AlertRuleWriteRequest.FromRule(cpu);
        rename.Name = "CPU busy";
        var json = Write(rename);
        Assert.False(json.TryGetProperty("notify_per_entity", out _));
        Assert.False(json.TryGetProperty("max_entities", out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void An_unset_cpu_warning_threshold_never_reads_as_zero(string value)
    {
        var processor = JsonSerializer.Deserialize<ProcessorSensor>($"{{\"processor_id\":1,\"processor_usage\":97,\"processor_perc_warn\":{value}}}", LibreNmsJson.Options)!;

        Assert.Null(processor.WarningPercent);
    }

    private static JsonElement Write(AlertRuleWriteRequest request)
        => JsonDocument.Parse(JsonSerializer.Serialize(request, LibreNmsJson.Options)).RootElement;
}
