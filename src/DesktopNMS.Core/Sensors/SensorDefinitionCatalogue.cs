using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Sensors;

public enum SensorDefinitionOutcome
{
    Resolved, Disabled, NotStateSensor, NoReading, InvalidReading, MissingOperatingSystem,
    MissingIdentity, CatalogueUnavailable, UnsupportedOperatingSystem, ShardUnavailable,
    TypeMissing, OidMismatch, ContextUnavailable, ContextMismatch, UnknownValue, Ambiguous,
}

/// <summary>A label and independently evidenced severity; an unresolved result must never imply OK.</summary>
public sealed record SensorStateResolution(string? Label, AlertSeverity Severity, bool HasAuthoritativeSeverity,
    string? DefinitionId = null, string? CatalogueVersion = null, string? SourceRepository = null)
{
    public SensorDefinitionOutcome Outcome { get; init; } = SensorDefinitionOutcome.Resolved;
    public static SensorStateResolution Unresolved { get; } = new(null, AlertSeverity.Unknown, false) { Outcome = SensorDefinitionOutcome.TypeMissing };
    internal static SensorStateResolution Missing(SensorDefinitionOutcome outcome) => Unresolved with { Outcome = outcome };
}

public sealed record SensorDefinitionSource(string Id, string Kind, string Location, string Revision, string Licence, string? Notice = null);
public sealed record SensorDefinitionMatch(string Os, string SensorClass, string SensorType, string OidPrefix)
{
    public string TypeMatch { get; init; } = "exact";
    public JsonElement[]? Conditions { get; init; }
    public string? OidSuffix { get; init; }
}
public sealed record SensorDefinitionState(string Value, string Label, string? GenericState);
public sealed record SensorDefinitionReference(string SourceId, string Path, string Symbol, bool SuppliesSeverity);
public sealed record SensorDefinition(string Id, SensorDefinitionMatch Match, SensorDefinitionState[] States, SensorDefinitionReference[] SourceRefs);

/// <summary>One device OS's catalogue shard. Loading unrelated systems is unnecessary.</summary>
public sealed class SensorDefinitionCatalogue
{
    public required int SchemaVersion { get; init; }
    [JsonIgnore]
    public string CatalogueVersion { get; private set; } = "";
    public required SensorDefinitionSource[] Sources { get; init; }
    public required SensorDefinition[] Definitions { get; init; }
    private Dictionary<(string Os, string Type), SensorDefinition[]> _lookup = new();
    private Dictionary<string, SensorDefinition[]> _patterns = new();

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    internal static T Read<T>(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        CheckUniqueProperties(document.RootElement);
        return document.Deserialize<T>(Json) ?? throw new InvalidDataException("Empty sensor definitions data.");
    }

