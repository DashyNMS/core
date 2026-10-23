using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Sensors;

/// <summary>A label and independently evidenced severity; an unresolved result must never imply OK.</summary>
public sealed record SensorStateResolution(string? Label, AlertSeverity Severity, bool HasAuthoritativeSeverity,
    string? DefinitionId = null, string? CatalogueVersion = null, string? SourceRepository = null)
{
    public static SensorStateResolution Unresolved { get; } = new(null, AlertSeverity.Unknown, false);
}

public sealed record SensorDefinitionSource(string Id, string Kind, string Location, string Revision, string Licence, string? Notice = null);
public sealed record SensorDefinitionMatch(string Os, string SensorClass, string SensorType, string OidPrefix);
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
        if (catalogue.SchemaVersion != 1 || !SensorDefinitionIndex.IsVersion(catalogueVersion)
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
        var selectors = new HashSet<SensorDefinitionMatch>();
        foreach (var item in catalogue.Definitions)
        {
            if (item is null || !Text(item.Id, 512) || !ids.Add(item.Id) || item.Match is not { } match
                || !selectors.Add(match) || !SensorDefinitionIndex.IsOs(match.Os) || match.SensorClass != "state"
                || !Text(match.SensorType, 256) || !IsOid(match.OidPrefix)
                || (expectedOs is not null && match.Os != expectedOs)
                || item.States is not { Length: > 0 and <= 4096 } || item.SourceRefs is not { Length: > 0 and <= 100 })
                throw new InvalidDataException("Invalid or ambiguous sensor definition.");
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
        catalogue._lookup = catalogue.Definitions.GroupBy(item => (item.Match.Os, item.Match.SensorType))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Match.OidPrefix.Length).ToArray());
        return catalogue;
    }

    internal SensorDefinitionCatalogue ForRelease(string version) => new()
    {
        SchemaVersion = SchemaVersion, CatalogueVersion = version, Sources = Sources, Definitions = Definitions, _lookup = _lookup,
    };

    internal static bool Text(string? value, int length) => !string.IsNullOrWhiteSpace(value) && value.Length <= length;
    internal static bool IsOid(string? value) => value is { Length: <= 1024 } && Regex.IsMatch(value, @"^[0-2](?:\.(?:0|[1-9][0-9]*))+$", RegexOptions.CultureInvariant);
    private static bool IsInteger(string? value) => value is { Length: <= 32 } && Regex.IsMatch(value, @"^(?:0|-?[1-9][0-9]*)$", RegexOptions.CultureInvariant);

    public SensorStateResolution? Resolve(Sensor sensor, string? os)
    {
        if (!sensor.HasClass("state") || sensor.Current is not { } value || !double.IsFinite(value)
            || Math.Abs(value) > 9007199254740991d || Math.Truncate(value) != value || sensor.Oid is not { } oid
            || string.IsNullOrWhiteSpace(os) || string.IsNullOrWhiteSpace(sensor.SensorType)) return null;
        oid = oid.TrimStart('.');
        if (!IsOid(oid)) return null;
        if (!_lookup.TryGetValue((os, sensor.SensorType), out var candidates)) return null;
        var definition = candidates.FirstOrDefault(item => oid == item.Match.OidPrefix || oid.StartsWith(item.Match.OidPrefix + ".", StringComparison.Ordinal));
        if (definition is null) return null;
        var state = definition.States.SingleOrDefault(state => state.Value == value.ToString("0", CultureInfo.InvariantCulture));
        if (state is null) return null;
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
        if (index.SchemaVersion != 1 || !IsVersion(index.CatalogueVersion) || index.Shards is not { Length: > 0 and <= 10000 }
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
