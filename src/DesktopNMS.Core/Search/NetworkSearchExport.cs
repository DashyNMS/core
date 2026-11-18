using System.Globalization;
using System.Text;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Search;

/// <summary>
/// Network Search results as CSV (#91), like upstream's host export, so every
/// app writes the same file: one row per switch port an endpoint is learned
/// on, or one row for an endpoint with none.
/// </summary>
public static class NetworkSearchExport
{
    private static readonly string[] Header =
    [
        "Address", "MAC", "Vendor", "Names", "Switch", "Port", "VLAN", "Connection", "Last seen (UTC)", "Neighbour of",
    ];

    public static string ToCsv(IEnumerable<EndpointRecord> endpoints, MacAddressFormat macFormat = MacAddressFormat.Colon)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var csv = new StringBuilder();
        Line(csv, Header);

        foreach (var endpoint in endpoints)
        {
            var neighbours = string.Join("; ", endpoint.Neighbours.Select(n => Join(DeviceName(n.Device, n.DeviceId), n.Port?.DisplayName)).Distinct());
            var common = new
            {
                Address = string.Join("; ", endpoint.Addresses),
                Names = string.Join("; ", endpoint.Names),
            };

            if (endpoint.Locations.Count == 0)
            {
                var mac = endpoint.Macs.FirstOrDefault();
                Line(csv, [common.Address, Mac(mac?.Hex, macFormat), mac?.Vendor, common.Names, null, null, null, null, Time(mac?.LastSeen), neighbours]);
                continue;
            }

            foreach (var location in endpoint.Locations)
            {
                var mac = endpoint.Macs.FirstOrDefault(m => m.Hex == location.Mac);
                Line(csv,
                [
                    common.Address, Mac(location.Mac, macFormat), mac?.Vendor, common.Names,
                    DeviceName(location.Device, location.DeviceId), location.Port?.DisplayName ?? $"port {location.PortId}",
                    location.VlanNumber?.ToString(CultureInfo.InvariantCulture),
                    location.IsStale ? $"{Describe(location.Kind)} (stale)" : Describe(location.Kind),
                    Time(location.UpdatedAt), neighbours,
                ]);
            }
        }

        return csv.ToString();
    }

    private static string Describe(LocationKind kind) => kind switch
    {
        LocationKind.Direct => "Direct",
        LocationKind.Shared => "Shared port",
        _ => "Uplink",
    };

    private static string DeviceName(Device? device, int deviceId)
        => device?.Hostname ?? device?.SysName ?? $"device {deviceId.ToString(CultureInfo.InvariantCulture)}";

    private static string? Join(string a, string? b) => b is null ? a : $"{a} {b}";

    private static string? Mac(string? hex, MacAddressFormat format) => hex is null ? null : MacAddresses.Format(hex, format);

    private static string? Time(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder csv, IReadOnlyList<string?> fields)
    {
        csv.AppendJoin(',', fields.Select(Field));
        csv.Append("\r\n");
    }

    /// <summary>
    /// Quotes as RFC 4180 does, and defuses a value a spreadsheet would run as
    /// a formula - port descriptions and names come from the network, so
    /// anyone who can set one could otherwise plant a formula in the export.
    /// </summary>
    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
