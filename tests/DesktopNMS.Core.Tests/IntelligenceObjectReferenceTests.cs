using System.Text.Json;
using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class IntelligenceObjectReferenceTests
{
    [Fact]
    public void Sensor_references_keep_the_sensor_and_parent_device_from_tool_evidence()
    {
        var card = Assert.Single(IntelligenceStatusCards.FromTool("device_sensors", """
            {"status":"ok","data":{"device":{"device_id":12,"hostname":"switch.example.net"},
            "readings":[{"sensor_id":73,"description":"Chassis temperature","state":"warning"}]}}
            """, DateTimeOffset.UtcNow));
        var reference = Assert.Single(IntelligenceInlineReferences.FromSnapshot(card));
        Assert.Equal(new IntelligenceObjectReference("sensor", 73, 12), reference.Object);
        Assert.Same(reference, IntelligenceInlineMarkup.Resolve("[[sensor:73]]", new[] { reference }));
        Assert.Null(IntelligenceInlineMarkup.Resolve("[[sensor:74]]", new[] { reference }));
        Assert.Contains("Chassis temperature", IntelligenceInlineMarkup.ToReadableText("[[sensor:73]]", new[] { reference }));
    }

    [Fact]
    public void Only_scope_records_with_explicit_positive_ids_become_actionable()
    {
        var card = Assert.Single(IntelligenceStatusCards.FromTool("list_groups_and_locations", """
            {"status":"ok","data":{"groups":[{"group_id":3,"name":"Core"},{"name":"No ID"}],
            "locations":[{"location_id":4,"name":"Site A"},{"location_id":-1,"name":"Invalid"}]}}
            """, DateTimeOffset.UtcNow));
        var refs = IntelligenceInlineReferences.FromSnapshot(card).ToArray();
        Assert.Equal(new[] { "group:3", "location:4" }, refs.Select(r => r.Key));
        Assert.All(refs, r => Assert.True(r.Object!.IsValid));
        Assert.Empty(IntelligenceStatusCards.FromTool("device_sensors", """{"status":"error","data":{"readings":[{"sensor_id":73}]}}""", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Old_spotlights_still_load_and_new_spotlights_round_trip_typed_identity()
    {
        var old = JsonSerializer.Deserialize<SavedChatSpotlight>("""{"Kind":0,"Id":12,"Name":"Switch"}""")!;
        Assert.Null(old.Object);
        var saved = new SavedChatSpotlight(3, 73, "Temperature") { Object = new("sensor", 73, 12) };
        Assert.Equal(saved, JsonSerializer.Deserialize<SavedChatSpotlight>(JsonSerializer.Serialize(saved)));
        Assert.False(new IntelligenceObjectReference("sensor", 73).IsValid);
        Assert.False(new IntelligenceObjectReference("unknown", 73).IsValid);
    }
}
