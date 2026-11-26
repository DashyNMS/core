using System.Collections.Concurrent;
using System.Globalization;

namespace DesktopNMS.Core.Intelligence;

/// <summary>
/// The model's instructions (#324 in DashyNMS/desktop), kept as embedded text
/// in Instructions/*.md so they read as a document and can be reviewed as one.
/// Each answer gets the base block, its detail level's block, and the
/// personality, Spotlight and web search blocks only when they apply - every line is sent with every
/// round of every question, so nothing is sent that doesn't bear on it.
/// </summary>
/// <remarks>
/// Public so every route gives the model the same instructions (#97): an
/// agent provider in Desktop receives exactly what the ChatGPT route sends.
/// </remarks>
public static class IntelligenceInstructions
{
    private const string Prefix = "DesktopNMS.Core.Intelligence.Instructions.";

    private static readonly ConcurrentDictionary<string, string> Blocks = new(StringComparer.Ordinal);

    /// <summary>The most Spotlight objects described: as many as desktop lets a chat hold.</summary>
    internal const int MaxSpotlightObjects = 32;

    /// <param name="detail">The detail level (<see cref="IntelligenceDetail"/>); older reply styles are mapped.</param>
    /// <param name="spotlight">Objects already checked on this connection; described by kind and ID only, never by name.</param>
    /// <param name="webSearchEnabled">Whether the route offers web search for this question.</param>
    public static string Build(string? detail, IReadOnlyCollection<IntelligenceObjectReference>? spotlight, bool webSearchEnabled)
        => Build(detail, spotlight, webSearchEnabled, personality: null);

    /// <param name="personality">
    /// The user's description of how to sound, from <see cref="IntelligencePersonality.Effective"/>,
    /// or null. Cleaned again here and placed below the rules, labelled as the user's.
    /// </param>
    public static string Build(string? detail, IReadOnlyCollection<IntelligenceObjectReference>? spotlight, bool webSearchEnabled, string? personality)
        => Build(detail, spotlight, webSearchEnabled, personality, webSearchSupported: true);

    /// <summary>Builds instructions for a route whose web search capability may be limited by the provider.</summary>
    /// <param name="detail">The reply detail level.</param>
    /// <param name="spotlight">Objects already checked on this connection.</param>
    /// <param name="webSearchEnabled">Whether the user enabled web search for this question.</param>
    /// <param name="personality">The user's description of how to sound, if any.</param>
    /// <param name="webSearchSupported">Whether the provider offers web search through this route.</param>
    public static string Build(string? detail, IReadOnlyCollection<IntelligenceObjectReference>? spotlight,
        bool webSearchEnabled, string? personality, bool webSearchSupported)
    {
        var blocks = new List<string>
        {
            Block("base")
                .Replace("{max_calls}", IntelligenceConversation.MaxToolCalls.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{max_rounds}", IntelligenceConversation.MaxToolRounds.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            Block(IntelligenceDetail.Normalize(detail)),
        };

        if (Scope(spotlight) is { } scope)
        {
            blocks.Add(Block("spotlight").Replace("{scope}", scope, StringComparison.Ordinal));
        }

        var webSearchBlock = (webSearchSupported, webSearchEnabled) switch
        {
            (false, _) => "web-search-unavailable",
            (true, true) => "web-search-on",
            _ => "web-search-off",
        };
        blocks.Add(Block(webSearchBlock));

        // Last, so every rule comes before the user's text and the block can say so.
        if (IntelligencePersonality.Clean(personality) is { } voice)
        {
            blocks.Add(Block("personality").Replace("{personality}", voice, StringComparison.Ordinal));
        }

        return string.Join("\n\n", blocks);
    }

    /// <summary>
    /// The Spotlight as the instructions state it: fixed words and validated
    /// numeric IDs only. Instructions are the model's most trusted input, so
    /// network names - which anyone who can name a device controls - never go
    /// here; the model reads them from tool results, which it treats as data.
    /// </summary>
    internal static string? Scope(IReadOnlyCollection<IntelligenceObjectReference>? spotlight)
    {
        var described = (spotlight ?? [])
            .Where(o => o is { IsValid: true })
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSpotlightObjects)
            .ToList();
        return described.Count == 0 ? null : string.Join("; ", described);
    }

    private static string Describe(IntelligenceObjectReference o)
    {
        string Id(int? value) => value!.Value.ToString(CultureInfo.InvariantCulture);
        var on = o.DeviceId is { } device ? " on device_id=" + Id(device) : string.Empty;
        var rule = o.RuleId is > 0 ? " (rule_id=" + Id(o.RuleId) + ")" : string.Empty;
        return o.Kind switch
        {
            "group" => "device group group_id=" + Id(o.Id),
            "location" => "location location_id=" + Id(o.Id),
            "sensor" => "sensor sensor_id=" + Id(o.Id) + on,
            "port" => "interface port_id=" + Id(o.Id) + on,
            "alert" => "alert alert_id=" + Id(o.Id) + on + rule,
            "fault" => "fault alert_id=" + Id(o.Id) + on + rule,
            _ => "device device_id=" + Id(o.Id),
        };
    }

    private static string Block(string name) => Blocks.GetOrAdd(name, static name =>
    {
        using var stream = typeof(IntelligenceInstructions).Assembly.GetManifestResourceStream(Prefix + name + ".md")
                           ?? throw new InvalidOperationException($"The {name} instructions are missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    });
}
