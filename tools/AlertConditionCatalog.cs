// Replays LibreNMS's rule-builder field list (QueryBuilderFilter::generateTableFilter
// and Schema::getAllRelationshipPaths) offline against one release's
// resources/definitions/schema/db_schema.yaml, printing one catalogue line per
// field in AlertConditionFieldCatalog's format. Macros are not included; they
// come from config_definitions.json and rarely change.
//
//   gh api "repos/librenms/librenms/contents/resources/definitions/schema/db_schema.yaml?ref=26.11.0" \
//       -H "Accept: application/vnd.github.raw" > schema-26.11.0.yaml
//   dotnet run tools/AlertConditionCatalog.cs -- schema-26.11.0.yaml > fields-26.11.0.txt
//
// Diff two releases' output to see what a LibreNMS upgrade adds and removes,
// then update the catalogue and AlertRuleCompatibility's version lists (#87).
using System.Text.RegularExpressions;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: dotnet run tools/AlertConditionCatalog.cs -- <db_schema.yaml>");
    return 1;
}

var schema = ReadSchema(File.ReadAllLines(args[0]));
string[] tableBlacklist = ["device_group_device", "alerts", "alert_log"];
string[] relationshipBlacklist = ["devices_perms", "bill_perms", "ports_perms"];

// Schema::getTableRelationships: a column named x_id points at table xs (or xes).
var relationships = schema
    .Where(t => !relationshipBlacklist.Contains(t.Key))
    .ToDictionary(t => t.Key, t => t.Value.Select(c => TableFromKey(c.Field)).OfType<string>().Where(r => r != t.Key).ToList());

var valid = schema.Keys.Where(t => FindPath([t], "devices", []) is not null).Except(tableBlacklist).ToHashSet();

foreach (var (table, columns) in schema.OrderBy(t => t.Key, StringComparer.Ordinal))
{
    if (!valid.Contains(table))
    {
        continue;
    }

    foreach (var (field, type) in columns.OrderBy(c => c.Field, StringComparer.Ordinal))
    {
        if (field == "device_id" && table != "devices")
        {
            continue;
        }

        var kind = Kind(type);
        var line = kind switch
        {
            null => null,
            _ when Regex.IsMatch(field, "(_perc|_current|_usage|_perc_warn)$") => $"{table}.{field}|s",
            "e" =>
                $"{table}.{field}|e|" + string.Join(",", type[4..].Split(',').Select(v => v.Trim('(', ')', '\'', ' '))),
            _ => $"{table}.{field}|{kind}",
        };

        if (line is not null)
        {
            Console.WriteLine(line);
        }
    }
}

return 0;

// QueryBuilderFilter::getColumnType. Integers are strings there too (a TODO
// upstream); char, decimal, longtext and mediumtext fall through to null.
static string? Kind(string type) => type switch
{
    _ when type.StartsWith("varchar") || type.StartsWith("text") || type.StartsWith("double") || type.StartsWith("float") => "s",
    _ when type.StartsWith("int") || type.StartsWith("tinyint") || type.StartsWith("smallint") || type.StartsWith("mediumint") || type.StartsWith("bigint") => "s",
    _ when type.StartsWith("timestamp") || type.StartsWith("datetime") => "d",
    _ when type.StartsWith("enum") => "e",
    _ => null,
};

string? TableFromKey(string key)
{
    if (!key.EndsWith("_id"))
    {
        return null;
    }

    if (key == "app_id")
    {
        return "applications";
    }

    var guess = key[..^3];
    if (!guess.EndsWith('s'))
    {
        guess += guess.EndsWith('x') ? "es" : "s";
    }

    return schema.ContainsKey(guess) ? guess : null;
}

// Schema::findPathRecursive, line for line, including its dead-end search.
List<string>? FindPath(List<string> tables, string target, List<string> history)
{
    if (history.Count > 0)
    {
        tables = tables.Except(history).ToList();
    }

    foreach (var table in tables)
    {
        if (relationships[target].Contains(table))
        {
            return [table, target];
        }

        var related = relationships.GetValueOrDefault(table) ?? [];
        if (related.Count > 0)
        {
            if (related.Contains(target))
            {
                return [target, table];
            }

            if (FindPath(related, target, [.. history, .. tables]) is { } found)
            {
                return [.. found, table];
            }
        }
        else
        {
            var referrers = relationships.Where(r => r.Value.Contains(table)).Select(r => r.Key).ToList();
            if (FindPath(referrers, target, [.. history, .. tables]) is { } found)
            {
                return [.. found, table];
            }
        }
    }

    return null;
}

// db_schema.yaml is machine-written and regular: a table at column 0, its
// columns as one flow mapping per line under "  Columns:".
static Dictionary<string, List<(string Field, string Type)>> ReadSchema(string[] lines)
{
    var result = new Dictionary<string, List<(string, string)>>();
    List<(string, string)>? columns = null;
    var inColumns = false;
    foreach (var line in lines)
    {
        if (Regex.Match(line, @"^([A-Za-z0-9_]+):\s*$") is { Success: true } table)
        {
            columns = result[table.Groups[1].Value] = [];
            inColumns = false;
        }
        else if (line.StartsWith("  ") && !line.StartsWith("   "))
        {
            inColumns = line.Trim() == "Columns:";
        }
        else if (inColumns && columns is not null
                 && Regex.Match(line, @"Field: ([^,]+), Type: (""[^""]*""|'[^']*'|[^,]+),") is { Success: true } column)
        {
            columns.Add((column.Groups[1].Value.Trim(), column.Groups[2].Value.Trim('"', '\'')));
        }
    }

    return result;
}