    private static void CheckUniqueProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                CheckUniqueProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) CheckUniqueProperties(child);
    }

    public static SensorDefinitionCatalogue Load(ReadOnlySpan<byte> bytes, string? expectedOs = null, string catalogueVersion = "1.0.0")
    {
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Sensor definitions shard is too large.");
        var catalogue = Read<SensorDefinitionCatalogue>(bytes);
        if (catalogue.SchemaVersion is not (1 or 2) || !SensorDefinitionIndex.IsVersion(catalogueVersion)
            || catalogue.Sources is not { Length: > 0 and <= 100 } || catalogue.Definitions is not { Length: > 0 and <= 20000 })
            throw new InvalidDataException("Unsupported or empty sensor definitions catalogue.");
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in catalogue.Sources)
        {
            if (source is null || !Text(source.Id, 256) || !sources.Add(source.Id) || !Text(source.Location, 2048)
                || !Text(source.Revision, 256) || !Text(source.Licence, 256)
                || source.Kind is not ("librenms-discovery" or "mib-enum" or "reviewed-override"))
                throw new InvalidDataException("Invalid sensor definition provenance.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var selectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in catalogue.Definitions)
        {
            if (item is null || !Text(item.Id, 512) || !ids.Add(item.Id) || item.Match is not { } match
                || !selectors.Add(JsonSerializer.Serialize(match, Json)) || !SensorDefinitionIndex.IsOs(match.Os) || match.SensorClass != "state"
                || !Text(match.SensorType, 256) || !IsOid(match.OidPrefix)
                || (expectedOs is not null && match.Os != expectedOs)
                || item.States is not { Length: > 0 and <= 4096 } || item.SourceRefs is not { Length: > 0 and <= 100 })
                throw new InvalidDataException("Invalid or ambiguous sensor definition.");
            if (match.TypeMatch is not ("exact" or "template" or "any")
                || (catalogue.SchemaVersion == 1 && (match.TypeMatch != "exact" || match.Conditions is not null || match.OidSuffix is not null))
                || (match.TypeMatch == "any" && match.SensorType != "*")
                || (match.TypeMatch == "template" && (!match.SensorType.Contains('*') || match.SensorType.Count(c => c == '*') > 8))
                || match.Conditions is { Length: > 16 }
                || (match.OidSuffix is not null && !IsOidSuffix(match.OidSuffix)))
                throw new InvalidDataException("Unsupported sensor selector.");
            foreach (var condition in match.Conditions ?? []) SensorDefinitionConditions.Validate(condition);
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in item.States)
            {
                if (state is null || !Text(state.Label, 1024) || !IsInteger(state.Value) || !values.Add(state.Value)
                    || state.GenericState is not (null or "ok" or "warning" or "critical" or "unknown"))
                    throw new InvalidDataException("Invalid or duplicate sensor state.");
            }
            if (item.SourceRefs.Any(reference => reference is null || !sources.Contains(reference.SourceId)
                || !Text(reference.Path, 2048) || !Text(reference.Symbol, 512))
                || (item.States.Any(state => state.GenericState is not null) && !item.SourceRefs.Any(reference => reference.SuppliesSeverity)))
                throw new InvalidDataException("Sensor state severity has no supporting provenance.");
        }
        catalogue.CatalogueVersion = catalogueVersion;
        catalogue._lookup = catalogue.Definitions.Where(item => item.Match.TypeMatch == "exact").GroupBy(item => (item.Match.Os, item.Match.SensorType))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Match.OidPrefix.Length).ToArray());
        catalogue._patterns = catalogue.Definitions.Where(item => item.Match.TypeMatch != "exact").GroupBy(item => item.Match.Os)
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (catalogue._patterns.Values.Any(items => items.Length > 512)) throw new InvalidDataException("Too many parameterised sensor selectors.");
        return catalogue;
    }

    internal SensorDefinitionCatalogue ForRelease(string version) => new()
    {
        SchemaVersion = SchemaVersion, CatalogueVersion = version, Sources = Sources, Definitions = Definitions, _lookup = _lookup, _patterns = _patterns,
    };

    internal static bool Text(string? value, int length) => !string.IsNullOrWhiteSpace(value) && value.Length <= length;
    internal static bool IsOid(string? value) => value is { Length: <= 1024 } && Regex.IsMatch(value, @"^[0-2](?:\.(?:0|[1-9][0-9]*))+$", RegexOptions.CultureInvariant);
    private static bool IsOidSuffix(string? value) => value is { Length: > 0 and <= 1024 } && Regex.IsMatch(value, @"^(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))*$", RegexOptions.CultureInvariant);
    private static bool IsInteger(string? value) => value is { Length: <= 32 } && Regex.IsMatch(value, @"^(?:0|-?[1-9][0-9]*)$", RegexOptions.CultureInvariant);

    public SensorStateResolution? Resolve(Sensor sensor, string? os) => Resolve(sensor, os, null);

    /// <summary>Source predicates need actual device metadata; absent context never broadens a match.</summary>
    public SensorStateResolution? Resolve(Sensor sensor, string? os, Device? device)
    {
        var result = ResolveDetailed(sensor, os, device);
        return result.Outcome == SensorDefinitionOutcome.Resolved ? result : null;
    }

    /// <summary>Explain coverage and reading failures without changing the original nullable resolver API.</summary>
    public SensorStateResolution ResolveDetailed(Sensor sensor, string? os, Device? device = null)
    {
        if (!sensor.HasClass("state")) return SensorStateResolution.Missing(SensorDefinitionOutcome.NotStateSensor);
        if (sensor.Current is not { } value) return SensorStateResolution.Missing(SensorDefinitionOutcome.NoReading);
        if (!double.IsFinite(value) || Math.Abs(value) > 9007199254740991d || Math.Truncate(value) != value)
            return SensorStateResolution.Missing(SensorDefinitionOutcome.InvalidReading);
        if (string.IsNullOrWhiteSpace(os)) return SensorStateResolution.Missing(SensorDefinitionOutcome.MissingOperatingSystem);
        if (sensor.Oid is not { } oid || !Text(sensor.SensorType, 256)) return SensorStateResolution.Missing(SensorDefinitionOutcome.MissingIdentity);
        if (device is not null && (device.DeviceId != sensor.DeviceId || device.Os != os))
            return SensorStateResolution.Missing(SensorDefinitionOutcome.ContextMismatch);
        oid = oid.TrimStart('.');
        if (!IsOid(oid)) return SensorStateResolution.Missing(SensorDefinitionOutcome.MissingIdentity);
        _lookup.TryGetValue((os, sensor.SensorType!), out var exact);
        _patterns.TryGetValue(os, out var patterns);
        var typed = (exact ?? []).Concat(patterns ?? []).Where(item => SensorDefinitionConditions.TypeMatches(item.Match, sensor.SensorType!)).ToArray();
        if (typed.Length == 0) return SensorStateResolution.Missing(SensorDefinitionOutcome.TypeMissing);
        var identity = typed.Where(item => SensorDefinitionConditions.OidMatches(item.Match, oid)).ToArray();
        if (identity.Length == 0) return SensorStateResolution.Missing(SensorDefinitionOutcome.OidMismatch);
        var candidates = identity.Where(item => (item.Match.Conditions ?? []).All(condition => SensorDefinitionConditions.Evaluate(condition, os, device) == true))
            .OrderByDescending(item => item.Match.OidPrefix.Length)
            .ThenByDescending(item => item.Match.OidSuffix?.Length ?? 0)
            .ThenByDescending(item => item.Match.TypeMatch == "exact" ? 2 : item.Match.TypeMatch == "template" ? 1 : 0).ToArray();
        if (candidates.Length == 0) return SensorStateResolution.Missing(identity.Any(item => (item.Match.Conditions ?? []).Any(condition => SensorDefinitionConditions.Evaluate(condition, os, device) is null))
            ? SensorDefinitionOutcome.ContextUnavailable : SensorDefinitionOutcome.ContextMismatch);
        var definition = candidates[0];
        if (candidates.Skip(1).Any(item => item.Match.OidPrefix.Length == definition.Match.OidPrefix.Length
            && (item.Match.OidSuffix?.Length ?? 0) == (definition.Match.OidSuffix?.Length ?? 0)
            && item.Match.TypeMatch == definition.Match.TypeMatch && !item.States.OrderBy(state => state.Value, StringComparer.Ordinal).SequenceEqual(definition.States.OrderBy(state => state.Value, StringComparer.Ordinal))))
            return SensorStateResolution.Missing(SensorDefinitionOutcome.Ambiguous);
        var state = definition.States.SingleOrDefault(state => state.Value == value.ToString("0", CultureInfo.InvariantCulture));
        if (state is null) return SensorStateResolution.Missing(SensorDefinitionOutcome.UnknownValue);
        return new(state.Label, AlertSeverityExtensions.Parse(state.GenericState), state.GenericState is not null,
            definition.Id, CatalogueVersion, Sources.First(source => source.Id == definition.SourceRefs[0].SourceId).Location);
    }
}

