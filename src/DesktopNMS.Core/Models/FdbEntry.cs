using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One row from /api/v0/devices/{id}/fdb - a MAC address the switch has
/// learned on one of its ports, and the VLAN it was seen on. LibreNMS 26.11
/// changed how that VLAN is reported (librenms#21152): older servers send
/// <see cref="VlanId"/>, newer ones <see cref="VlanNumber"/>. Read the VLAN
/// through <see cref="DesktopNMS.Core.Devices.FdbVlanResolver"/>, which
/// handles both, rather than either property directly.
/// </summary>
public sealed class FdbEntry
{
    [JsonPropertyName("ports_fdb_id")]
    public int Id { get; set; }

    [JsonPropertyName("port_id")]
    public int PortId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("mac_address")]
    public string? MacAddress { get; set; }

    /// <summary>
    /// LibreNMS's own internal id for the VLAN row, NOT the 802.1Q VLAN
    /// number - confirmed against a live server, where this held values like
    /// 50 on a device whose actual VLANs were 1/2074/2076/2099/2102/2254.
    /// Resolve the real tag and name via <see cref="Vlan"/> (<see cref="IVlansApi"/>),
    /// keyed by this value. Only servers before 26.11 send it; it is null on
    /// newer ones, which send <see cref="VlanNumber"/> instead.
    /// </summary>
    [JsonPropertyName("vlan_id")]
    public int? VlanId { get; set; }

    /// <summary>
    /// The actual 802.1Q VLAN number, sent directly by LibreNMS 26.11 and
    /// later. Null on older servers, and on newer ones when the switch did
    /// not say which VLAN it learned the address on - an unknown VLAN, not
    /// VLAN 0 or a missing entry.
    /// </summary>
    [JsonPropertyName("vlan")]
    public int? VlanNumber { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
