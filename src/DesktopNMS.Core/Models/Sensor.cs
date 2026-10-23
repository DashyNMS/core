using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A row from /api/v0/resources/sensors - one reading from one sensor on one
/// device (dBm optical power, temperature, voltage, ...).
/// </summary>
public sealed class Sensor
{
    [JsonPropertyName("sensor_id")]
    public int SensorId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    /// <summary>The kind of sensor, e.g. "dbm", "temperature", "voltage".</summary>
    [JsonPropertyName("sensor_class")]
    public string? SensorClass { get; set; }

    [JsonPropertyName("sensor_type")]
    public string? SensorType { get; set; }

    [JsonPropertyName("sensor_oid")]
    public string? Oid { get; set; }

    /// <summary>Presentation resolved by the optional installation catalogue; never sent to LibreNMS.</summary>
    [JsonIgnore]
    public Sensors.SensorStateResolution? StateDefinition { get; set; }

    [JsonPropertyName("sensor_descr")]
    public string? Description { get; set; }

    /// <summary>Which reading of its kind on the device this is, e.g. "hpicfPseAvailablePower.1001" - what pairs a PoE budget's total with its used figure (see <see cref="Alerting.PoeBudget"/>).</summary>
    [JsonPropertyName("sensor_index")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseStringConverter))]
    public string? Index { get; set; }

    [JsonPropertyName("sensor_current")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseNullableDoubleConverter))]
    public double? Current { get; set; }

    /// <summary>A missing or non-finite reading is unavailable, never a healthy zero.</summary>
    [JsonIgnore]
    public bool HasReading => Current is { } value && double.IsFinite(value);

    /// <summary>Optional unit supplied by the API for a class the client doesn't know.</summary>
    [JsonPropertyName("sensor_unit")]
    public string? Unit { get; set; }

    /// <summary>LibreNMS's own high-critical limit, if one is configured.</summary>
    [JsonPropertyName("sensor_limit")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseNullableDoubleConverter))]
    public double? LimitHigh { get; set; }

    /// <summary>LibreNMS's own high-warning limit, if one is configured.</summary>
    [JsonPropertyName("sensor_limit_warn")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseNullableDoubleConverter))]
    public double? LimitHighWarn { get; set; }

    /// <summary>LibreNMS's own low-critical limit, if one is configured.</summary>
    [JsonPropertyName("sensor_limit_low")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseNullableDoubleConverter))]
    public double? LimitLow { get; set; }

    /// <summary>LibreNMS's own low-warning limit, if one is configured.</summary>
    [JsonPropertyName("sensor_limit_low_warn")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseNullableDoubleConverter))]
    public double? LimitLowWarn { get; set; }

    [JsonPropertyName("lastupdate")]
    public DateTime? LastUpdate { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    public bool HasClass(string sensorClass) => string.Equals(SensorClass, sensorClass, StringComparison.OrdinalIgnoreCase);

    public bool IsDbm => HasClass("dbm");
}
