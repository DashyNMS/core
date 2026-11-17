using System.Text.Json.Nodes;
using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// What a tool says about the limits of its own evidence reaches the model
/// unchanged, and stays in the history later questions resend
/// (DashyNMS/desktop#592). Payloads mirror the MCP server's projections.
/// </summary>
public sealed class IntelligenceToolEvidenceTests
{
    public static TheoryData<string, string> UncertainPayloads => new()
    {
        { "health_overview", """{"status":"ok","summary":"Sensors could not be listed for 2 devices.","data":{"readings":[],"unavailable":[{"source":"sensors","reason":"denied"}],"omitted":{"devices":2}}}""" },
        { "device_health", """{"status":"ok","summary":"Maintenance could not be read.","data":{"device":{"device_id":12,"status":"up"},"maintenance":"unknown","sensors":{"total":4,"unknown":1,"ignored":1}}}""" },
        { "network_events", """{"status":"ok","summary":"History was sampled.","data":{"events":[],"coverage":"sampled","history_count":null,"severity":"unknown"},"truncated":true}""" },
        { "graph", """{"status":"ok","summary":"Some graph listings couldn't be read, so the available types are incomplete.","data":{"types":["device_bits"],"unavailable":[{"listing":"health","reason":"failed"}]}}""" },
        { "what_changed", """{"status":"ok","summary":"No config baseline was found.","data":{"config":{"baseline":"missing","policy":"summary"},"logs":{"status":"unavailable","reason":"address read failed"}}}""" },
        { "active_alerts", """{"status":"ok","summary":"Rule names couldn't be read.","data":{"devices":[],"page":{"total":null},"rules":"denied"}}""" },
        { "device_details", """{"status":"ok","summary":"VLANs unavailable.","data":{"vlans":{"status":"unavailable"},"ports":{"items":[],"omitted":12,"next_offset":50}}}""" },
    };

    [Theory]
    [MemberData(nameof(UncertainPayloads))]
    public async Task Unavailable_partial_and_unknown_evidence_reaches_the_model_byte_for_byte(string tool, string payload)
    {
        var harness = new ConversationHarness(ConversationHarness.Calls(tool), ConversationHarness.Answers("Answer."));
        harness.Tools.Results.Enqueue(new IntelligenceToolResult(payload, IsError: false));

        await harness.AskAsync("How is it?");

        Assert.Equal(payload, ConversationHarness.OutputFor(harness.Requests[1])!.GetValue<string>());
    }

    [Fact]
    public async Task A_refused_lookup_stays_an_error_and_never_reads_as_an_empty_success()
    {
        const string Denied = """{"status":"error","message":"This LibreNMS account can't read sensors."}""";
        var harness = new ConversationHarness(ConversationHarness.Calls("device_sensors"), ConversationHarness.Answers("Answer."));
        harness.Tools.Results.Enqueue(new IntelligenceToolResult(Denied, IsError: true));

        await harness.AskAsync("Any hot sensors?");

        Assert.Equal(new JsonObject { ["error"] = Denied }.ToJsonString(), ConversationHarness.OutputFor(harness.Requests[1])!.GetValue<string>());
        Assert.True(Assert.Single(harness.Listener.Observations).IsError);
        Assert.Empty(harness.Listener.StatusCards);
    }

    [Fact]
    public void A_successful_zero_and_an_unreadable_count_stay_different_in_the_app()
    {
        var now = DateTimeOffset.UtcNow;
        var zero = IntelligenceStatusCards.FromTool("network_overview", """{"status":"ok","data":{"devices":{"up":5,"down":0},"alerts":{"critical":0}}}""", now);
        var unknown = IntelligenceStatusCards.FromTool("network_overview", """{"status":"ok","data":{"devices":{"up":5,"down":null},"alerts":{"critical":null,"unavailable":"denied"}}}""", now);

        Assert.Contains(zero.SelectMany(c => c.Rows), r => r.Name == "Devices down" && r.Detail == "0");
        Assert.Contains(zero.SelectMany(c => c.Rows), r => r.Name == "Critical alerts" && r.Detail == "0");
        Assert.DoesNotContain(unknown.SelectMany(c => c.Rows), r => r.Name is "Devices down" or "Critical alerts");
        Assert.Equal("Lookup reported an error. No successful result was returned.",
            IntelligenceToolObservation.FromResult("network_overview", new("""{"status":"error","message":"denied"}""", false), now).Summary);
    }

