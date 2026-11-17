using System.Collections.Concurrent;
using System.Globalization;

namespace DesktopNMS.Core.Intelligence;

/// <summary>
/// The model's instructions (#324 in DashyNMS/desktop), kept as embedded text
/// in Instructions/*.md so they read as a document and can be reviewed as one.
/// Each answer gets the base block, its detail level's block, and the Spotlight
/// and web search blocks only when they apply - every line is sent with every
/// round of every question, so nothing is sent that doesn't bear on it.
/// </summary>
internal static class IntelligenceInstructions
{
    private const string Prefix = "DesktopNMS.Core.Intelligence.Instructions.";

    private static readonly ConcurrentDictionary<string, string> Blocks = new(StringComparer.Ordinal);

    /// <summary>The most Spotlight objects described; the apps allow fewer.</summary>
    internal const int MaxSpotlightObjects = 20;

    public static string Build(string? detail, IReadOnlyCollection<IntelligenceObjectReference>? spotlight, bool webSearchEnabled)
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

        blocks.Add(Block(webSearchEnabled ? "web-search-on" : "web-search-off"));
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
            .Distinct()
            .Take(MaxSpotlightObjects)
            .Select(Describe)
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
