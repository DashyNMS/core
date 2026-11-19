using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Devices;

/// <summary>
/// The VLAN a forwarding-table entry was learned on, from either LibreNMS
/// response shape. Before 26.11 an entry carried only LibreNMS's internal
/// VLAN row id (<see cref="FdbEntry.VlanId"/>), which means nothing without
/// the VLAN table; from 26.11 it carries the 802.1Q number itself
/// (<see cref="FdbEntry.VlanNumber"/>, librenms#21152). Every app resolved
/// the old id on its own, so the new shape left their VLAN columns blank
/// (#86) - this is the one place that knows both.
/// </summary>
/// <remarks>
/// An internal id is never shown as a VLAN number: the two differ on real
/// servers, so an id with no matching VLAN row stays unknown. Likewise a
/// missing or out-of-range number stays unknown rather than becoming VLAN 0.
/// </remarks>
public sealed class FdbVlanResolver
{
    /// <summary>The lowest and highest usable 802.1Q VLAN numbers (0 and 4095 are reserved).</summary>
    private const int FirstVlan = 1;
    private const int LastVlan = 4094;

    private readonly Dictionary<int, Vlan> _byId = new();
    private readonly Dictionary<(int DeviceId, int Number), Vlan> _byDeviceAndNumber = new();

    /// <summary>
    /// A resolver with no VLAN table: direct numbers still resolve, legacy
    /// ids do not, and no names are known. For when the table could not be
    /// read.
    /// </summary>
    public static FdbVlanResolver Empty { get; } = new([]);

    /// <param name="vlans">
    /// The VLAN table (<see cref="IVlansApi.ListAsync"/>), for any number of
    /// devices. Only needed for names and for older servers' internal ids.
    /// </param>
    public FdbVlanResolver(IEnumerable<Vlan> vlans)
    {
        ArgumentNullException.ThrowIfNull(vlans);
        foreach (var vlan in vlans)
        {
            _byId.TryAdd(vlan.VlanId, vlan);

            // A device can list one number more than once (one row per VLAN
            // domain); the first row with a name is the one worth showing.
            var key = (vlan.DeviceId, vlan.VlanNumber);
            if (!_byDeviceAndNumber.TryGetValue(key, out var existing)
                || (string.IsNullOrWhiteSpace(existing.VlanName) && !string.IsNullOrWhiteSpace(vlan.VlanName)))
            {
                _byDeviceAndNumber[key] = vlan;
            }
        }
    }

    /// <summary>
    /// Whether resolving <paramref name="entries"/> needs the VLAN table for
    /// their numbers - true when any carries only an older server's internal
    /// id. Names always need the table; this lets a caller that only wants
    /// numbers skip a fleet-wide read on 26.11 and later.
    /// </summary>
    public static bool NeedsVlanTable(IEnumerable<FdbEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Any(e => e.VlanNumber is null && e.VlanId is > 0);
    }

    /// <summary>
    /// The 802.1Q number <paramref name="entry"/> was learned on, or null
    /// when it is unknown: the server did not say, the number is out of
    /// range, or an older server's internal id has no row in the VLAN table.
    /// </summary>
    public int? NumberOf(FdbEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.VlanNumber is { } direct)
        {
            return IsUsable(direct) ? direct : null;
        }

        return Legacy(entry) is { } vlan && IsUsable(vlan.VlanNumber) ? vlan.VlanNumber : null;
    }

    /// <summary>
    /// The VLAN's name on the device that learned <paramref name="entry"/>,
    /// or null when the VLAN or its name is unknown. On 26.11 and later it
    /// is matched by device and number; on older servers by internal id.
    /// </summary>
    public string? NameOf(FdbEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var name = Find(entry)?.VlanName;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>
    /// The VLAN table row for <paramref name="entry"/>, or null when there is
    /// none. A direct number with no row still has a number (<see cref="NumberOf"/>);
    /// it just has no name.
    /// </summary>
    public Vlan? Find(FdbEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.VlanNumber is { } direct)
        {
            return IsUsable(direct) && _byDeviceAndNumber.TryGetValue((entry.DeviceId, direct), out var vlan) ? vlan : null;
        }

        return Legacy(entry) is { } legacy && IsUsable(legacy.VlanNumber) ? legacy : null;
    }

    /// <summary>
    /// The row an older server's internal id points at. The id is the VLAN
    /// table's own key, so it is unique, but a row for another device means
    /// the table and the entry disagree - better unknown than wrong.
    /// </summary>
    private Vlan? Legacy(FdbEntry entry) =>
        entry.VlanId is { } id && id > 0 && _byId.TryGetValue(id, out var vlan)
        && (entry.DeviceId <= 0 || vlan.DeviceId <= 0 || vlan.DeviceId == entry.DeviceId)
            ? vlan
            : null;

    private static bool IsUsable(int number) => number is >= FirstVlan and <= LastVlan;
}
