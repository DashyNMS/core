using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Sensors;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class SensorDefinitionRulesTests
{
    private static JsonObject Definition(string id, string type = "*", string kind = "any", string? condition = null, string label = "Ready")
        => JsonSerializer.SerializeToNode(new
        {
            id,
            match = new { os = "example-os", sensorClass = "state", sensorType = type, typeMatch = kind,
                oidPrefix = "1.3.6.1.4.1.32473.1", conditions = condition is null ? null : new[] { JsonSerializer.Deserialize<JsonElement>(condition) } },
            states = new[] { new { value = "2", label, genericState = "ok" } },
            sourceRefs = new[] { new { sourceId = "fixture", path = "fixture.php", symbol = "exampleState", suppliesSeverity = true } },
        })!.AsObject();

    private static SensorDefinitionCatalogue Catalogue(params JsonObject[] definitions)
    {
        var node = JsonSerializer.SerializeToNode(new { schemaVersion = 2,
            sources = new[] { new { id = "fixture", kind = "librenms-discovery", location = "https://example.net", revision = "fixture", licence = "MIT" } } })!.AsObject();
        node["definitions"] = new JsonArray(definitions.Cast<JsonNode>().ToArray());
        return SensorDefinitionCatalogue.Load(JsonSerializer.SerializeToUtf8Bytes(node));
    }

    private static Sensor Reading(string type = "exampleFan", double? value = 2) => new()
        { DeviceId = 12, SensorClass = "state", SensorType = type, Current = value, Oid = ".1.3.6.1.4.1.32473.1.7" };
    private static Device Context(string? hardware = "Example chassis") => new() { DeviceId = 12, Os = "example-os", Hardware = hardware };

    [Fact]
    public void Shared_rules_require_matching_device_context_and_preserve_oid_boundaries()
    {
        var catalogue = Catalogue(Definition("shared", condition: """{"field":"hardware","op":"contains","value":"Example"}"""));
        Assert.Equal("Ready", catalogue.Resolve(Reading(), "example-os", Context())!.Label);
        Assert.Equal(SensorDefinitionOutcome.ContextUnavailable, catalogue.ResolveDetailed(Reading(), "example-os").Outcome);
        Assert.Equal(SensorDefinitionOutcome.ContextMismatch, catalogue.ResolveDetailed(Reading(), "example-os", Context("Other")).Outcome);
        var sensor = Reading(); sensor.Oid = "1.3.6.1.4.1.32473.10.7";
        Assert.Equal(SensorDefinitionOutcome.OidMismatch, catalogue.ResolveDetailed(sensor, "example-os", Context()).Outcome);
        var wrongDevice = Context(); wrongDevice.DeviceId++;
        Assert.Null(catalogue.Resolve(Reading(), "example-os", wrongDevice));
    }

    [Fact]
    public void Negation_cannot_turn_missing_context_into_a_match()
    {
        var catalogue = Catalogue(Definition("negated", condition: """{"not":{"field":"hardware","op":"contains","value":"Excluded"}}"""));
        Assert.Null(catalogue.Resolve(Reading(), "example-os"));
        Assert.Null(catalogue.Resolve(Reading(), "example-os", Context(null)));
        Assert.NotNull(catalogue.Resolve(Reading(), "example-os", Context()));
        Assert.Null(catalogue.Resolve(Reading(), "example-os", Context("Excluded")));
    }

    [Fact]
    public void Templates_match_literals_and_exact_types_take_precedence()
    {
        var catalogue = Catalogue(Definition("template", "Input_*_State", "template"),
            Definition("exact", "Input_7_State", "exact", label: "Exact"));
        Assert.Equal("Exact", catalogue.Resolve(Reading("Input_7_State"), "example-os")!.Label);
        Assert.Equal("Ready", catalogue.Resolve(Reading("Input_8_State"), "example-os")!.Label);
        Assert.Null(catalogue.Resolve(Reading("Other_8_State"), "example-os"));
        Assert.Null(catalogue.Resolve(Reading("Input_8_State_extra"), "example-os"));
    }

    [Fact]
    public void Overlapping_rules_with_different_states_are_explicitly_ambiguous()
    {
        var catalogue = Catalogue(Definition("a", condition: """{"field":"hardware","op":"contains","value":"Example"}"""),
            Definition("b", condition: """{"field":"hardware","op":"equals","value":"Example chassis"}""", label: "Different"));
        Assert.Equal(SensorDefinitionOutcome.Ambiguous, catalogue.ResolveDetailed(Reading(), "example-os", Context()).Outcome);
        Assert.Null(catalogue.Resolve(Reading(), "example-os", Context()));
    }

    [Theory]
    [InlineData("""{"field":"hardware","op":"regex","value":".*"}""")]
    [InlineData("""{"unsupported":"runtime expression"}""")]
    [InlineData("""{"all":[]}""")]
    [InlineData("""{"field":"hardware","op":"contains","value":"Example","extra":true}""")]
    public void Unknown_or_malformed_predicates_are_not_accepted(string condition)
        => Assert.Throws<InvalidDataException>(() => Catalogue(Definition("invalid", condition: condition)));

    [Fact]
    public void Missing_readings_and_unknown_values_are_distinct_from_missing_definitions()
    {
        var catalogue = Catalogue(Definition("example"));
        Assert.Equal(SensorDefinitionOutcome.NoReading, catalogue.ResolveDetailed(Reading(value: null), "example-os").Outcome);
        Assert.Equal(SensorDefinitionOutcome.InvalidReading, catalogue.ResolveDetailed(Reading(value: 2.1), "example-os").Outcome);
        var unknown = catalogue.ResolveDetailed(Reading(value: 99), "example-os");
        Assert.Equal(SensorDefinitionOutcome.UnknownValue, unknown.Outcome);
        Assert.Equal(AlertSeverity.Unknown, unknown.Severity);
        Assert.False(unknown.HasAuthoritativeSeverity);
        Assert.Equal(SensorDefinitionOutcome.MissingOperatingSystem, catalogue.ResolveDetailed(Reading(), null).Outcome);
    }

    [Fact]
    public void Fixed_oid_suffixes_do_not_match_neighbouring_columns_or_an_empty_index()
    {
        var definition = Definition("suffix", "exampleFan", "exact");
        definition["match"]!["oidSuffix"] = "6.1";
        var catalogue = Catalogue(definition);
        var sensor = Reading(); sensor.Oid = "1.3.6.1.4.1.32473.1.7.6.1";
        Assert.NotNull(catalogue.Resolve(sensor, "example-os"));
        sensor.Oid = "1.3.6.1.4.1.32473.1.7.6.10";
        Assert.Null(catalogue.Resolve(sensor, "example-os"));
        sensor.Oid = "1.3.6.1.4.1.32473.1.6.1";
        Assert.Null(catalogue.Resolve(sensor, "example-os"));
        sensor.Oid = "1.3.6.1.4.1.32473.10.7.6.1";
        Assert.Null(catalogue.Resolve(sensor, "example-os"));
    }
}
