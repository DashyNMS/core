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
        "From LibreNMS 26.11, SNMP settings live in per-device polling methods and devices.{0} no longer exists. Test device_polling_methods (method_type, enabled) instead, or remove the condition.";

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
            // Each query block resolves its own bare columns: a subquery's
            // tables don't make the outer query's columns ambiguous.
            var blocks = QueryBlocks(text);
            var scopes = blocks.Select(b => NamedTables(b.Text)).ToList();
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i].Text;
                var scrubbed = scopes[i].Scrubbed;

                // This block's tables, then each enclosing block's: a correlated
                // subquery's bare column resolves outwards when no inner table has it.
                var chain = new List<IReadOnlySet<string>>();
                for (var at = i; at >= 0; at = blocks[at].Parent)
                {
                    chain.Add(scopes[at].Tables);

                    // A derived table (a subquery in FROM) has columns this can't
                    // see, so a bare name may be one of them: look no further out.
                    if (DerivedTable().IsMatch(blocks[at].Text))
                    {
                        chain.Add(Opaque);
                        break;
                    }
                }

                foreach (var removed in RemovedFields)
                {
                    var qualified = $@"(?<![\w.]){Regex.Escape(removed.Table)}\.{Regex.Escape(removed.Column)}(?!\w)";

                    // A bare column, but not a qualifier ("port.device_id"). Alias
                    // declarations are already gone from the scrubbed text, so
                    // "FROM devices AS timeout WHERE timeout > 5" still counts.
                    var bare = $@"(?<![\w.]){Regex.Escape(removed.Column)}(?![\w.])";
                    if (Regex.IsMatch(block, qualified, RegexOptions.IgnoreCase, RegexTimeout)
                        || (BareMeansOnly(removed, chain) && Regex.IsMatch(scrubbed, bare, RegexOptions.IgnoreCase, RegexTimeout)))
                    {
                        found.Add(removed);
                    }
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
    /// <param name="scopes">The block's own tables first, then each enclosing block's, as SQL resolves names.</param>
    private static bool BareMeansOnly(RemovedConditionField removed, IReadOnlyList<IReadOnlySet<string>> scopes)
    {
        var owners = TablesByColumn.TryGetValue(removed.Column, out var tables) ? tables : [removed.Table];
        foreach (var named in scopes)
        {
            if (ReferenceEquals(named, Opaque))
            {
                return false;
            }

            // The nearest scope with a table that has the column decides.
            if (owners.Any(named.Contains))
            {
                return named.Contains(removed.Table)
                       && owners.All(t => t.Equals(removed.Table, StringComparison.OrdinalIgnoreCase) || !named.Contains(t));
            }
        }

        // No scope names an owner: a bare condition with no FROM at all counts
        // only when no other table has the column.
        return scopes.All(s => s.Count == 0) && owners.All(t => t.Equals(removed.Table, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly char[] SqlWhitespace = [' ', '\t', '\r', '\n'];

    /// <summary>Stands for a derived table's unknown columns in a scope chain.</summary>
    private static readonly IReadOnlySet<string> Opaque = new HashSet<string>();

    /// <summary>A subquery as a relation: straight after FROM or JOIN, or later in FROM's comma list - not a scalar subquery in a function's arguments.</summary>
    private static Regex DerivedTable() => new(@"\b(?:from\s+(?:\w+(?:\s+(?:as\s+)?\w+)?\s*,\s*)*|join\s+)\(subquery\d+\)", RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly HashSet<string> SqlWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "distinct", "from", "where", "and", "or", "not", "as", "is", "null", "in", "like", "between", "case", "when", "then", "else", "end", "asc", "desc",
    };

    /// <summary>
    /// The select list without its aliases - "ports_fdb.vlan vlan_id" as well
    /// as "ports_fdb.vlan AS vlan_id" - so a result name is never read as a column.
    /// </summary>
    private static string WithoutSelectAliases(string block)
    {
        var select = Regex.Match(block, @"^\s*select\s+(?<list>.*?)\s+from\b", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        if (!select.Success)
        {
            return block;
        }

        // Split on the list's own commas, not those inside function calls.
        var list = select.Groups["list"];
        var items = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < list.Value.Length; i++)
        {
            depth += list.Value[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (list.Value[i] == ',' && depth == 0)
            {
                items.Add(list.Value[start..i]);
                start = i + 1;
            }
        }

        items.Add(list.Value[start..]);
        var cleaned = items.Select(item =>
        {
            var alias = Regex.Match(item, @"^(?<expr>.*[\w)*])\s+(?<alias>[A-Za-z_]\w*)\s*$", RegexOptions.Singleline, RegexTimeout);
            return alias.Success && !SqlWords.Contains(alias.Groups["alias"].Value) && !SqlWords.Contains(LastWord(alias.Groups["expr"].Value))
                ? alias.Groups["expr"].Value
                : item;
        });

        return block[..list.Index] + string.Join(",", cleaned) + block[(list.Index + list.Length)..];
    }

    private static string LastWord(string text) => Regex.Match(text, @"(\w+)\s*$", RegexOptions.None, RegexTimeout).Groups[1].Value;

    /// <summary>Rule SQL is short; a pattern that runs this long is stuck, not working.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A query and each of its subqueries as separate blocks, outermost first,
    /// each with its nested subqueries replaced by a placeholder and knowing
    /// which block encloses it (-1 for the query itself).
    /// </summary>
    private static List<(string Text, int Parent)> QueryBlocks(string sql)
    {
        var inner = new List<string>();
        var text = sql;
        while (true)
        {
            var start = text.LastIndexOf("(select", StringComparison.OrdinalIgnoreCase);
            var compact = Regex.Match(text, @"\(\s+select\b", RegexOptions.IgnoreCase | RegexOptions.RightToLeft, RegexTimeout);
            if (compact.Success && compact.Index > start)
            {
                start = compact.Index;
            }

            if (start < 0)
            {
                break;
            }

            var depth = 0;
            var end = start;
            for (; end < text.Length; end++)
            {
                depth += text[end] switch { '(' => 1, ')' => -1, _ => 0 };
                if (depth == 0)
                {
                    break;
                }
            }

            end = Math.Min(end, text.Length - 1);
            inner.Add(text[(start + 1)..end]);
            text = text[..start] + $" (subquery{(inner.Count - 1).ToString(CultureInfo.InvariantCulture)}) " + text[(end + 1)..];
        }

        // Outermost first; a block's parent is the one holding its placeholder.
        var all = new List<string> { text };
        all.AddRange(Enumerable.Reverse(inner));
        return all.Select((block, at) =>
        {
            var token = at == 0 ? null : $"(subquery{(all.Count - 1 - at).ToString(CultureInfo.InvariantCulture)})";
            var parent = token is null ? -1 : all.FindIndex(b => b.Contains(token, StringComparison.Ordinal));
            return (block, parent);
        }).ToList();
    }

    /// <summary>
    /// The tables one query block names after FROM (including comma lists) or
    /// JOIN, and the block with alias declarations removed, so an alias is
    /// never read as a column while a real column of the same name still is.
    /// </summary>
    private static (IReadOnlySet<string> Tables, string Scrubbed) NamedTables(string sql)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scrubbed = sql;

        void Add(string declaration)
        {
            var words = declaration.Trim().Split(SqlWhitespace, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                return;
            }

            tables.Add(words[0]);
            var alias = words.Length >= 3 && words[1].Equals("as", StringComparison.OrdinalIgnoreCase) ? words[2]
                : words.Length >= 2 && !words[1].Equals("as", StringComparison.OrdinalIgnoreCase) ? words[1]
                : null;
            if (alias is not null)
            {
                scrubbed = Regex.Replace(scrubbed, $@"(?<![\w.]){Regex.Escape(words[0])}\s+(?:as\s+)?{Regex.Escape(alias)}(?![\w.])", words[0], RegexOptions.IgnoreCase, RegexTimeout);
            }
        }

        foreach (Match from in Regex.Matches(sql, @"\bfrom\s+([\w\s,]+?)(?=\bwhere\b|\bjoin\b|\bleft\b|\binner\b|\bright\b|\bon\b|\bgroup\b|\border\b|\blimit\b|\(|\)|;|$)", RegexOptions.IgnoreCase))
        {
            foreach (var part in from.Groups[1].Value.Split(','))
            {
                Add(part);
            }
        }

        foreach (Match join in Regex.Matches(sql, @"\bjoin\s+(\w+(?:\s+(?:as\s+)?(?!on\b)\w+)?)", RegexOptions.IgnoreCase))
        {
            Add(join.Groups[1].Value);
        }

        // A column alias ("SELECT vlan AS vlan_id", or "SELECT vlan vlan_id") names a result, not a column.
        scrubbed = Regex.Replace(WithoutSelectAliases(scrubbed), @"\bas\s+\w+", " ", RegexOptions.IgnoreCase, RegexTimeout);
        return (tables, scrubbed);
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
