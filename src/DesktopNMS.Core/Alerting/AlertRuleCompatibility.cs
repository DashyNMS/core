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
/// <param name="Replacement">The column to use instead, where there is one.</param>
/// <param name="Advice">One line for the rule editor.</param>
public sealed record RemovedConditionField(string Field, Version RemovedIn, string? Replacement, string Advice)
{
    /// <summary>The column name alone, "last_ping".</summary>
    public string Column => Field[(Field.IndexOf('.') + 1)..];
}

/// <summary>
/// Rule conditions across LibreNMS versions (#11). LibreNMS 26.10.0
/// (migration bbb40ad) drops four columns from <c>devices</c>; a rule still
/// testing one fails on the server. New conditions shouldn't offer them, and
/// existing rules using them - built, imported or raw SQL - are flagged for
/// review. Custom SQL is never rewritten.
/// </summary>
public static class AlertRuleCompatibility
{
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
    };

    private static readonly IReadOnlyDictionary<string, RemovedConditionField> ByField =
        RemovedFields.ToDictionary(f => f.Field, StringComparer.OrdinalIgnoreCase);

    /// <summary>The removal, if <paramref name="field"/> no longer exists on a server of <paramref name="serverVersion"/>. Unknown versions assume the field is there.</summary>
    public static RemovedConditionField? Removal(string? field, Version? serverVersion)
        => field is not null && serverVersion is not null && ByField.TryGetValue(field, out var removed) && serverVersion >= removed.RemovedIn
            ? removed
            : null;

    /// <summary>
    /// The fields to offer for a new condition on a server of
    /// <paramref name="serverVersion"/> - <see cref="AlertConditionFields.Groups"/>
    /// without any it has removed. Existing conditions still resolve through
    /// <see cref="AlertConditionFields.Resolve"/>, so they open and can be fixed.
    /// </summary>
    public static IReadOnlyList<AlertConditionFieldGroup> FieldGroups(Version? serverVersion)
    {
        if (serverVersion is null || !RemovedFields.Any(f => serverVersion >= f.RemovedIn))
        {
            return AlertConditionFields.Groups;
        }

        return AlertConditionFields.Groups
            .Select(g => new AlertConditionFieldGroup(g.Name, g.Fields.Where(f => Removal(f.Field, serverVersion) is null).ToList()))
            .Where(g => g.Fields.Count > 0)
            .ToList();
    }

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
            var text = sql.Replace("`", string.Empty, StringComparison.Ordinal);
            foreach (var removed in RemovedFields)
            {
                // "devices.last_ping" or a bare "last_ping", but not "last_ping_timetaken" or "x.last_ping".
                var pattern = $@"(?<![\w.])(?:devices\.)?{Regex.Escape(removed.Column)}(?!\w)";
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
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
