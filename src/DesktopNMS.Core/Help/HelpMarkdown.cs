using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Help;

/// <summary>The Markdown a guide uses, read just enough to find its titles, headings, links and words.</summary>
internal static partial class HelpMarkdown
{
    /// <summary>Reads a page: its title comes from its first level-one heading, or <paramref name="title"/> when there's none, as on a wiki.</summary>
    public static (string? Title, string Markdown, IReadOnlyList<HelpHeading> Headings, IReadOnlyList<HelpSection> Sections) Read(string text)
    {
        string? title = null;
        var kept = new List<string>();
        var headings = new List<HelpHeading>();
        var sections = new List<HelpSection>();
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        HelpHeading? current = null;
        var body = new List<string>();

        foreach (var (line, isCode) in Lines(WithoutComments(text)))
        {
            if (!isCode && HeadingLine().Match(line) is { Success: true } match)
            {
                var level = match.Groups[1].Length;
                var shown = Plain(match.Groups[2].Value);
                var anchor = Anchor(shown, anchors);
                if (level == 1 && title is null && headings.Count == 0)
                {
                    title = shown;
                    continue;
                }

                EndSection();
                current = new HelpHeading(level, shown, anchor);
                headings.Add(current);
                kept.Add(line);
                continue;
            }

            if (!isCode && TagLine().IsMatch(line))
            {
                // The logo and centring tags on a front page. Apps draw their own.
                continue;
            }

            var converted = isCode ? line : WithWikiLinks(line);
            kept.Add(converted);
            body.Add(converted);
        }

        EndSection();
        return (title, string.Join('\n', kept).Trim(), headings, sections);

        void EndSection()
        {
            var markdown = string.Join('\n', body).Trim();
            body.Clear();
            if (current is not null || markdown.Length > 0)
            {
                sections.Add(new HelpSection(current, markdown, PlainText(markdown)));
            }
        }
    }