public sealed record SensorDefinitionAsset(string Sha256, int DownloadBytes);
public sealed record SensorDefinitionShard(string Os, string Sha256, int DownloadBytes, int ExpandedBytes, int Definitions);

/// <summary>The small stable index; OS data stays on disk until needed.</summary>
public sealed class SensorDefinitionIndex
{
    public required int SchemaVersion { get; init; }
    public required string CatalogueVersion { get; init; }
    public required SensorDefinitionShard[] Shards { get; init; }
    public required SensorDefinitionAsset Notices { get; init; }

    internal static bool IsVersion(string? value) => value is { Length: <= 80 } && Regex.IsMatch(value, @"^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\+catalogue\.[1-9][0-9]*)?$", RegexOptions.CultureInvariant);
    internal static bool IsOs(string? value) => value is { Length: <= 128 } && Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant) && value is not "." and not "..";
    internal static bool IsHash(string? value) => value is { Length: 64 } && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    public static SensorDefinitionIndex Load(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Sensor definitions index is too large.");
        var index = SensorDefinitionCatalogue.Read<SensorDefinitionIndex>(bytes);
        if (index.SchemaVersion is not (1 or 2) || !IsVersion(index.CatalogueVersion) || index.Shards is not { Length: > 0 and <= 10000 }
            || index.Notices is not { DownloadBytes: > 0 and <= 1048576 } || !IsHash(index.Notices.Sha256))
            throw new InvalidDataException("Unsupported sensor definitions index.");
        var systems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shard in index.Shards)
            if (shard is null || !IsOs(shard.Os) || !systems.Add(shard.Os) || !IsHash(shard.Sha256)
                || shard.DownloadBytes is <= 0 or > 524288 || shard.ExpandedBytes is <= 0 or > 2097152 || shard.Definitions <= 0)
                throw new InvalidDataException("Invalid or duplicate OS shard.");
        return index;
    }
}
