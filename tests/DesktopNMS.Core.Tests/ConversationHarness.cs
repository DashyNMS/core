using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// A conversation against scripted model turns and scripted tool results,
/// keeping every request body so tests can assert the model's exact input.
/// </summary>
internal sealed class ConversationHarness
{
    private readonly Handler _handler;

    public ConversationHarness(params string[] turns)
    {
        _handler = new Handler(turns);
        Conversation = new IntelligenceConversation(new ResponsesClient(new HttpClient(_handler)), Tools);
    }

    public IntelligenceConversation Conversation { get; }
    public ScriptedTools Tools { get; } = new();
    public RecordingListener Listener { get; } = new();
    public IReadOnlyList<JsonObject> Requests => _handler.Bodies;

    public Task AskAsync(string question, IReadOnlyCollection<IntelligenceObjectReference>? spotlight = null)
        => Conversation.AskAsync(question, "test-model", _ => Task.FromResult("test-token"), Listener, CancellationToken.None, spotlight);

    /// <summary>One streamed model turn.</summary>
    public static string Turn(params string[] events) => string.Join("", events.Select(e => "data: " + e + "\n\n"));

    /// <summary>A model turn that calls one tool and stops.</summary>
    public static string Calls(string tool, string callId = "call_1") => Turn(
        $$$"""{"type":"response.output_item.done","item":{"type":"function_call","call_id":"{{{callId}}}","name":"dashynms.{{{tool}}}","arguments":"{}"}}""",
        """{"type":"response.completed"}""");

    /// <summary>A model turn that answers in text.</summary>
    public static string Answers(string text) => Turn(
        $$$"""{"type":"response.output_item.done","item":{"type":"message","role":"assistant","content":[{"type":"output_text","text":{{{JsonSerializer.Serialize(text)}}}}]}}""",
        """{"type":"response.completed"}""");

    /// <summary>The output the model was sent for a call, as it appears in a later request.</summary>
    public static JsonNode? OutputFor(JsonObject request, string callId = "call_1")
        => request["input"]!.AsArray().OfType<JsonObject>()
            .Single(i => i["type"]?.GetValue<string>() == "function_call_output" && i["call_id"]?.GetValue<string>() == callId)["output"];

    private sealed class Handler(string[] turns) : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(turns[Bodies.Count - 1], Encoding.UTF8, "text/event-stream") };
        }
    }

    internal sealed class ScriptedTools : IIntelligenceTools
    {
        public Queue<IntelligenceToolResult> Results { get; } = new();

        public Task<IReadOnlyList<IntelligenceTool>> GetToolsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<IntelligenceTool>>(IntelligenceToolAllowlist.Names
                .Select(name => new IntelligenceTool(name, name, JsonSerializer.SerializeToElement(new { type = "object" }))).ToList());

        public Task<IntelligenceToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
            => Task.FromResult(Results.Dequeue());
    }

    internal sealed class RecordingListener : IIntelligenceListener
    {
        public List<IntelligenceImage> Images { get; } = new();
        public List<IntelligenceStatusCard> StatusCards { get; } = new();
        public List<IntelligenceToolObservation> Observations { get; } = new();
        public void OnText(string delta) { }
        public void OnToolStarted(string name, string arguments) { }
        public void OnToolFinished(string name, bool isError) { }
        public void OnImage(IntelligenceImage image) => Images.Add(image);
        public void OnStatusCard(IntelligenceStatusCard card) => StatusCards.Add(card);
        public void OnToolObserved(IntelligenceToolObservation observation) => Observations.Add(observation);
    }
}
