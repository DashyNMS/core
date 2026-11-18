using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>A LibreNMS version, from /api/v0/system's <c>local_ver</c> ("26.10.1", "26.10.0-12-gabc1234").</summary>
public static class LibreNmsVersion
{
    /// <summary>LibreNMS 26.10.0: per-entity faults, and four device columns removed (#11).</summary>
    public static Version Faults { get; } = new(26, 10, 0);

    /// <summary>
    /// LibreNMS 26.11.0: polling methods replace the device SNMP columns
    /// (librenms#21030), forwarding-table entries carry the VLAN number
    /// instead of an internal id (librenms#21152), and Proxmox moves from its
    /// own table into <c>vminfo</c> (librenms#21066) (#86, #87).
    /// </summary>
    public static Version PollingMethods { get; } = new(26, 11, 0);

    /// <summary>The year.month.patch at the start of <paramref name="text"/>, or null when there isn't one.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text.Trim(), @"^v?(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success)
        {
            return null;
        }

        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return new Version(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            patch);
    }
}

/// <summary>A rule condition column LibreNMS has removed, and what to use instead.</summary>
/// <param name="Field">"devices.last_ping".</param>
/// <param name="RemovedIn">The LibreNMS version it went in.</param>
/// <param name="Replacement">
/// The column that holds the same information instead, where there is one.
/// Never apply it automatically: a replacement can hold different values
/// (<c>ports_fdb.vlan</c> is a VLAN number where <c>ports_fdb.vlan_id</c> was
/// an internal id), so the condition's value needs a person's review too.
/// </param>
/// <param name="Advice">One line for the rule editor.</param>
public sealed record RemovedConditionField(string Field, Version RemovedIn, string? Replacement, string Advice)
{
    /// <summary>The table alone, "devices".</summary>
    public string Table => Field[..Field.IndexOf('.')];

    /// <summary>The column name alone, "last_ping".</summary>
    public string Column => Field[(Field.IndexOf('.') + 1)..];
}

/// <summary>
/// Rule conditions across LibreNMS versions (#11, #87). Each release drops
/// columns a saved rule may still test, which then fails on the server, and
/// adds columns older servers don't have. New conditions offer only the
/// connected server's fields; existing rules using a removed one - built,
/// imported or raw SQL - are flagged for review with specific advice. Custom
/// SQL and conditions are never rewritten.
/// </summary>
public static class AlertRuleCompatibility
{
    private const string PollingMethodAdvice =
        "LibreNMS 26.11 moved SNMP settings from devices to per-device polling methods, so devices.{0} is gone. Test device_polling_methods (method_type, enabled) instead, or remove the condition.";

    private const string ProxmoxAdvice =
        "LibreNMS 26.11 removed the Proxmox application and its proxmox table. Proxmox VMs now appear in vminfo through the Proxmox API polling method; use vminfo fields such as vminfo.cluster_id or vminfo.vmwVmDisplayName.";

    private const string VrfLiteAdvice =
        "LibreNMS 26.11 merged vrf_lite_cisco into vrfs. Use vrfs.context_name for the context.";

