using System.Text;
using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Help;

/// <summary>
/// A product's user guide for one release (#96): its pages, the contents
/// list, where links and images go, and search. Immutable, and safe to read
/// from any thread.
/// </summary>
/// <remarks>
/// It reads both forms the guide takes: the repository's <c>docs/guide</c>,
/// which is bundled and refreshed, and the wiki copied from it, with
/// <c>Home</c> and <c>_Sidebar</c> pages. Either way a page's
/// <see cref="HelpPage.Id"/> is its wiki name, so a link from an app or a
/// wiki address means the same page.
/// </remarks>
public sealed partial class HelpGuide
{
    /// <summary>The front page's id, as on the wiki.</summary>
    public const string HomeId = "Home";

    private readonly Dictionary<string, HelpPage> _byId;
    private readonly Dictionary<string, HelpPage> _byPath;
    private readonly HelpFileSet _files;
    private readonly HelpSearchIndex _search;

    private HelpGuide(HelpProduct product, HelpRelease? release, HelpFileSet files, IReadOnlyList<HelpPage> pages, IReadOnlyList<HelpContentsGroup> contents)
    {
        Product = product;
        Release = release;
        _files = files;
        Pages = pages;
        Contents = contents;
        _byId = new Dictionary<string, HelpPage>(StringComparer.OrdinalIgnoreCase);
        _byPath = new Dictionary<string, HelpPage>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            _byId.TryAdd(page.Id, page);
            _byPath.TryAdd(page.Path, page);
        }

