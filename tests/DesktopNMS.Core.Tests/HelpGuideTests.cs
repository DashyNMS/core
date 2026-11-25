using System.Text;
using DesktopNMS.Core.Help;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Reading a guide, following its links and searching it (#96). Every page here is made up.</summary>
public class HelpGuideTests
{
    private static readonly HelpProduct Product = new("example", "Example app", "example/app");

    internal static readonly Dictionary<string, string> Pages = new()
    {
        ["docs/guide/README.md"] = """
            # Example user guide

            <img src="https://example.net/logo.svg" alt="Example" width="80">

            Start with [the tour](getting-started.md). It's short.

            ## Everyday tasks

            - [Watch a widget](how-to/watch.md): see it move.
            - [Again](how-to/watch.md#stop-watching)

            ## Reference

            - [Troubleshooting](reference/troubleshooting.md)
            - [The licence](../../LICENSE)
            """,
        ["docs/guide/getting-started.md"] = """
            # Getting started

            Open the app and choose **Sign in**.

            ## What you need

            | Thing | Why |
            | --- | --- |
            | A server | To ask |

            ## What you need

            A second part with the same name.
            """,
        ["docs/guide/how-to/watch.md"] = """
            # Watch a widget

            ![A widget](../../screenshots/widget.png)

            Choose **Watch** to pin the widget. See [what you need](../getting-started.md#what-you-need).

            <!-- not for readers
            at all -->

            ## Stop watching

            Choose `MCP_STOP_*` or **Unpin**.

            ### On a phone

            Swipe it away.

            ## Colours

            ```bash
            # not a heading
            <b>kept</b> <!-- kept -->
            ```
            """,
        ["docs/guide/reference/troubleshooting.md"] = """
            # Troubleshooting

            ## The widget is missing

            Check the server's clock. A certificate that isn't trusted also hides it.
            """,
    };

    internal static HelpFileSet Files(IEnumerable<KeyValuePair<string, string>>? pages = null, params (string Path, byte[] Content)[] others)
        => new((pages ?? Pages).Select(page => new KeyValuePair<string, byte[]>(page.Key, Encoding.UTF8.GetBytes(page.Value)))
            .Concat(others.Select(other => new KeyValuePair<string, byte[]>(other.Path, other.Content))));

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private static HelpGuide Guide(string? version = "2.0.0-nightly.4")
        => HelpGuide.Load(Product, HelpRelease.ForVersion(version), Files(null, ("docs/screenshots/widget.png", Png)));

    [Fact]
    public void Contents_follow_the_front_page_and_group_under_its_headings()
    {
        var guide = Guide();

        Assert.Equal([null, "Everyday tasks", "Reference"], guide.Contents.Select(group => group.Name));
        Assert.Equal(["Home", "Getting-started"], guide.Contents[0].Entries.Select(entry => entry.PageId));
        Assert.Equal(["Watch-a-widget"], guide.Contents[1].Entries.Select(entry => entry.PageId));
        Assert.Equal(["Home", "Getting-started", "Watch-a-widget", "Troubleshooting"], guide.Pages.Select(page => page.Id));
        Assert.Equal("Example user guide", guide.Home!.Title);
    }

    [Fact]
    public void A_page_the_contents_forgot_can_still_be_reached()
    {
        var guide = HelpGuide.Load(Product, null, Files(Pages.Append(new("docs/guide/how-to/lost.md", "# Lost\n\nHere."))));

        Assert.Equal("More", guide.Contents[^1].Name);
        Assert.Equal("Lost", Assert.Single(guide.Contents[^1].Entries).PageId);
    }

    [Fact]
    public void A_page_keeps_its_headings_and_loses_its_title_html_and_comments()
    {
        var page = Guide().Find("watch-A-widget")!;

        Assert.Equal("Watch a widget", page.Title);
        Assert.Equal(["stop-watching", "on-a-phone", "colours"], page.Headings.Select(heading => heading.Anchor));
        Assert.DoesNotContain("# Watch a widget", page.Markdown);
        Assert.DoesNotContain("not for readers", page.Markdown);
        Assert.DoesNotContain("at all", page.Markdown);
        Assert.Contains("# not a heading", page.Markdown);
        Assert.Contains("<b>kept</b> <!-- kept -->", page.Markdown);
        Assert.DoesNotContain("<img", Guide().Home!.Markdown);
        Assert.Equal("Choose MCP_STOP_* or Unpin.", page.Sections[1].Text);
    }

    [Fact]
    public void A_section_can_be_read_with_the_headings_beneath_it()
    {
        var page = Guide().Find("Watch-a-widget")!;

        var section = page.MarkdownUnder("stop-watching")!;

        Assert.StartsWith("Choose `MCP_STOP_*`", section);
        Assert.Contains("### On a phone\n\nSwipe it away.", section);
        Assert.DoesNotContain("Colours", section);
        Assert.Null(page.MarkdownUnder("nowhere"));
    }

    [Fact]
    public void A_repeated_heading_gets_its_own_anchor()
    {
        Assert.Equal(["what-you-need", "what-you-need-1"], Guide().Find("Getting-started")!.Headings.Select(heading => heading.Anchor));
    }

    [Fact]
    public void Links_between_pages_stay_in_the_app()
    {
        var guide = Guide();
        var page = guide.Find("Watch-a-widget")!;

        Assert.Equal(new HelpPageLink("Getting-started", "what-you-need"), guide.ResolveLink(page, "../getting-started.md#what-you-need"));
        Assert.Equal(new HelpPageLink("Getting-started", null), guide.ResolveLink(page, "../getting-started.md#gone"));
        Assert.Equal(new HelpPageLink("Watch-a-widget", "colours"), guide.ResolveLink(page, "#colours"));
        Assert.Equal(new HelpPageLink("Troubleshooting", "the-widget-is-missing"),
            guide.ResolveLink(page, "https://github.com/example/app/wiki/Troubleshooting#the-widget-is-missing"));
        Assert.Equal(new HelpPageLink("Home", null), guide.ResolveLink(page, "https://github.com/example/app/wiki"));
        Assert.Equal(new HelpPageLink("Troubleshooting", null),
            guide.ResolveLink(page, "https://github.com/example/app/blob/main/docs/guide/reference/troubleshooting.md"));
    }

    [Theory]
    [InlineData("https://example.net/page", "https://example.net/page")]
    [InlineData("mailto:help@example.net", "mailto:help@example.net")]
    [InlineData("https://github.com/example/other/wiki/Home", "https://github.com/example/other/wiki/Home")]
    [InlineData("https://github.com/example/app/wikis", "https://github.com/example/app/wikis")]
    public void Addresses_outside_the_guide_open_in_the_browser(string target, string address)
    {
        var guide = Guide();

        Assert.Equal(new HelpExternalLink(new Uri(address)), guide.ResolveLink(guide.Home!, target));
    }

    [Fact]
    public void Repository_files_are_linked_at_the_running_release()
    {
        var guide = Guide();
        Assert.Equal(new HelpExternalLink(new Uri("https://github.com/example/app/blob/v2.0.0-nightly.4/LICENSE")),
            guide.ResolveLink(guide.Home!, "../../LICENSE"));

        var local = Guide(null);
        Assert.Equal(new HelpExternalLink(new Uri("https://github.com/example/app/blob/main/LICENSE")),
            local.ResolveLink(local.Home!, "../../LICENSE"));
    }

    [Theory]
    [InlineData("missing.md")]
    [InlineData("how-to/../missing.md")]
    [InlineData("../../../outside.md")]
    [InlineData("..%2f..%2f..%2foutside.md")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\..\\outside.md")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//example.net/share")]
    [InlineData("C:/Windows/win.ini")]
    public void A_link_that_goes_nowhere_safe_is_reported_as_broken(string target)
    {
        var guide = Guide();

        Assert.Equal(new HelpBrokenLink(target), guide.ResolveLink(guide.Home!, target));
    }

    [Fact]
    public void Images_come_from_the_guides_own_files_whichever_branch_they_name()
    {
        var guide = Guide();
        var page = guide.Find("Watch-a-widget")!;

        Assert.Equal("docs/screenshots/widget.png", guide.ResolveImage(page, "../../screenshots/widget.png")!.Path);
        Assert.Equal(Png, guide.ResolveImage(page, "https://raw.githubusercontent.com/example/app/master/docs/screenshots/widget.png")!.Content);
        Assert.Equal(Png, guide.ResolveImage(page, "https://raw.githubusercontent.com/example/app/refs/heads/main/docs/screenshots/widget.png")!.Content);
        Assert.Equal(Png, guide.ResolveImage(page, "https://github.com/example/app/raw/v1.0.0/docs/screenshots/widget.png")!.Content);
    }

    [Theory]
    [InlineData("../../screenshots/gone.png")]
    [InlineData("../../../../screenshots/widget.png")]
    [InlineData("https://example.net/widget.png")]
    [InlineData("https://raw.githubusercontent.com/example/other/main/docs/screenshots/widget.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("/docs/screenshots/widget.png")]
    public void An_image_the_guide_does_not_hold_is_not_fetched(string target)
    {
        var guide = Guide();

        Assert.Null(guide.ResolveImage(guide.Find("Watch-a-widget")!, target));
    }

    [Fact]
    public void A_wiki_is_read_by_its_sidebar_and_page_names()
    {
        var guide = HelpGuide.Load(Product, null, Files(new Dictionary<string, string>
        {
            ["Home.md"] = "Welcome. See [[Watch a widget]] or [[the fixes|Troubleshooting#clock]].",
            ["Watch-a-widget.md"] = "## Stop\n\nStop it.",
            ["Troubleshooting.md"] = "## Clock\n\nCheck it.",
            ["_Sidebar.md"] = "**[User guide](Home)**\n\n**Tasks**\n\n- [Watch a widget!](Watch-a-widget)\n\n**Help**\n\n- [[Fixes|Troubleshooting]]\n",
            ["_Footer.md"] = "Generated.",
        }));

        Assert.Equal([null, "Tasks", "Help"], guide.Contents.Select(group => group.Name));
        Assert.Equal("Watch a widget!", guide.Find("Watch-a-widget")!.Title);
        Assert.Equal(3, guide.Pages.Count);
        Assert.Contains("[Watch a widget](Watch-a-widget)", guide.Home!.Markdown);
        Assert.Equal(new HelpPageLink("Watch-a-widget", "stop"), guide.ResolveLink(guide.Home!, "Watch-a-widget#stop"));
        Assert.Equal(new HelpPageLink("Troubleshooting", "clock"), guide.ResolveLink(guide.Home!, "Troubleshooting#clock"));
        Assert.Equal(new HelpBrokenLink("Nowhere"), guide.ResolveLink(guide.Home!, "Nowhere"));
    }

    [Theory]
    [InlineData("How do I watch a widget?", "Watch-a-widget", null)]
    [InlineData("unpinning", "Watch-a-widget", "stop-watching")]
    [InlineData("certificate not trusted", "Troubleshooting", "the-widget-is-missing")]
    [InlineData("certif", "Troubleshooting", "the-widget-is-missing")]
    public void Search_finds_the_page_and_heading(string query, string page, string? anchor)
    {
        var results = Guide().Search(query);

        Assert.Equal(page, results[0].Page.Id);
        Assert.Equal(anchor, results[0].Heading?.Anchor);
        Assert.NotEmpty(results[0].Passage);
    }

    [Theory]
    [InlineData("quantum espresso recipe")]
    [InlineData("")]
    [InlineData("???")]
    public void Search_finds_nothing_rather_than_guessing(string query)
    {
        Assert.Empty(Guide().Search(query));
    }

    [Fact]
    public void Search_keeps_to_its_limit()
    {
        Assert.Single(Guide().Search("widget", 1));
        Assert.Empty(Guide().Search("widget", 0));
    }

    [Theory]
    [InlineData("2.0.0-nightly.4+abc123", "2.0.0-nightly.4", "v2.0.0-nightly.4")]
    [InlineData("v2.0.0", "2.0.0", "v2.0.0")]
    public void A_release_comes_from_the_apps_version(string version, string shown, string reference)
    {
        var release = HelpRelease.ForVersion(version)!;

        Assert.Equal(shown, release.Version);
        Assert.Equal(reference, release.Reference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("2.0.0/../..")]
    public void A_version_that_names_no_release_gives_none(string? version)
    {
        Assert.Null(HelpRelease.ForVersion(version));
        Assert.Throws<ArgumentException>(() => HelpRelease.ForReference("2.0.0", "../main"));
    }

    [Fact]
    public void A_folder_is_read_as_a_set_of_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "dashynms-help-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (path, text) in Pages)
            {
                var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, text);
            }

            Assert.Equal(4, HelpGuide.Load(Product, null, HelpFileSet.FromDirectory(root)).Pages.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Nothing_bundled_gives_an_empty_guide()
    {
        var guide = HelpGuide.Load(Product, null, HelpFileSet.FromDirectory(Path.Combine(Path.GetTempPath(), "dashynms-not-there")));

        Assert.Null(guide.Home);
        Assert.Empty(guide.Contents);
        Assert.Empty(guide.Search("widget"));
    }

    [Fact]
    public void A_wiki_address_is_a_pages_public_place()
    {
        Assert.Equal("https://github.com/DashyNMS/desktop/wiki/Install-on-macOS#update", HelpProduct.Desktop.WikiAddress("Install-on-macOS", "update").ToString());
    }
}
