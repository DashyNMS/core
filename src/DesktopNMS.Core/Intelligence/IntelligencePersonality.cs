using System.Text;
using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Core.Intelligence;

/// <summary>
/// The user's description of how Dashy should sound (#608 in DashyNMS/desktop):
/// tone and voice only. It reaches the model inside a labelled block below
/// DashyNMS's own rules, so it is cleaned and limited before it goes - it
/// can't break out of that block or crowd out the rules.
/// </summary>
public static class IntelligencePersonality
{
    /// <summary>The most characters kept: enough for a voice, not for a second set of instructions.</summary>
    public const int MaxLength = 600;

    /// <summary>The personality to send, or null when it's off or empty.</summary>
    public static string? Effective(AppSettings settings)
        => settings.IntelligencePersonalityEnabled ? Clean(settings.IntelligencePersonality) : null;

    /// <summary>
    /// The text as the model sees it: no control characters or angle
    /// brackets - so nothing can close its block - at most one blank line in
    /// a row, trimmed and cut to <see cref="MaxLength"/>. Null when nothing's left.
    /// </summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = new StringBuilder(Math.Min(text.Length, MaxLength));
        var newlines = 0;
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (c == '\n')
            {
                if (++newlines <= 2) cleaned.Append('\n');
                continue;
            }

            if (char.IsControl(c) || c is '<' or '>' or '\u2028' or '\u2029') continue;
            newlines = 0;
            cleaned.Append(c);
        }

        var result = cleaned.ToString().Trim();
        if (result.Length > MaxLength) result = result[..MaxLength].TrimEnd();
        return result.Length == 0 ? null : result;
    }
}
