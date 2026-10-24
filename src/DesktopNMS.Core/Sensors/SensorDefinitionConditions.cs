using System.Text.Json;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Sensors;

/// <summary>Bounded data predicates; unavailable context cannot satisfy a negated condition.</summary>
internal static class SensorDefinitionConditions
{
    internal static bool OidMatches(SensorDefinitionMatch match, string oid)
    {
        if (oid != match.OidPrefix && !oid.StartsWith(match.OidPrefix + ".", StringComparison.Ordinal)) return false;
        return match.OidSuffix is null || oid.Length > match.OidPrefix.Length + match.OidSuffix.Length + 2
            && oid.EndsWith("." + match.OidSuffix, StringComparison.Ordinal);
    }

    internal static void Validate(JsonElement node)
    {
        var count = 0;
        Validate(node, 0, ref count);
    }

    private static void Validate(JsonElement node, int depth, ref int count)
    {
        if (++count > 128 || depth > 8 || node.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid sensor condition.");
        var keys = node.EnumerateObject().Select(property => property.Name).ToArray();
        if (keys.Length == 1 && keys[0] is "all" or "any")
        {
            var children = node.GetProperty(keys[0]);
            if (children.ValueKind != JsonValueKind.Array || children.GetArrayLength() is < 1 or > 16)
                throw new InvalidDataException("Invalid sensor condition group.");
            foreach (var child in children.EnumerateArray()) Validate(child, depth + 1, ref count);
            return;
        }
        if (keys.Length == 1 && keys[0] == "not") { Validate(node.GetProperty("not"), depth + 1, ref count); return; }
        if (keys.Except(new[] { "field", "op", "value" }).Any()
            || !node.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String
            || field.GetString() is not ("os" or "hardware" or "version" or "sysObjectID")
            || !node.TryGetProperty("op", out var operation) || operation.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Unsupported sensor condition.");
        var op = operation.GetString();
        if (op == "present" && keys.Length == 2) return;
        if (op is not ("equals" or "notEquals" or "contains") || keys.Length != 3
            || !node.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String
            || !SensorDefinitionCatalogue.Text(value.GetString(), 1024))
            throw new InvalidDataException("Unsupported sensor condition operation.");
    }

    internal static bool? Evaluate(JsonElement node, string os, Device? device)
    {
        if (node.TryGetProperty("not", out var negated)) return !Evaluate(negated, os, device);
        if (node.TryGetProperty("all", out var all))
        {
            var values = all.EnumerateArray().Select(child => Evaluate(child, os, device)).ToArray();
            return values.Contains(false) ? false : values.Contains(null) ? null : true;
        }
        if (node.TryGetProperty("any", out var any))
        {
            var values = any.EnumerateArray().Select(child => Evaluate(child, os, device)).ToArray();
            return values.Contains(true) ? true : values.Contains(null) ? null : false;
        }
        var field = node.GetProperty("field").GetString();
        if (device is null && field != "os") return null;
        var value = field switch { "os" => os, "hardware" => device?.Hardware, "version" => device?.Version, "sysObjectID" => device?.SysObjectId, _ => null };
        var op = node.GetProperty("op").GetString();
        if (op == "present") return value is null ? null : true;
        if (value is null) return null;
        var expected = node.GetProperty("value").GetString()!;
        return op switch { "equals" => value == expected, "notEquals" => value != expected, "contains" => value.Contains(expected, StringComparison.Ordinal), _ => null };
    }

    internal static bool TypeMatches(SensorDefinitionMatch match, string type)
    {
        if (match.TypeMatch == "any") return true;
        if (match.TypeMatch == "exact") return type == match.SensorType;
        // Glob literals separated by *, with bounded input and no regular expressions.
        var pattern = match.SensorType;
        int p = 0, t = 0, star = -1, retry = 0;
        while (t < type.Length)
        {
            if (p < pattern.Length && pattern[p] == type[t]) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = t; }
            else if (star >= 0) { p = star + 1; t = ++retry; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