    /// <summary>Each line, and whether it's part of a fenced code block.</summary>
    public static IEnumerable<(string Line, bool IsCode)> Lines(string text)
    {
        char? fence = null;
        var length = 0;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = FenceLine().Match(line);
            if (fence is null)
            {
                if (match.Success)
                {
                    fence = match.Groups[1].Value[0];
                    length = match.Groups[1].Length;
                    yield return (line, true);
                    continue;
                }

                yield return (line, false);
                continue;
            }

            yield return (line, true);
            if (match.Success && match.Groups[1].Value[0] == fence && match.Groups[1].Length >= length && match.Groups[2].Value.Trim().Length == 0)
            {
                fence = null;
            }
        }
    }

    /// <summary>Links and images in a line, outside inline code.</summary>
    public static IEnumerable<(bool IsImage, string Text, string Target)> Links(string line)
    {
        foreach (Match match in LinkPattern().Matches(InlineCode().Replace(line, code => new string(' ', code.Length))))
        {
            yield return (match.Groups[1].Length > 0, Unescape(match.Groups[2].Value), match.Groups[3].Value);
        }
    }

    /// <summary>A wiki's [[Page]] and [[Text|Page]] as ordinary links, so an app's renderer needs only one kind.</summary>
    public static string WithWikiLinks(string line)
        => line.Contains("[[", StringComparison.Ordinal)
            ? WikiLink().Replace(line, match =>
            {
                var text = match.Groups[1].Value.Trim();
                var page = match.Groups[2].Success ? match.Groups[2].Value.Trim() : text;
                var (name, fragment) = SplitFragment(page);
                return $"[{text}]({PageId(name)}{(fragment is null ? string.Empty : "#" + fragment)})";
            })
            : line;

    /// <summary>The name a wiki gives a page with this title: "Install on macOS" is Install-on-macOS.</summary>
    public static string PageId(string title)
        => Spaces().Replace(NotInPageId().Replace(title, string.Empty).Trim(), "-");

    public static (string Path, string? Fragment) SplitFragment(string target)
    {
        var hash = target.IndexOf('#');
        return hash < 0 ? (target, null) : (target[..hash], target[(hash + 1)..]);
    }

    /// <summary>One line's Markdown as the words a reader sees. What's in code marks is kept as written.</summary>
    public static string Plain(string markdown)
    {
        // Put aside first, so the stars in `MCP_DBM_*` aren't read as emphasis, then put back.
        var codes = new List<string>();
        var text = InlineCode().Replace(markdown, match =>
        {
            codes.Add(match.Groups[2].Value.Trim());
            return "\uE000" + (char)('\uE100' + codes.Count - 1);
        });
        text = LinkPattern().Replace(text, "$2");
        text = Emphasis().Replace(text, "$2");
        text = WebUtility.HtmlDecode(Unescape(Tags().Replace(text, string.Empty)));
        for (var index = 0; index < codes.Count; index++)
        {
            text = text.Replace("\uE000" + (char)('\uE100' + index), codes[index], StringComparison.Ordinal);
        }

        return text.Trim();
    }

    /// <summary>A block of Markdown as plain text: table pipes, list marks and quotes go, code stays.</summary>
    public static string PlainText(string markdown)
    {
        var result = new StringBuilder();
        foreach (var (line, isCode) in Lines(markdown))
        {
            if (isCode)
            {
                if (!FenceLine().IsMatch(line))
                {
                    Append(line.Trim());
                }

                continue;
            }

            if (TableRule().IsMatch(line))
            {
                continue;
            }

            var text = BlockMarks().Replace(line, string.Empty).Trim();
            if (text.StartsWith('|'))
            {
                text = string.Join(" – ", text.Trim('|').Split('|').Select(cell => cell.Trim()).Where(cell => cell.Length > 0));
            }

            Append(Plain(text));
        }

        return result.ToString().Trim();

        void Append(string line)
        {
            if (line.Length > 0)
            {
                result.Append(line).Append('\n');
            }
        }
    }

    /// <summary>GitHub's anchor for a heading: lower case, punctuation dropped, spaces as hyphens, and "-1" after a repeat.</summary>
    private static string Anchor(string text, Dictionary<string, int> used)
    {
        var slug = new StringBuilder();
        foreach (var character in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '-')
            {
                slug.Append(character);
            }
            else if (char.IsWhiteSpace(character))
            {
                slug.Append('-');
            }
        }

        var anchor = slug.ToString();
        if (!used.TryGetValue(anchor, out var count))
        {
            used[anchor] = 0;
            return anchor;
        }

        used[anchor] = ++count;
        return anchor + "-" + count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string WithoutComments(string text)
    {
        if (!text.Contains("<!--", StringComparison.Ordinal))
        {
            return text;
        }

        // Line by line rather than across the page, so a comment shown in a code block stays.
        var kept = new List<string>();
        var inside = false;
        foreach (var (line, isCode) in Lines(text))
        {
            if (isCode && !inside)
            {
                kept.Add(line);
                continue;
            }

            var rest = line;
            var shown = new StringBuilder();
            while (rest.Length > 0)
            {
                var mark = rest.IndexOf(inside ? "-->" : "<!--", StringComparison.Ordinal);
                if (mark < 0)
                {
                    if (!inside)
                    {
                        shown.Append(rest);
                    }

                    break;
                }

                if (!inside)
                {
                    shown.Append(rest, 0, mark);
                }

                rest = rest[(mark + (inside ? 3 : 4))..];
                inside = !inside;
            }

            if (shown.Length > 0 || line.Length == 0)
            {
                kept.Add(shown.ToString());
            }
        }

        return string.Join('\n', kept);
    }

    private static string Unescape(string text) => Escape().Replace(text, "$1");

    [GeneratedRegex(@"^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*(`{3,}|~{3,})(.*)$")]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"(!?)\[((?:\\.|[^\]\\])*)\]\(\s*<?([^)\s>]+)>?(?:\s+""[^""]*"")?\s*\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\[\[([^\]|]+)(?:\|([^\]]+))?\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"(?<!`)(`+)(?!`)(.*?[^`])\1(?!`)")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"(\*\*|__|\*|(?<!\w)_)(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"</?[a-zA-Z][a-zA-Z0-9]*(?:\s[^>]*)?/?>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"^\s*(?:</?[a-zA-Z][a-zA-Z0-9]*(?:\s[^>]*)?/?>\s*)+$")]
    private static partial Regex TagLine();

    [GeneratedRegex(@"^\s*\|?(?:\s*:?-{2,}:?\s*\|?)+\s*$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"^\s*(?:#{1,6}\s+|(?:>\s*)+|[-*+]\s+|\d+[.)]\s+)")]
    private static partial Regex BlockMarks();

    [GeneratedRegex(@"[^\w\- ]")]
    private static partial Regex NotInPageId();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|<>~])")]
    private static partial Regex Escape();
}