    public static IReadOnlyList<RemovedConditionField> RemovedFields { get; } = new[]
    {
        new RemovedConditionField(
            "devices.last_ping", LibreNmsVersion.Faults, null,
            "LibreNMS 26.10 no longer keeps devices.last_ping. Use devices.status for reachability, or the device_stats ping columns."),
        new RemovedConditionField(
            "devices.last_ping_timetaken", LibreNmsVersion.Faults, "device_stats.ping_rtt_last",
            "Ping latency moved to device_stats.ping_rtt_last in LibreNMS 26.10."),
        new RemovedConditionField(
            "devices.last_poll_attempted", LibreNmsVersion.Faults, null,
            "LibreNMS 26.10 no longer keeps devices.last_poll_attempted. Use devices.last_polled."),
        new RemovedConditionField(
            "devices.agent_uptime", LibreNmsVersion.Faults, null,
            "LibreNMS 26.10 no longer keeps devices.agent_uptime. Use devices.uptime."),

        PollingMethod("community"),
        PollingMethod("authlevel"),
        PollingMethod("authname"),
        PollingMethod("authpass"),
        PollingMethod("authalgo"),
        PollingMethod("cryptopass"),
        PollingMethod("cryptoalgo"),
        PollingMethod("snmpver"),
        PollingMethod("port"),
        PollingMethod("transport"),
        PollingMethod("timeout"),
        PollingMethod("retries"),
        new RemovedConditionField(
            "devices.snmp_disable", LibreNmsVersion.PollingMethods, null,
            "LibreNMS 26.11 replaced devices.snmp_disable with polling methods. Test whether the device has an enabled SNMP method in device_polling_methods (method_type, enabled)."),
        new RemovedConditionField(
            "devices.port_association_mode", LibreNmsVersion.PollingMethods, null,
            "LibreNMS 26.11 moved devices.port_association_mode into the device's SNMP polling-method settings, which rules can't test. Remove the condition."),
        new RemovedConditionField(
            "ports_fdb.vlan_id", LibreNmsVersion.PollingMethods, "ports_fdb.vlan",
            "LibreNMS 26.11 replaced ports_fdb.vlan_id, an internal row id, with ports_fdb.vlan, the actual VLAN number. The values differ, so re-enter the condition with the VLAN number rather than just changing the field."),
        new RemovedConditionField(
            "processors.entPhysicalIndex", LibreNmsVersion.PollingMethods, null,
            "LibreNMS 26.11 no longer keeps processors.entPhysicalIndex. Match the processor by processors.processor_index or processors.processor_descr."),
        Proxmox("id"),
        Proxmox("vmid"),
        Proxmox("cluster"),
        Proxmox("description"),
        Proxmox("last_seen"),
        new RemovedConditionField("vrf_lite_cisco.vrf_lite_cisco_id", LibreNmsVersion.PollingMethods, "vrfs.vrf_id", VrfLiteAdvice),
        new RemovedConditionField("vrf_lite_cisco.context_name", LibreNmsVersion.PollingMethods, "vrfs.context_name", VrfLiteAdvice),
        new RemovedConditionField("vrf_lite_cisco.intance_name", LibreNmsVersion.PollingMethods, null, VrfLiteAdvice),
        new RemovedConditionField("vrf_lite_cisco.vrf_name", LibreNmsVersion.PollingMethods, "vrfs.vrf_name", VrfLiteAdvice),
    };

    /// <summary>
    /// Fields each release added, so older servers don't offer them. A field
    /// not listed here exists on every supported release (or was removed -
    /// see <see cref="RemovedFields"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, Version> AddedFields { get; } = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase)
    {
        ["alert_faults.alerted"] = LibreNmsVersion.Faults,
        ["alert_faults.entity_id"] = LibreNmsVersion.Faults,
        ["alert_faults.entity_key"] = LibreNmsVersion.Faults,
        ["alert_faults.entity_type"] = LibreNmsVersion.Faults,
        ["alert_faults.first_seen"] = LibreNmsVersion.Faults,
        ["alert_faults.id"] = LibreNmsVersion.Faults,
        ["alert_faults.info"] = LibreNmsVersion.Faults,
        ["alert_faults.last_seen"] = LibreNmsVersion.Faults,
        ["alert_faults.note"] = LibreNmsVersion.Faults,
        ["alert_faults.open"] = LibreNmsVersion.Faults,
        ["alert_faults.rule_id"] = LibreNmsVersion.Faults,
        ["alert_faults.severity"] = LibreNmsVersion.Faults,
        ["alert_faults.state"] = LibreNmsVersion.Faults,
        ["alert_faults.timestamp"] = LibreNmsVersion.Faults,

        ["device_polling_methods.affects_availability"] = LibreNmsVersion.PollingMethods,
        ["device_polling_methods.created_at"] = LibreNmsVersion.PollingMethods,
        ["device_polling_methods.enabled"] = LibreNmsVersion.PollingMethods,
        ["device_polling_methods.id"] = LibreNmsVersion.PollingMethods,
        ["device_polling_methods.last_check_changed_at"] = LibreNmsVersion.PollingMethods,
        ["device_polling_methods.last_check_message"] = LibreNmsVersion.PollingMethods,
        ["device_polling_methods.last_check_successful"] = LibreNmsVersion.PollingMethods,
        ["device_polling_methods.method_type"] = LibreNmsVersion.PollingMethods,
        ["device_polling_methods.secret_id"] = LibreNmsVersion.PollingMethods,
        ["device_polling_methods.updated_at"] = LibreNmsVersion.PollingMethods,
        ["mempools.polling_method"] = LibreNmsVersion.PollingMethods,
        ["ports_fdb.vlan"] = LibreNmsVersion.PollingMethods,
        ["processors.processor_perc_warn_custom"] = LibreNmsVersion.PollingMethods,
        ["sensors.category"] = LibreNmsVersion.PollingMethods,
        ["users.phone"] = LibreNmsVersion.PollingMethods,
        ["vminfo.cluster_id"] = LibreNmsVersion.PollingMethods,
        ["vrfs.context_enabled"] = LibreNmsVersion.PollingMethods,
        ["vrfs.context_failures"] = LibreNmsVersion.PollingMethods,
        ["vrfs.context_manual"] = LibreNmsVersion.PollingMethods,
        ["vrfs.context_name"] = LibreNmsVersion.PollingMethods,
        ["vrfs.context_verified"] = LibreNmsVersion.PollingMethods,
        ["vrfs.vrf_manual"] = LibreNmsVersion.PollingMethods,
    };

