using System.Text.Json;

namespace DesktopNMS.Core.Intelligence;

/// <summary>A bounded batch of typed lookup observations. Kept as evidence, never displayed as a separate card.</summary>
public sealed record IntelligenceStatusCard(string Title, string Scope, DateTimeOffset ObservedAt, IReadOnlyList<IntelligenceStatusRow> Rows);

public sealed record IntelligenceStatusRow(string Name, string Detail, string Status, string Tone, string DeviceStatus = "", int? DeviceId = null, string? AlertRule = null, string? Hostname = null, int? AlertId = null, IReadOnlyList<IntelligenceFaultRef>? Faults = null)
{
    public IntelligenceObjectReference? Object { get; init; }
}

/// <summary>
/// One fault of an alert group, as the MCP server lists it on LibreNMS 26.10
/// (DashyNMS/desktop#354) - so an answer's [[alert:ID]] can name a fault the
/// tools returned, not only the group's first.
/// </summary>
public sealed record IntelligenceFaultRef(int Id, string? Entity, string State);

public static class IntelligenceStatusCards
{
    public const int MaxRows = 100;

    public static IReadOnlyList<IntelligenceStatusCard> FromTool(string tool, string output, DateTimeOffset observedAt)
    {
        if (output.Length > 1_000_000 || tool is not ("active_alerts" or "alert_details" or "device_health" or "device_details" or "find_device" or "network_overview" or "device_sensors" or "health_overview" or "list_groups_and_locations")) return [];
        try
        {
            using var json = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (Text(root, "status") != "ok" || Get(root, "data") is not { ValueKind: JsonValueKind.Object } data) return [];
            var rows = new List<IntelligenceStatusRow>();
            var cards = new List<IntelligenceStatusCard>();
            if (tool is "device_sensors" or "health_overview")
            {
                foreach (var item in Items(Get(data, "readings")).Take(MaxRows))
                {
                    var reading = tool == "device_sensors" ? item : Get(item, "reading");
                    var device = tool == "device_sensors" ? Get(data, "device") : Get(item, "device");
                    if (Identifier(reading, "sensor_id") is not { } id || Id(device) is not { } deviceId) continue;
                    var state = Text(reading, "state") ?? "unknown";
                    rows.Add(new(Clean(Text(reading, "description") ?? $"Sensor {id}"), Name(device), state, state is "critical" or "warning" ? state : state == "ok" ? "up" : "unknown")
                    { Object = new("sensor", id, deviceId) });
                }
                if (rows.Count > 0) cards.Add(new("Sensor readings", "Readings returned by the lookup", observedAt, rows));
            }
            else if (tool == "list_groups_and_locations")
            {
                foreach (var kind in new[] { "group", "location" })
                foreach (var item in Items(Get(data, kind + "s")).Take(MaxRows - rows.Count))
                {
                    if (Identifier(item, kind + "_id") is not { } id || Text(item, "name") is not { Length: > 0 } name) continue;
                    rows.Add(new(Clean(name), kind == "group" ? "Device group" : "Location", "", "unknown") { Object = new(kind, id) });
                }
                if (rows.Count > 0) cards.Add(new("Network scopes", "Groups and locations returned by the lookup", observedAt, rows));
            }
            else if (tool == "alert_details")
            {
                var alert = Get(data, "alert");
                var device = Get(data, "device");
                var rule = Get(data, "rule");
                if (Identifier(alert, "alert_id") is not { } alertId) return [];
                var severity = Severity(Text(alert, "severity"));
                var ruleName = Clean(Text(rule, "name") ?? "Alert");

                // On 26.10 the alert is one fault of a group: say which, and of how many.
                var entity = Text(alert, "entity") is { } named ? " · " + Clean(named) : "";
                var ofGroup = Number(Get(data, "group"), "fault_count") is > 1 and var count ? $" · one of {count} faults" : "";
                rows.Add(new(Name(device), ruleName + entity + " · " + AlertState(Text(alert, "state")) + ofGroup, severity.Label, severity.Tone,
                    DeviceState(Text(device, "status")), Id(device), ruleName, Clean(Text(device, "hostname") ?? ""), alertId)
                { Object = new(AlertKind(alert, Get(data, "group").ValueKind == JsonValueKind.Object), alertId, Id(device), Identifier(rule, "rule_id")) });
                cards.Add(new("Alert details", "Individual alert lookup", observedAt, rows));
            }
            else if (tool == "active_alerts")
            {
                // Historical alert windows have different semantics; leave them to the text answer.
                if (Get(data, "window").ValueKind != JsonValueKind.Undefined) return [];
                foreach (var device in Items(Get(data, "devices")))
                {
                    foreach (var alert in Items(Get(device, "alerts")))
                    {
                        if (rows.Count >= MaxRows) break;
                        var severity = Severity(Text(alert, "severity"));
                        var state = AlertState(Text(alert, "state"));
                        var minutes = Number(alert, "age_minutes");
                        var age = minutes is { } m ? m >= 1440 ? $" · {m / 1440}d {m % 1440 / 60}h" : m >= 60 ? $" · {m / 60}h {m % 60}m" : $" · {m}m" : "";

                        // One row per rule firing on a device (#354): its faults are counted, and named for [[alert:ID]].
                        var faultCount = Number(alert, "fault_count");
                        var what = faultCount is > 1 ? $" · {faultCount} faults" : Text(alert, "entity") is { } entity ? " · " + Clean(entity) : "";
                        var faults = Items(Get(alert, "faults"))
                            .Select(f => Identifier(f, "fault_id") is { } faultId ? new IntelligenceFaultRef(faultId, Text(f, "entity") is { } name ? Clean(name) : null, AlertState(Text(f, "state"))) : null)
                            .OfType<IntelligenceFaultRef>()
                            .ToList();
                        rows.Add(new(Name(device), Clean(Text(alert, "rule") ?? "Alert") + what + " · " + state + age,
                            severity.Label, severity.Tone, DeviceState(Text(device, "device_status")), Id(device),
                            Clean(Text(alert, "rule") ?? "Alert"), Clean(Text(device, "hostname") ?? ""), Identifier(alert, "alert_id"), faults.Count > 0 ? faults : null)
                        { Object = Identifier(alert, "alert_id") is { } id ? new(AlertKind(alert, faults.Count > 0), id, Id(device), Identifier(alert, "rule_id")) : null });
                    }
                    if (rows.Count >= MaxRows) break;
                }
                var total = Number(Get(data, "page"), "total");
                var truncated = Get(root, "truncated").ValueKind == JsonValueKind.True;
                var faultTotal = Number(Get(data, "totals"), "faults");
                var scope = total is { } count
                    ? $"Showing {rows.Count} of {count} matching open alerts" + (faultTotal is { } f && f > count ? $" ({f} faults)" : "")
                    : $"Showing {rows.Count} returned alerts · total unavailable";
                scope += " · visible to this account";
                if (truncated) scope += " · tool result shortened";
                // Empty is only authoritative if an explicit total says there are no matching alerts.
                if (rows.Count > 0 || total == 0) cards.Add(new("Alert status", scope + " · includes acknowledged", observedAt, rows));
            }
            else if (tool == "network_overview")
            {
                var devices = Get(data, "devices");
                AddCount(rows, devices, "up", "Devices up", "Up", "up");
                AddCount(rows, devices, "down", "Devices down", "Down", "down");
                AddCount(rows, devices, "disabled", "Devices disabled", "Disabled", "unknown");
                if (rows.Count > 0) cards.Add(new("Device status", "Network overview", observedAt, rows.ToArray()));
                rows.Clear();
                var alerts = Get(data, "alerts");
                AddCount(rows, alerts, "critical", "Critical alerts", "Critical", "critical");
                AddCount(rows, alerts, "warning", "Warning alerts", "Warning", "warning");
                AddCount(rows, alerts, "acknowledged", "Acknowledged alerts", "Acknowledged", "unknown");
                AddCount(rows, alerts, "partly_acknowledged", "Partly acknowledged alerts", "Partly acknowledged", "warning");
                if (rows.Count > 0) cards.Add(new("Alert status", "Network overview · visible to this account · includes acknowledged", observedAt, rows));
            }
            else
            {
                var devices = tool == "find_device" ? Items(Get(data, "devices")) : new[] { Get(data, "device") };
                foreach (var device in devices.Take(MaxRows))
                {
                    if (device.ValueKind != JsonValueKind.Object || Id(device) is null) continue;
                    var status = DeviceState(Text(device, "status"));
                    var detail = string.Join(" · ", new[] { Text(device, "location"), Text(device, "hardware") }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => Clean(s!)));
                    rows.Add(new(Name(device), detail, status, status == "Up" ? "up" : status == "Down" ? "down" : "unknown", DeviceId: Id(device), Hostname: Clean(Text(device, "hostname") ?? "")));
                }
                if (rows.Count > 0) cards.Add(new("Device status", $"{rows.Count} returned device(s) · status at lookup time", observedAt, rows));
            }
            return cards;
        }
        catch (JsonException) { return []; }
    }

    private static void AddCount(List<IntelligenceStatusRow> rows, JsonElement source, string key, string label, string status, string tone)
    {
        if (Number(source, key) is { } count) rows.Add(new(label, count.ToString("N0"), status, tone));
    }

    private static string AlertKind(JsonElement alert, bool hasFaults) => Text(alert, "source") switch
    { "fault" => "fault", "alert" => "alert", _ => hasFaults ? "fault" : "alert" };

    private static JsonElement Get(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) ? child : default;
    private static string? Text(JsonElement value, string key) => Get(value, key) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    private static long? Number(JsonElement value, string key) => Get(value, key) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var count) && count >= 0 ? count : null;
    private static int? Id(JsonElement device) => Identifier(device, "device_id");
    private static int? Identifier(JsonElement value, string key) => Number(value, key) is > 0 and <= int.MaxValue and var id ? (int)id : null;
    private static IEnumerable<JsonElement> Items(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static string Name(JsonElement device) => Clean(Text(device, "display") ?? Text(device, "hostname") ?? $"Device {Id(device)?.ToString() ?? "unknown"}");
    private static string Clean(string value) => new(value.Where(c => !char.IsControl(c)).Take(200).ToArray());
    private static string DeviceState(string? state) => state switch { "up" => "Up", "down" => "Down", "disabled" => "Disabled", _ => "Unknown" };
    private static string AlertState(string? state) => state switch { "active" => "Active", "acknowledged" => "Acknowledged", "partly_acknowledged" => "Partly acknowledged", "recovered" => "Recovered", _ => "State unknown" };
    private static (string Label, string Tone) Severity(string? severity) => severity switch { "critical" => ("Critical", "critical"), "warning" => ("Warning", "warning"), "ok" => ("OK", "up"), _ => ("Unknown", "unknown") };
}
