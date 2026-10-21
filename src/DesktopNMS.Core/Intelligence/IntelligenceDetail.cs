namespace DesktopNMS.Core.Intelligence;

/// <summary>
/// How much an Intelligence answer says (#329 in DashyNMS/desktop). One dial
/// rather than two reply styles: every level cites what it looked at, keeps
/// observed apart from inferred and states uncertainty that matters - only
/// the length and layout change.
/// </summary>
public static class IntelligenceDetail
{
    public const string Brief = "brief";
    public const string Balanced = "balanced";
    public const string Detailed = "detailed";
    public const string Full = "full";

    public const string Default = Balanced;

    /// <summary>The levels, least to most.</summary>
    public static IReadOnlyList<string> Levels { get; } = new[] { Brief, Balanced, Detailed, Full };

    /// <summary>
    /// A known level, or the default. The two reply styles before #329 map
    /// over, so settings and saved chats keep what they had: conversational is
    /// Balanced, evidence-based is Detailed.
    /// </summary>
    public static string Normalize(string? level) => level switch
    {
        Brief or Balanced or Detailed or Full => level,
        "evidence-based" => Detailed,
        _ => Default,
    };

    /// <summary>Brief and Balanced read as a message; Detailed and Full as a report.</summary>
    public static bool IsConversational(string? level) => Normalize(level) is Brief or Balanced;
}
