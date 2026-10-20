using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A row from LibreNMS's alerts list: before 26.10, one per rule and device
/// (/api/v0/alerts, the alerts table); from 26.10, one per matching entity -
/// a core, a port, a sensor - (/api/v0/faults, alert_faults). See
/// <see cref="Source"/> and <see cref="Identity"/>.
/// </summary>
/// <remarks>
/// The API selects <c>devices.hostname</c>, every column of the alerts or
/// alert_faults table, and <c>severity</c>/<c>name</c>/<c>proc</c>/<c>notes</c>
/// from <c>alert_rules</c>. Unrecognised columns land in
/// <see cref="AdditionalData"/> so a LibreNMS upgrade that adds columns does
/// not break deserialisation.
/// </remarks>
public sealed class Alert
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("rule_id")]
    public int RuleId { get; set; }

    /// <summary>Raw alerts.state value. Use <see cref="State"/> for the typed form.</summary>
    [JsonPropertyName("state")]
    public int StateValue { get; set; }

    /// <summary>True once LibreNMS has dispatched the alert to its transports.</summary>
    [JsonPropertyName("alerted")]
    public bool Alerted { get; set; }

    [JsonPropertyName("open")]
    public bool Open { get; set; }

    /// <summary>Free-text note; acknowledgements are appended here by the server.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>Server-local time the alert last changed state.</summary>
    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; set; }

    /// <summary>
    /// Opaque JSON blob LibreNMS uses for bookkeeping (e.g. until_clear). It
    /// arrives as a string from the database column, but has been seen as an
    /// embedded object, hence the tolerant converter.
    /// </summary>
    [JsonPropertyName("info")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseStringConverter))]
    public string? Info { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    /// <summary>Raw rule severity string. Use <see cref="Severity"/> for the typed form.</summary>
    [JsonPropertyName("severity")]
    public string? SeverityText { get; set; }

    /// <summary>The alert rule name, e.g. "Device Down! Due to no ICMP response.".</summary>
    [JsonPropertyName("name")]
    public string? RuleName { get; set; }

    /// <summary>URL of the rule's procedure/runbook document, if the rule defines one.</summary>
    [JsonPropertyName("proc")]
    public string? Procedure { get; set; }

    /// <summary>Operator notes attached to the rule.</summary>
    [JsonPropertyName("notes")]
    public string? RuleNotes { get; set; }

    /// <summary>A fault's entity type, e.g. "processor" or "port" - null on a legacy alert or a fault LibreNMS didn't type.</summary>
    [JsonPropertyName("entity_type")]
    public string? EntityType { get; set; }

    /// <summary>The fault's entity id (a processor id, a port id...) - null on a legacy alert.</summary>
    [JsonPropertyName("entity_id")]
    public long? EntityId { get; set; }

    /// <summary>What tells this fault apart from its device/rule siblings - empty for a fault LibreNMS made from a pre-26.10 alert, null on a legacy alert.</summary>
    [JsonPropertyName("entity_key")]
    public string? EntityKey { get; set; }

    /// <summary>
    /// The fault's own details: the rule's matched rows for this entity
    /// (26.10+). Legacy alerts have none - their details are in the alert log.
    /// May hold anything the device reports, so redact before showing or sending it.
    /// </summary>
    [JsonPropertyName("details")]
    public JsonElement? Details { get; set; }

    /// <summary>When this fault was first seen (26.10+).</summary>
    [JsonPropertyName("first_seen")]
    public DateTime? FirstSeen { get; set; }

    /// <summary>When this fault was last seen matching (26.10+).</summary>
    [JsonPropertyName("last_seen")]
    public DateTime? LastSeen { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>Which kind of row this is - set by <see cref="Api.IAlertsApi"/> from the endpoint it came from.</summary>
    [JsonIgnore]
    public AlertSource Source { get; set; } = AlertSource.Alert;

    /// <summary>Id and source together - what to keep across polls and restarts, see <see cref="AlertKey"/>.</summary>
    [JsonIgnore]
    public AlertKey Identity => new(Source, Id);

    [JsonIgnore]
    public AlertState State => AlertStateExtensions.FromValue(StateValue);

    [JsonIgnore]
    public AlertSeverity Severity => AlertSeverityExtensions.Parse(SeverityText);

    [JsonIgnore]
    public bool IsAcknowledged => State == AlertState.Acknowledged;

    [JsonIgnore]
    public string DisplayHostname => string.IsNullOrWhiteSpace(Hostname) ? $"device {DeviceId}" : Hostname!;

    [JsonIgnore]
    public string DisplayRuleName => string.IsNullOrWhiteSpace(RuleName) ? $"Rule {RuleId}" : RuleName!;

    /// <summary>
    /// Identity used to tell "the same alert as last poll" from "a new alert"
    /// within one server's rows - the alerts table reuses a row for a
    /// rule/device pair, and a fault keeps its row while it's open. Across
    /// sessions or an upgrade to LibreNMS 26.10, use <see cref="Identity"/>.
    /// </summary>
    [JsonIgnore]
    public int Key => Id;

    public override string ToString() => $"{Identity} {DisplayHostname}: {DisplayRuleName} [{Severity}/{State}]";
}