        _search = new HelpSearchIndex(pages);
    }

    public HelpProduct Product { get; }

    /// <summary>The release it describes, or null when that isn't known, such as a local build.</summary>
    public HelpRelease? Release { get; }

    /// <summary>Every page, in the contents list's order.</summary>
    public IReadOnlyList<HelpPage> Pages { get; }

    /// <summary>The contents list: the wiki's sidebar, or the front page's own list of links.</summary>
    public IReadOnlyList<HelpContentsGroup> Contents { get; }

    /// <summary>The front page, or null for an empty guide.</summary>
    public HelpPage? Home => Find(HomeId);

    /// <summary>Reads a guide from its files. A set with no guide in it gives one with no pages.</summary>
    public static HelpGuide Load(HelpProduct product, HelpRelease? release, HelpFileSet files)
    {
        var isWiki = !files.Contains(HelpProduct.GuideFolder + "/README.md") && files.Contains("Home.md");
        var root = isWiki ? string.Empty : HelpProduct.GuideFolder + "/";
        var pages = new Dictionary<string, HelpPage>(StringComparer.Ordinal);
        string? sidebar = null;

        foreach (var path in files.Paths.Order(StringComparer.Ordinal))
        {
            if (!path.StartsWith(root, StringComparison.Ordinal) || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || (isWiki && path.Contains('/')) || !files.TryGet(path, out var content))
            {
                continue;
            }

            var name = path[(path.LastIndexOf('/') + 1)..^3];
            var text = Encoding.UTF8.GetString(content).TrimStart('﻿');
            if (name.StartsWith('_'))
            {
                if (name == "_Sidebar")
                {
                    sidebar = text;
                }

                continue;
            }

            var read = HelpMarkdown.Read(text);
            var title = read.Title ?? name.Replace('-', ' ');
            var id = path == root + (isWiki ? "Home.md" : "README.md") ? HomeId : isWiki ? name : HelpMarkdown.PageId(title);
            if (id.Length > 0 && pages.Values.All(page => !string.Equals(page.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                pages.Add(path, new HelpPage(id, path, id == HomeId && read.Title is null ? product.Name + " user guide" : title,
                    read.Markdown, read.Headings, read.Sections));
            }
        }

        var home = pages.Values.FirstOrDefault(page => page.Id == HomeId);
        var contents = ReadContents(sidebar, home, pages, isWiki);
        var order = contents.SelectMany(group => group.Entries).Select(entry => entry.PageId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ordered = pages.Values.OrderBy(page => order.FindIndex(id => id == page.Id) is var at and >= 0 ? at : int.MaxValue).ToList();

        // A wiki's own title is the page's name; the sidebar has it with its punctuation.
        if (isWiki)
        {
            var titles = contents.SelectMany(group => group.Entries).GroupBy(entry => entry.PageId).ToDictionary(group => group.Key, group => group.First().Title);
            ordered = [.. ordered.Select(page => page.Id != HomeId && titles.TryGetValue(page.Id, out var title) ? page with { Title = title } : page)];
        }

        return new HelpGuide(product, release, files, ordered, contents);
    }

    /// <summary>The page with this id or title, ignoring case, or null.</summary>
    public HelpPage? Find(string? idOrTitle)
    {
        if (string.IsNullOrWhiteSpace(idOrTitle))
        {
            return null;
        }

        return _byId.TryGetValue(idOrTitle.Trim(), out var page) || _byId.TryGetValue(HelpMarkdown.PageId(idOrTitle), out page)
            ? page
            : Pages.FirstOrDefault(candidate => string.Equals(candidate.Title, idOrTitle.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Pages and headings that answer some words or a question, best first. Runs here: nothing is sent anywhere.</summary>
    public IReadOnlyList<HelpSearchResult> Search(string query, int limit = 20) => _search.Search(query, limit);

    /// <summary>Where a link written in <paramref name="page"/> goes.</summary>
    public HelpLink ResolveLink(HelpPage page, string target)
    {
        target = target.Trim();
        if (target.StartsWith('#'))
        {
            return new HelpPageLink(page.Id, page.FindHeading(Decode(target[1..]))?.Anchor);
        }

        if (target.StartsWith("//", StringComparison.Ordinal) || SchemePattern().IsMatch(target))
        {
            return ResolveAddress(target);
        }

        var (path, fragment) = HelpMarkdown.SplitFragment(target);
        path = Decode(path);
        if (HelpPaths.Combine(page.Path, path) is not { } resolved)
        {
            return new HelpBrokenLink(target);
        }

        // The file comes first; "Install-on-macOS" is also a page's name on a wiki.
        if (_byPath.TryGetValue(resolved, out var linked) || (!path.Contains('/') && _byId.TryGetValue(path, out linked)))
        {
            return PageLink(linked, fragment);
        }

        // Only a file that is elsewhere in the repository; a missing guide page is a mistake.
        var isGuidePage = resolved.StartsWith(HelpProduct.GuideFolder + "/", StringComparison.Ordinal) || page.Path.IndexOf('/') < 0;
        return isGuidePage
            ? new HelpBrokenLink(target)
            : new HelpExternalLink(new Uri($"https://github.com/{Product.Repository}/blob/{Release?.Reference ?? "main"}/{EscapePath(resolved)}{(fragment is null ? string.Empty : "#" + fragment)}"));
    }

    /// <summary>
    /// The image a page shows, or null when the guide doesn't hold it - then
    /// show its alt text. Images elsewhere on the web are never fetched, as
    /// opening a page would then tell another server.
    /// </summary>
    public HelpImage? ResolveImage(HelpPage page, string target)
    {
        target = target.Trim();
        var (path, _) = HelpMarkdown.SplitFragment(target);
        var resolved = SchemePattern().IsMatch(target) ? RepositoryPath(target) : HelpPaths.Combine(page.Path, Decode(path));
        return resolved is not null && _files.TryGet(resolved, out var content) ? new HelpImage(resolved, content) : null;
    }

    private HelpPageLink PageLink(HelpPage page, string? fragment)
        => new(page.Id, fragment is null ? null : page.FindHeading(Decode(fragment))?.Anchor);

    private HelpLink ResolveAddress(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var address))
        {
            return new HelpBrokenLink(target);
        }

        if (address.Scheme == Uri.UriSchemeMailto)
        {
            return new HelpExternalLink(address);
        }

        if (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp)
        {
            return new HelpBrokenLink(target);
        }

        // This guide's own pages, linked by their public address.
        var fragment = address.Fragment.Length > 1 ? address.Fragment[1..] : null;
        if (RepositoryPath(target) is { } path && _byPath.TryGetValue(path, out var page))
        {
            return PageLink(page, fragment);
        }

        var wiki = $"/{Product.Repository}/wiki";
        if (IsGitHub(address) && address.AbsolutePath.StartsWith(wiki, StringComparison.OrdinalIgnoreCase))
        {
            var name = Decode(address.AbsolutePath[wiki.Length..]).Trim('/');
            if (address.AbsolutePath.Length == wiki.Length || address.AbsolutePath[wiki.Length] == '/')
            {
                if (_byId.TryGetValue(name.Length == 0 ? HomeId : name, out page))
                {
                    return PageLink(page, fragment);
                }
            }
        }

        return new HelpExternalLink(address);
    }

    /// <summary>
    /// The repository path in one of this repository's GitHub addresses,
    /// whichever branch or tag it names: it's read from this release's files,
    /// so the help never mixes releases.
    /// </summary>
    private string? RepositoryPath(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var path = Decode(address.AbsolutePath);
        var raw = $"/{Product.Repository}/";
        string? rest = null;
        if (address.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) && path.StartsWith(raw, StringComparison.OrdinalIgnoreCase))
        {
            rest = path[raw.Length..];
        }
        else if (IsGitHub(address))
        {
            foreach (var kind in (string[])["blob/", "raw/"])
            {
                if (path.StartsWith(raw + kind, StringComparison.OrdinalIgnoreCase))
                {
                    rest = path[(raw.Length + kind.Length)..];
                }
            }
        }

        // What follows is the branch or tag, then the file.
        if (rest?.StartsWith("refs/heads/", StringComparison.Ordinal) == true)
        {
            rest = rest["refs/heads/".Length..];
        }
        else if (rest?.StartsWith("refs/tags/", StringComparison.Ordinal) == true)
        {
            rest = rest["refs/tags/".Length..];
        }

        var slash = rest?.IndexOf('/') ?? -1;
        return slash > 0 ? HelpPaths.Normalise(rest![(slash + 1)..]) : null;
    }

    private static bool IsGitHub(Uri address) => address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<HelpContentsGroup> ReadContents(string? sidebar, HelpPage? home, Dictionary<string, HelpPage> pages, bool isWiki)
    {
        var groups = new List<(string? Name, List<HelpContentsEntry> Entries)> { (null, []) };
        var listed = new HashSet<string>(StringComparer.Ordinal);
        if (home is not null)
        {
            groups[0].Entries.Add(new HelpContentsEntry(home.Id, home.Title));
            listed.Add(home.Id);
        }

        // A front page groups its links under second-level headings; a sidebar
        // also under a line in bold, as the wiki publisher writes it.
        var from = home?.Path ?? (isWiki ? "Home.md" : HelpProduct.GuideFolder + "/README.md");
        foreach (var (line, isCode) in HelpMarkdown.Lines(sidebar ?? home?.Markdown ?? string.Empty))
        {
            if (isCode)
            {
                continue;
            }

            var links = HelpMarkdown.Links(HelpMarkdown.WithWikiLinks(line)).Where(link => !link.IsImage).ToList();
            var heading = GroupHeading().Match(line);
            if (heading.Success && links.Count == 0 && (heading.Groups["level"].Success || sidebar is not null))
            {
                groups.Add((HelpMarkdown.Plain(heading.Groups["text"].Value), []));
                continue;
            }

            foreach (var link in links)
            {
                var (path, _) = HelpMarkdown.SplitFragment(link.Target);
                var page = HelpPaths.Combine(from, Decode(path)) is { } resolved && pages.TryGetValue(resolved, out var byPath)
                    ? byPath
                    : pages.Values.FirstOrDefault(candidate => string.Equals(candidate.Id, Decode(path), StringComparison.OrdinalIgnoreCase));
                if (page is not null && listed.Add(page.Id))
                {
                    groups[^1].Entries.Add(new HelpContentsEntry(page.Id, isWiki ? HelpMarkdown.Plain(link.Text) : page.Title));
                }
            }
        }

        // Nothing is left unreachable, even by a list someone forgot to add it to.
        var missed = pages.Values.Where(page => !listed.Contains(page.Id)).Select(page => new HelpContentsEntry(page.Id, page.Title)).ToList();
        if (missed.Count > 0)
        {
            groups.Add(("More", missed));
        }

        return [.. groups.Where(group => group.Entries.Count > 0).Select(group => new HelpContentsGroup(group.Name, group.Entries))];
    }

    private static string Decode(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex SchemePattern();

    [GeneratedRegex(@"^\s*(?:(?<level>##)\s+(?<text>.+?)|\*\*(?<text>[^*]+)\*\*)\s*$")]
    private static partial Regex GroupHeading();
}