    private static readonly IReadOnlyDictionary<string, RemovedConditionField> ByField =
        RemovedFields.ToDictionary(f => f.Field, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every table in the catalogue that has a given column, for deciding
    /// whether an unqualified column in SQL can only mean the removed one.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> TablesByColumn =
        AlertConditionFields.Groups.SelectMany(g => g.Fields)
            .Where(f => f.Table != "macros")
            .GroupBy(f => f.Field[(f.Field.IndexOf('.') + 1)..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Table).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The removal, if <paramref name="field"/> no longer exists on a server
    /// of <paramref name="serverVersion"/>. With no version, any removal: a
    /// field that might be gone is worth the warning.
    /// </summary>
    public static RemovedConditionField? Removal(string? field, Version? serverVersion)
        => field is not null && ByField.TryGetValue(field, out var removed) && (serverVersion is null || serverVersion >= removed.RemovedIn)
            ? removed
            : null;

    /// <summary>
    /// Whether a server of <paramref name="serverVersion"/> has
    /// <paramref name="field"/> for a new condition. With no version, only
    /// fields every supported release has, so a new condition works on any.
    /// </summary>
    public static bool IsAvailable(string field, Version? serverVersion)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (Removal(field, serverVersion) is not null)
        {
            return false;
        }

        return !AddedFields.TryGetValue(field, out var added) || (serverVersion is not null && serverVersion >= added);
    }

    /// <summary>
    /// The fields to offer for a new condition on a server of
    /// <paramref name="serverVersion"/> - <see cref="AlertConditionFields.Groups"/>
    /// without any it has removed or doesn't have yet (see <see cref="IsAvailable"/>).
    /// Existing conditions still resolve through <see cref="AlertConditionFields.Resolve"/>,
    /// so they open and can be fixed.
    /// </summary>
    public static IReadOnlyList<AlertConditionFieldGroup> FieldGroups(Version? serverVersion)
        => AlertConditionFields.Groups
            .Select(g => new AlertConditionFieldGroup(g.Name, g.Fields.Where(f => IsAvailable(f.Field, serverVersion)).ToList()))
            .Where(g => g.Fields.Count > 0)
            .ToList();

    /// <summary>The removed columns <paramref name="rule"/> tests, in its builder conditions or its SQL.</summary>
    public static IReadOnlyList<RemovedConditionField> FindRemovedColumns(AlertRule rule, Version? serverVersion = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return FindRemovedColumns(rule.Builder, rule.Query, serverVersion);
    }

    /// <summary>
    /// The removed columns used by a builder tree (JSON) or SQL - an
    /// override query, an imported rule, a collection rule. With a version,
    /// only those removed by then; without one, any of them.
    /// </summary>
    /// <remarks>
    /// SQL matches a qualified <c>table.column</c>, or a bare column only
    /// when it can mean nothing else: its table is named in FROM or JOIN and
    /// no other named table has that column. Without a FROM clause (a bare
    /// condition), a bare column counts only when no other table in the
    /// catalogue has it. So <c>vlans.vlan_id</c> or a bare <c>id</c> never
    /// passes for a removed <c>ports_fdb.vlan_id</c> or <c>proxmox.id</c>.
    /// Table aliases are not followed.
    /// </remarks>
    public static IReadOnlyList<RemovedConditionField> FindRemovedColumns(string? builderJson, string? sql, Version? serverVersion = null)
    {
        var found = new HashSet<RemovedConditionField>();

        foreach (var field in BuilderFields(builderJson))
        {
            if (ByField.TryGetValue(field, out var removed))
            {
                found.Add(removed);
            }
        }

        if (!string.IsNullOrWhiteSpace(sql))
        {
            // Quoted values are data, not columns: a rule matching os = 'timeout' tests no column.
            var text = Regex.Replace(sql.Replace("`", string.Empty, StringComparison.Ordinal), @"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", "''");
            var named = NamedTables(text);
            foreach (var removed in RemovedFields)
            {
                var qualified = $@"(?<![\w.]){Regex.Escape(removed.Table)}\.{Regex.Escape(removed.Column)}(?!\w)";
                if (Regex.IsMatch(text, qualified, RegexOptions.IgnoreCase)
                    || (BareMeansOnly(removed, named) && Regex.IsMatch(text, $@"(?<![\w.]){Regex.Escape(removed.Column)}(?!\w)", RegexOptions.IgnoreCase)))
                {
                    found.Add(removed);
                }
            }
        }

        return RemovedFields
            .Where(found.Contains)
            .Where(f => serverVersion is null || serverVersion >= f.RemovedIn)
            .ToList();
    }

    private static RemovedConditionField PollingMethod(string column) =>
        new($"devices.{column}", LibreNmsVersion.PollingMethods, null, string.Format(CultureInfo.InvariantCulture, PollingMethodAdvice, column));

    private static RemovedConditionField Proxmox(string column) =>
        new($"proxmox.{column}", LibreNmsVersion.PollingMethods, null, ProxmoxAdvice);

    /// <summary>Whether an unqualified mention of the column can only be <paramref name="removed"/>.</summary>
    private static bool BareMeansOnly(RemovedConditionField removed, IReadOnlySet<string> named)
    {
        var owners = TablesByColumn.TryGetValue(removed.Column, out var tables) ? tables : [removed.Table];
        if (named.Count == 0)
        {
            return owners.All(t => t.Equals(removed.Table, StringComparison.OrdinalIgnoreCase));
        }

        return named.Contains(removed.Table)
               && owners.All(t => t.Equals(removed.Table, StringComparison.OrdinalIgnoreCase) || !named.Contains(t));
    }

    /// <summary>The tables a query names after FROM (including comma lists) or JOIN.</summary>
    private static IReadOnlySet<string> NamedTables(string sql)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match from in Regex.Matches(sql, @"\bfrom\s+([\w\s,]+?)(?=\bwhere\b|\bjoin\b|\bleft\b|\binner\b|\bright\b|\bon\b|\(|\)|$)", RegexOptions.IgnoreCase))
        {
            foreach (var part in from.Groups[1].Value.Split(','))
            {
                if (part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } table)
                {
                    tables.Add(table);
                }
            }
        }

        foreach (Match join in Regex.Matches(sql, @"\bjoin\s+(\w+)", RegexOptions.IgnoreCase))
        {
            tables.Add(join.Groups[1].Value);
        }

        return tables;
    }

    private static IEnumerable<string> BuilderFields(string? builderJson)
    {
        if (string.IsNullOrWhiteSpace(builderJson))
        {
            yield break;
        }

        AlertConditionNode? root;
        try
        {
            root = JsonSerializer.Deserialize<AlertConditionNode>(builderJson, LibreNmsJson.Options);
        }
        catch (JsonException)
        {
            yield break;
        }

        var pending = new Stack<AlertConditionNode>();
        if (root is not null)
        {
            pending.Push(root);
        }

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if ((node.Field ?? node.Id) is { Length: > 0 } field)
            {
                yield return field;
            }

            foreach (var child in node.Rules ?? new List<AlertConditionNode>())
            {
                pending.Push(child);
            }
        }
    }
}
