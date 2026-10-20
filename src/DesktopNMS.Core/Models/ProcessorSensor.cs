using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One CPU/processor reading from /api/v0/devices/{id}/health/processor/{sensor_id}.
/// Unlike <see cref="Sensor"/>, LibreNMS has no fleet-wide endpoint for these -
/// only a per-device list of ids, then one call per id for the actual reading.
/// </summary>
public sealed class ProcessorSensor
{
    [JsonPropertyName("processor_id")]
    public int ProcessorId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("processor_descr")]
    public string? Description { get; set; }

    [JsonPropertyName("processor_usage")]
    public double? UsagePercent { get; set; }

    /// <summary>
    /// LibreNMS's own configured warning threshold for this processor. Null
    /// when unset - from LibreNMS 26.10 (librenms#20768) that means the limit
    /// was removed, so rules skip this processor. Never read as zero.
    /// </summary>
    [JsonPropertyName("processor_perc_warn")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseNullableDoubleConverter))]
    public double? WarningPercent { get; set; }
}
