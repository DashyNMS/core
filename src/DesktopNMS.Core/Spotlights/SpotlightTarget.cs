using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Spotlights;

/// <summary>
/// One entry in a dashboard card's Spotlight (desktop#552): a device, a
/// device group or a location, with the name it had when it was chosen so a
/// card can still say what it was kept to if the server no longer has it.
/// </summary>
public sealed class SpotlightTarget
{
    public const string DeviceKind = "device";
    public const string GroupKind = "group";
    public const string LocationKind = "location";

    public SpotlightTarget()
    {
    }

    public SpotlightTarget(SpotlightKind kind, int id, string name)
    {
        KindName = NameOf(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "A dashboard Spotlight holds devices, groups and locations.");
        Id = id;
        Name = name;
    }

    public static SpotlightTarget Device(int id, string name) => new(SpotlightKind.Device, id, name);

    public static SpotlightTarget Group(int id, string name) => new(SpotlightKind.Group, id, name);

    public static SpotlightTarget Location(int id, string name) => new(SpotlightKind.Location, id, name);

    /// <summary>
    /// "device", "group" or "location" as stored. Text rather than the enum
    /// so a kind a newer app adds reads as unknown here instead of failing
    /// the whole settings file (desktop#197).
    /// </summary>
    [JsonPropertyName("kind")]
    public string KindName { get; set; } = DeviceKind;

    /// <summary>The kind, or null for one this version doesn't know - which <see cref="SpotlightResolver"/> leaves out.</summary>
    [JsonIgnore]
    public SpotlightKind? Kind => KindName?.Trim().ToLowerInvariant() switch
    {
        DeviceKind => SpotlightKind.Device,
        GroupKind => SpotlightKind.Group,
        LocationKind => SpotlightKind.Location,
        _ => null,
    };

    /// <summary>The LibreNMS device, device group or location id.</summary>
    public int Id { get; set; }

    /// <summary>The name shown for it: a device in the user's naming style, or the group's or location's own name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Anything a newer app stores on an entry, kept so saving here doesn't strip it (as <see cref="Configuration.DashboardWidget.Extra"/>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The same thing, whatever it's called: same kind and id.</summary>
    public bool SameAs(SpotlightTarget other)
        => string.Equals(KindName, other.KindName, StringComparison.OrdinalIgnoreCase) && Id == other.Id;

    public SpotlightTarget Clone() => new()
    {
        KindName = KindName,
        Id = Id,
        Name = Name,
        Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra),
    };

    public override string ToString() => Name;

    private static string? NameOf(SpotlightKind kind) => kind switch
    {
        SpotlightKind.Device => DeviceKind,
        SpotlightKind.Group => GroupKind,
        SpotlightKind.Location => LocationKind,
        _ => null,
    };
}
