using System.Globalization;
using System.Text;
using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Core.Intelligence;

/// <summary>
/// The user's description of how Dashy should sound (#608 in DashyNMS/desktop):
/// tone and voice only. It reaches the model inside a labelled block after
/// DashyNMS's own rules, so it is cleaned and limited before it goes - it
/// can't break out of that block, hide text from the person who wrote it, or
/// crowd out the rules.
/// </summary>
public static class IntelligencePersonality
{
    /// <summary>The most characters kept: enough for a voice, not for a second set of instructions.</summary>
    public const int MaxLength = 600;

    /// <summary>The personality to send, or null when it's off or empty.</summary>
    public static string? Effective(AppSettings settings)
        => settings.IntelligencePersonalityEnabled ? Clean(settings.IntelligencePersonality) : null;

    /// <summary>
    /// The text as the model sees it. Compatibility forms are folded first, so
    /// a fullwidth bracket can't stand in for the real one; then angle
    /// brackets go, so nothing can close the block, along with control,
    /// format, private-use and unassigned characters, so nothing invisible is
    /// sent. Tabs become spaces and every line ending a new line; runs of blank
    /// lines fold to one. Trimmed and cut to <see cref="MaxLength"/> without
    /// splitting a character. Null when nothing's left.
    /// </summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalised = text.Normalize(NormalizationForm.FormKC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u0085', '\n')
            .Replace('\u2028', '\n')
            .Replace('\u2029', '\n')
            .Replace('\t', ' ');

        var lines = new List<string>();
        var blank = false;
        foreach (var raw in normalised.Split('\n'))
        {
            var line = Keep(raw).TrimEnd();
            if (line.Trim().Length == 0)
            {
                if (!blank && lines.Count > 0) lines.Add(string.Empty);
                blank = true;
                continue;
            }

            blank = false;
            lines.Add(line);
        }

        var result = string.Join('\n', lines).Trim();
        if (result.Length > MaxLength)
        {
            var cut = char.IsHighSurrogate(result[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
            result = result[..cut].TrimEnd();
        }

        return result.Length == 0 ? null : result;
    }

    /// <summary>The characters of one line worth keeping, whole surrogate pairs included.</summary>
    private static string Keep(string line)
    {
        var kept = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
            {
                if (Visible(CharUnicodeInfo.GetUnicodeCategory(line, i))) kept.Append(c).Append(line[i + 1]);
                i++;
                continue;
            }

            if (c is '<' or '>' || !Visible(char.GetUnicodeCategory(c))) continue;
            kept.Append(c);
        }

        return kept.ToString();
    }

    private static bool Visible(UnicodeCategory category) => category is not (UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
}
