namespace DesktopNMS.Core.Help;

/// <summary>A heading within a page, for in-page navigation.</summary>
/// <param name="Level">1 to 6. The page's own title isn't listed.</param>
/// <param name="Anchor">What a link's "#…" names it by, as GitHub makes it.</param>
public sealed record HelpHeading(int Level, string Text, string Anchor);

/// <summary>A part of a page: a heading and what's under it, up to the next. The first part, before any heading, has none.</summary>
/// <param name="Markdown">Its Markdown, without the heading line.</param>
/// <param name="Text">The same as plain text, for search and passages.</param>
public sealed record HelpSection(HelpHeading? Heading, string Markdown, string Text);

/// <summary>
/// One page of a guide. Each app renders <see cref="Markdown"/> itself, and
/// asks <see cref="HelpGuide"/> where each link and image in it goes.
/// </summary>
/// <param name="Id">Its name on the wiki, such as "Install-on-macOS". Stable for links from the app.</param>
/// <param name="Path">Its path in the repository, which relative links start from.</param>
/// <param name="Title">What to show as its heading.</param>
/// <param name="Markdown">Its text without the title line, raw HTML or comments.</param>
public sealed record HelpPage(string Id, string Path, string Title, string Markdown, IReadOnlyList<HelpHeading> Headings, IReadOnlyList<HelpSection> Sections)
{
    /// <summary>The heading with this anchor, or null.</summary>
    public HelpHeading? FindHeading(string? anchor)
        => anchor is null ? null : Headings.FirstOrDefault(heading => string.Equals(heading.Anchor, anchor, StringComparison.OrdinalIgnoreCase));

    /// <summary>The part under a heading, deeper headings included, or null when there's no such heading.</summary>
    public string? MarkdownUnder(string anchor)
    {
        if (FindHeading(anchor) is not { } heading)
        {
            return null;
        }

        var start = Sections.ToList().FindIndex(section => section.Heading == heading);
        var parts = new List<string>();
        for (var index = start; index < Sections.Count; index++)
        {
            var section = Sections[index];
            if (index > start && section.Heading!.Level <= heading.Level)
            {
                break;
            }

            parts.Add(index == start ? section.Markdown : new string('#', section.Heading!.Level) + " " + section.Heading.Text + "\n\n" + section.Markdown);
        }

        return string.Join("\n\n", parts).Trim();
    }
}

/// <summary>A page in the contents list.</summary>
public sealed record HelpContentsEntry(string PageId, string Title);

/// <summary>A group in the contents list, such as "Everyday tasks". The front page's group has no name.</summary>
public sealed record HelpContentsGroup(string? Name, IReadOnlyList<HelpContentsEntry> Entries);
