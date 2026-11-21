using System.Text.Json;
using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// The one set of tool rules every route shares (#97): an agent provider's
/// bridge calls the runner directly, so it's checked without a conversation.
/// </summary>
public sealed class IntelligenceToolRunnerTests
{
    private static readonly IReadOnlySet<string> Offered = IntelligenceToolAllowlist.Names;

    [Fact]
    public async Task RefusesToolsThatWereNotOfferedOrApproved()
    {
        var tools = new ConversationHarness.ScriptedTools();
        var listener = new ConversationHarness.RecordingListener();

        var unknown = await IntelligenceToolRunner.RunAsync(tools, Offered, "delete_device", "{}", listener, CancellationToken.None);
        var notOffered = await IntelligenceToolRunner.RunAsync(tools, new HashSet<string>(), "network_overview", "{}", listener, CancellationToken.None);
        var missing = await IntelligenceToolRunner.RunAsync(tools, Offered, null, "{}", listener, CancellationToken.None);

        Assert.All(new[] { unknown, notOffered, missing }, outcome => Assert.True(outcome.IsError));
        Assert.Contains("There is no tool called", unknown.Text);
        Assert.Empty(listener.Observations);
    }

    [Theory]
    [InlineData("not json", "The arguments weren't valid JSON.")]
    [InlineData("[1]", "The arguments must be a JSON object.")]
    public async Task RefusesArgumentsThatAreNotAJsonObject(string arguments, string message)
    {
        var outcome = await IntelligenceToolRunner.RunAsync(
            new ConversationHarness.ScriptedTools(), Offered, "network_overview", arguments, new ConversationHarness.RecordingListener(), CancellationToken.None);

        Assert.True(outcome.IsError);
        Assert.Equal(message, JsonDocument.Parse(outcome.Text).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task BoundsOutputAndRecordsADisplaySafeObservation()
    {
        var tools = new ConversationHarness.ScriptedTools();
        tools.Results.Enqueue(new IntelligenceToolResult(new string('x', IntelligenceToolRunner.MaxOutputChars + 10), IsError: false));
        var listener = new ConversationHarness.RecordingListener();

        var outcome = await IntelligenceToolRunner.RunAsync(tools, Offered, "network_overview", "{}", listener, CancellationToken.None);

        Assert.False(outcome.IsError);
        Assert.EndsWith("\n[Output shortened.]", outcome.Text);
        Assert.Equal(IntelligenceToolRunner.MaxOutputChars + "\n[Output shortened.]".Length, outcome.Text.Length);
        Assert.Equal("Lookup completed.", Assert.Single(listener.Observations).Summary);
    }

    [Fact]
    public async Task KeepsAToolErrorAsAnErrorForTheModel()
    {
        var tools = new ConversationHarness.ScriptedTools();
        tools.Results.Enqueue(new IntelligenceToolResult("LibreNMS refused the request.", IsError: true));

        var outcome = await IntelligenceToolRunner.RunAsync(
            tools, Offered, "device_details", """{"device_id":1}""", new ConversationHarness.RecordingListener(), CancellationToken.None);

        Assert.True(outcome.IsError);
        Assert.Equal("LibreNMS refused the request.", JsonDocument.Parse(outcome.Text).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HidesExceptionDetailFromTheModel()
    {
        var listener = new ConversationHarness.RecordingListener();

        var outcome = await IntelligenceToolRunner.RunAsync(
            new ThrowingTools(new InvalidOperationException("https://librenms.example.net/api/v0?token=secret")),
            Offered, "network_overview", "{}", listener, CancellationToken.None);

        Assert.True(outcome.IsError);
        Assert.DoesNotContain("secret", outcome.Text);
        Assert.True(Assert.Single(listener.Observations).IsError);
    }

    [Fact]
    public async Task RecordsAStoppedLookupThenRethrows()
    {
        var listener = new ConversationHarness.RecordingListener();

        await Assert.ThrowsAsync<OperationCanceledException>(() => IntelligenceToolRunner.RunAsync(
            new ThrowingTools(new OperationCanceledException()), Offered, "network_overview", "{}", listener, CancellationToken.None));

        Assert.Equal("Lookup stopped before completion.", Assert.Single(listener.Observations).Summary);
    }

    [Fact]
    public async Task ShowsAndReturnsOnlyImagesWithinTheLimits()
    {
        var png = new IntelligenceImage([1, 2, 3], "image/png");
        var tools = new ConversationHarness.ScriptedTools();
        tools.Results.Enqueue(new IntelligenceToolResult("graph", IsError: false,
            [png, new IntelligenceImage([], "image/png"), new IntelligenceImage([1], "text/plain"), png, png]));
        var listener = new ConversationHarness.RecordingListener();

        var outcome = await IntelligenceToolRunner.RunAsync(tools, Offered, "graph", "{}", listener, CancellationToken.None);

        Assert.Equal(IntelligenceToolRunner.MaxImagesPerCall, outcome.Images.Count);
        Assert.Equal(outcome.Images, listener.Images);
        Assert.Contains("3 images from this lookup weren't attached", outcome.Text);
    }

    private sealed class ThrowingTools(Exception exception) : IIntelligenceTools
    {
        public Task<IReadOnlyList<IntelligenceTool>> GetToolsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<IntelligenceTool>>(Array.Empty<IntelligenceTool>());

        public Task<IntelligenceToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
            => Task.FromException<IntelligenceToolResult>(exception);
    }
}