    [Fact]
    public void Unassessed_and_ignored_sensors_never_show_as_healthy()
    {
        var cards = IntelligenceStatusCards.FromTool("device_sensors",
            """{"status":"ok","data":{"device":{"device_id":12,"display":"switch"},"readings":[{"sensor_id":1,"state":"unknown"},{"sensor_id":2,"state":"ignored"},{"sensor_id":3,"state":"ok"}]}}""",
            DateTimeOffset.UtcNow);

        var rows = Assert.Single(cards).Rows;
        Assert.Equal(new[] { "unknown", "unknown", "up" }, rows.Select(r => r.Tone));
    }

    [Fact]
    public async Task An_oversized_result_is_shortened_with_a_marker_the_model_can_see()
    {
        var payload = """{"status":"ok","data":{"items":[""" + string.Join(",", Enumerable.Repeat("\"x\"", 20_000)) + "]}}";
        var harness = new ConversationHarness(ConversationHarness.Calls("find_device"), ConversationHarness.Answers("Answer."));
        harness.Tools.Results.Enqueue(new IntelligenceToolResult(payload, IsError: false));

        await harness.AskAsync("List everything");

        var sent = ConversationHarness.OutputFor(harness.Requests[1])!.GetValue<string>();
        Assert.EndsWith("\n[Output shortened.]", sent, StringComparison.Ordinal);
        Assert.StartsWith(payload[..IntelligenceConversation.MaxToolOutputChars], sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Graph_images_travel_beside_the_text_and_later_requests_carry_a_note_instead()
    {
        const string Payload = """{"status":"ok","summary":"Some graph listings couldn't be read, so the available types are incomplete.","data":{"type":"device_bits"}}""";
        var image = new IntelligenceImage([1, 2, 3], "image/png");
        var harness = new ConversationHarness(ConversationHarness.Calls("graph"), ConversationHarness.Answers("Here."), ConversationHarness.Answers("Again."));
        harness.Tools.Results.Enqueue(new IntelligenceToolResult(Payload, false, [image]));

        await harness.AskAsync("Traffic graph?");
        await harness.AskAsync("And now?");

        var parts = ConversationHarness.OutputFor(harness.Requests[1])!.AsArray();
        Assert.Equal(Payload, parts[0]!["text"]!.GetValue<string>());
        Assert.Equal(image.DataUrl, parts[1]!["image_url"]!.GetValue<string>());
        Assert.Same(image, Assert.Single(harness.Listener.Images));

        var resent = ConversationHarness.OutputFor(harness.Requests[2])!.AsArray();
        Assert.Equal(Payload, resent[0]!["text"]!.GetValue<string>());
        Assert.Equal("[A graph image was shown to the user here.]", resent[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_image_that_couldn_t_be_shown_is_never_presented_to_the_model_as_shown()
    {
        const string Payload = """{"status":"ok","summary":"Graph attached.","data":{}}""";
        var tooLarge = new IntelligenceImage(new byte[IntelligenceImage.MaxBytes + 1], "image/png");
        var notImage = new IntelligenceImage([1], "text/html");
        var harness = new ConversationHarness(ConversationHarness.Calls("graph"), ConversationHarness.Answers("Here."));
        harness.Tools.Results.Enqueue(new IntelligenceToolResult(Payload, false, [tooLarge, notImage]));

        await harness.AskAsync("Traffic graph?");

        var sent = ConversationHarness.OutputFor(harness.Requests[1])!.GetValue<string>();
        Assert.StartsWith(Payload, sent, StringComparison.Ordinal);
        Assert.EndsWith("[2 images from this lookup weren't shown to the user: they were empty, too large, not images or over the limit.]", sent, StringComparison.Ordinal);
        Assert.Empty(harness.Listener.Images);
    }

    [Fact]
    public async Task A_saved_and_restored_chat_resends_the_same_evidence()
    {
        var (tool, payload) = ((string)UncertainPayloads.First()[0], (string)UncertainPayloads.First()[1]);
        var harness = new ConversationHarness(ConversationHarness.Calls(tool), ConversationHarness.Answers("Answer."));
        harness.Tools.Results.Enqueue(new IntelligenceToolResult(payload, false));
        await harness.AskAsync("What changed?");

        var restored = new ConversationHarness(ConversationHarness.Answers("Follow-up."));
        restored.Conversation.RestoreHistory(JsonNode.Parse(harness.Conversation.ExportHistory().ToJsonString())!.AsArray());
        await restored.AskAsync("And since?");

        Assert.Equal(payload, ConversationHarness.OutputFor(restored.Requests.Single())!.GetValue<string>());
    }
}
