using System.Text.Json;

namespace DesktopNMS.Core.Intelligence;

/// <summary>
/// What one tool call gives back to the model: bounded text, whether it
/// failed, and the graphs that passed the image limits. The caller wraps it
/// in its own protocol - a Responses function output, or an MCP result.
/// </summary>
public sealed record IntelligenceToolOutcome(string Text, bool IsError, IReadOnlyList<IntelligenceImage> Images);

/// <summary>
/// Runs one tool call the way every Intelligence route must (#97): only a
/// tool that was offered and is on <see cref="IntelligenceToolAllowlist"/>,
/// arguments that are a JSON object, a display-safe observation and status
/// cards for the panel, and output and images bounded before the model sees
/// them. Shared, so ChatGPT's conversation and an agent provider's tool
/// bridge can't drift apart on any of it.
/// </summary>
public static class IntelligenceToolRunner
{
    /// <summary>The most characters one tool output adds to the history.</summary>
    public const int MaxOutputChars = 24_000;

    /// <summary>The most images one tool call shows and sends.</summary>
    public const int MaxImagesPerCall = 2;

    /// <param name="offered">The tools listed to the model for this question; anything else is refused whatever the model asks for.</param>
    /// <param name="arguments">The arguments exactly as the model sent them.</param>
    /// <exception cref="OperationCanceledException">The question was stopped; the observation says so first.</exception>
    public static async Task<IntelligenceToolOutcome> RunAsync(
        IIntelligenceTools tools,
        IReadOnlySet<string> offered,
        string? name,
        string arguments,
        IIntelligenceListener listener,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(listener);

        // Only a tool that was offered and is approved - whatever the model asks for.
        if (name is null || !offered.Contains(name) || !IntelligenceToolAllowlist.Names.Contains(name))
        {
            return Error($"There is no tool called \"{name}\".");
        }

        JsonElement parsed;
        try
        {
            using var document = JsonDocument.Parse(arguments);
            parsed = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error("The arguments weren't valid JSON.");
        }

        if (parsed.ValueKind != JsonValueKind.Object)
        {
            return Error("The arguments must be a JSON object.");
        }

        listener.OnToolStarted(name, arguments);
        IntelligenceToolResult result;
        try
        {
            result = await tools.CallAsync(name, parsed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            listener.OnToolObserved(new(name, DateTimeOffset.UtcNow, "Lookup stopped before completion.", true));
            listener.OnToolFinished(name, isError: true);
            throw;
        }
        catch (Exception)
        {
            listener.OnToolObserved(new(name, DateTimeOffset.UtcNow, "Lookup failed. No successful result was returned.", true));
            listener.OnToolFinished(name, isError: true);

            // Exception messages can contain request URLs, credentials or raw server data.
            return Error("The network tool failed. Try again or check the connection settings.");
        }

        var observation = IntelligenceToolObservation.FromResult(name, result, DateTimeOffset.UtcNow);
        listener.OnToolObserved(observation);
        listener.OnToolFinished(name, observation.IsError);
        var text = result.Output.Length > MaxOutputChars
            ? result.Output[..MaxOutputChars] + "\n[Output shortened.]"
            : result.Output;
        if (result.IsError)
        {
            return Error(text);
        }

        foreach (var card in IntelligenceStatusCards.FromTool(name, result.Output, DateTimeOffset.UtcNow))
            listener.OnStatusCard(card);

        var offeredImages = result.Images ?? Array.Empty<IntelligenceImage>();
        var images = offeredImages
            .Where(i => i.Data.Length is > 0 and <= IntelligenceImage.MaxBytes && i.MimeType.StartsWith("image/", StringComparison.Ordinal))
            .Take(MaxImagesPerCall)
            .ToList();

        // The tool's own text may say a graph is attached: say which never reached the user.
        if (offeredImages.Count > images.Count)
        {
            var withheld = offeredImages.Count - images.Count;
            text += withheld == 1
                ? $"\n[1 image from this lookup wasn't attached for you or shown to the user: it was empty, too large, not an image or over the limit of {MaxImagesPerCall}.]"
                : $"\n[{withheld} images from this lookup weren't attached for you or shown to the user: they were empty, too large, not images or over the limit of {MaxImagesPerCall}.]";
        }

        foreach (var image in images)
        {
            listener.OnImage(image);
        }

        return new IntelligenceToolOutcome(text, IsError: false, images);
    }

    /// <summary>The error JSON the model has always been given, so its instructions about failed lookups still apply.</summary>
    private static IntelligenceToolOutcome Error(string message)
        => new(new System.Text.Json.Nodes.JsonObject { ["error"] = message }.ToJsonString(), IsError: true, Array.Empty<IntelligenceImage>());
}
