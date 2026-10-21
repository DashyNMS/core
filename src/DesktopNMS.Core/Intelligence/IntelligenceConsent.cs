using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Core.Intelligence;

/// <summary>
/// The "what's sent" notice shown before Intelligence is first used. When
/// what it says changes, its version goes up and it's shown again, so nobody
/// is held to a notice they never saw (#323 in DashyNMS/desktop).
/// </summary>
public static class IntelligenceConsent
{
    /// <summary>1: what's sent and config backups. 2: adds web search and its search providers.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The current notice has been accepted.</summary>
    public static bool IsCurrent(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.IntelligenceConsentVersion >= CurrentVersion;
    }

    /// <summary>Web search runs only when it's on and the notice that describes it was accepted.</summary>
    public static bool AllowsWebSearch(AppSettings settings)
        => IsCurrent(settings) && settings.IntelligenceWebSearchEnabled;

    public static void Accept(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.IntelligenceConsentGiven = true;
        settings.IntelligenceConsentVersion = CurrentVersion;
    }
}