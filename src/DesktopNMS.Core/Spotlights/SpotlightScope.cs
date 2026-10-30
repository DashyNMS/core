namespace DesktopNMS.Core.Spotlights;

/// <summary>
/// A Spotlight worked out against the server (see <see cref="SpotlightResolver"/>):
/// the devices it covers now, the entries as they're called now, and those
/// the server no longer has. Either the whole network, or exactly
/// <see cref="DeviceIds"/> - which can be empty, for a group with no members.
/// </summary>
public sealed class SpotlightScope
{
    public const string WholeNetworkSummary = "Whole network";

    private SpotlightScope(bool isWholeNetwork, IReadOnlySet<int> deviceIds, IReadOnlyList<SpotlightTarget> targets, IReadOnlyList<SpotlightTarget> missing)
    {
        IsWholeNetwork = isWholeNetwork;
        DeviceIds = deviceIds;
        Targets = targets;
        Missing = missing;
    }

    /// <summary>No Spotlight: every device.</summary>
    public static SpotlightScope WholeNetwork { get; } = new(true, new HashSet<int>(), Array.Empty<SpotlightTarget>(), Array.Empty<SpotlightTarget>());

    public static SpotlightScope Of(IReadOnlySet<int> deviceIds, IReadOnlyList<SpotlightTarget> targets, IReadOnlyList<SpotlightTarget> missing)
        => new(false, deviceIds, targets, missing);

    public bool IsWholeNetwork { get; }

    /// <summary>The devices covered - empty when <see cref="IsWholeNetwork"/>, so check that first or use <see cref="Includes"/>.</summary>
    public IReadOnlySet<int> DeviceIds { get; }

    /// <summary>Every entry, in the order chosen, with a group's or location's current name.</summary>
    public IReadOnlyList<SpotlightTarget> Targets { get; }

    /// <summary>Entries the server no longer has (or this account can't see). They stay saved: the card says so rather than quietly widening to the whole network.</summary>
    public IReadOnlyList<SpotlightTarget> Missing { get; }

    public bool HasMissing => Missing.Count > 0;

    /// <summary>Kept to a Spotlight that covers no devices at all - nothing to fetch.</summary>
    public bool IsEmpty => !IsWholeNetwork && DeviceIds.Count == 0;

    public bool Includes(int deviceId) => IsWholeNetwork || DeviceIds.Contains(deviceId);

    /// <summary>"Core routers, edge-01", with "Unknown group" and so on for a missing entry, or "Whole network".</summary>
    public string Summary => IsWholeNetwork ? WholeNetworkSummary : Describe(Targets, Missing);

    /// <summary>
    /// The same wording for a saved Spotlight before it has been worked out -
    /// a card's subtitle while it loads, or when the server can't be reached.
    /// </summary>
    public static string Describe(IReadOnlyList<SpotlightTarget>? targets)
        => targets is { Count: > 0 } ? Describe(targets, Array.Empty<SpotlightTarget>()) : WholeNetworkSummary;

    /// <summary>What a missing entry is called: "Unknown group", "Unknown device" or "Unknown location".</summary>
    public static string UnknownName(SpotlightTarget target) => target.Kind switch
    {
        SpotlightKind.Group => "Unknown group",
        SpotlightKind.Location => "Unknown location",
        _ => "Unknown device",
    };

    private static string Describe(IReadOnlyList<SpotlightTarget> targets, IReadOnlyList<SpotlightTarget> missing)
        => string.Join(", ", targets.Select(t => missing.Any(m => m.SameAs(t))
            ? UnknownName(t) + (string.IsNullOrWhiteSpace(t.Name) ? string.Empty : " (" + t.Name + ")")
            : t.Name));
}
