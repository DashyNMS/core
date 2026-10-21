using System.Collections.Concurrent;
using System.Globalization;

namespace DesktopNMS.Core.Intelligence;

/// <summary>
/// The model's instructions (#324 in DashyNMS/desktop), kept as embedded text
/// in Instructions/*.md so they read as a document and can be reviewed as one.
/// Each answer gets the base block, its reply style's block, and the Spotlight
/// and web search blocks only when they apply - every line is sent with every
/// round of every question, so nothing is sent that doesn't bear on it.
/// </summary>
internal static class IntelligenceInstructions
{
    private const string Prefix = "DesktopNMS.Core.Intelligence.Instructions.";

    private static readonly ConcurrentDictionary<string, string> Blocks = new(StringComparer.Ordinal);

    public static string Build(string? replyStyle, string? spotlight, bool webSearchEnabled)
    {
        var blocks = new List<string>
        {
            Block("base")
                .Replace("{max_calls}", IntelligenceConversation.MaxToolCalls.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{max_rounds}", IntelligenceConversation.MaxToolRounds.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            Block(IntelligenceReplyStyles.Normalize(replyStyle) == IntelligenceReplyStyles.EvidenceBased ? "evidence-based" : "conversational"),
        };
        if (spotlight is not null)
        {
            blocks.Add(Block("spotlight").Replace("{scope}", spotlight, StringComparison.Ordinal));
        }

        blocks.Add(Block(webSearchEnabled ? "web-search-on" : "web-search-off"));
        return string.Join("\n\n", blocks);
    }

    private static string Block(string name) => Blocks.GetOrAdd(name, static name =>
    {
        using var stream = typeof(IntelligenceInstructions).Assembly.GetManifestResourceStream(Prefix + name + ".md")
                           ?? throw new InvalidOperationException($"The {name} instructions are missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    });
}
