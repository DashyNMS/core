using System.Net;
using System.Text;
using DesktopNMS.Core.Help;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Bundled, downloaded and saved guides (#96). The server is in memory and every page is made up.</summary>
public sealed class HelpGuideServiceTests : IDisposable
{
    private static readonly HelpProduct Product = new("example", "Example app", "example/app");
    private static readonly HelpRelease Release = HelpRelease.ForVersion("2.0.0-nightly.4")!;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dashynms-help-" + Guid.NewGuid().ToString("N"));
    private readonly Server _server = new();
    private readonly Clock _clock = new();

    public HelpGuideServiceTests()
    {
        foreach (var (path, text) in HelpGuideTests.Pages)
        {
            _server.Files[path] = Encoding.UTF8.GetBytes(text);
        }

        _server.Files["docs/screenshots/widget.png"] = [1, 2, 3];
    }

    private HelpGuideService Service(HelpRelease? release = null, HelpFileSet? bundled = null, bool known = true)
        => new(Product, known ? release ?? Release : null, bundled ?? HelpGuideTests.Files(), _folder, new HttpClient(_server), _clock);

    [Fact]
    public async Task The_bundled_guide_is_there_at_once_and_nothing_is_requested()
    {
        using var service = Service();
        await service.InitialiseAsync();

        Assert.Equal(HelpGuideSource.Bundled, service.Source);
        Assert.Equal("2.0.0-nightly.4", service.Current.Release!.Version);
        Assert.Equal(4, service.Current.Pages.Count);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_refresh_reads_the_running_releases_tag_with_a_generic_name()
    {
        using var service = Service();

        Assert.Equal(HelpRefreshOutcome.Updated, await service.RefreshAsync());

        Assert.Equal(HelpGuideSource.Downloaded, service.Source);
        Assert.Equal(_clock.Now, service.TakenAt);
        Assert.NotNull(service.Current.ResolveImage(service.Current.Find("Watch-a-widget")!, "../../screenshots/widget.png"));
        Assert.All(_server.Requests, address => Assert.StartsWith("https://raw.githubusercontent.com/example/app/v2.0.0-nightly.4/docs/", address));
        Assert.Equal(["DashyNMS-Help/1.0"], _server.Agents.Distinct());
        Assert.All(_server.Headers, names => Assert.Equal(["User-Agent"], names));
    }

    [Fact]
    public async Task A_changed_page_is_shown_and_the_change_is_announced()
    {
        _server.Files["docs/guide/reference/troubleshooting.md"] = Encoding.UTF8.GetBytes("# Troubleshooting\n\nNew advice.");
        using var service = Service(bundled: HelpGuideTests.Files(null, ("docs/screenshots/widget.png", [1, 2, 3])));
        var raised = 0;
        service.Changed += (_, _) => raised++;

        Assert.Equal(HelpRefreshOutcome.Updated, await service.RefreshAsync());

        Assert.Equal(1, raised);
        Assert.Contains("New advice.", service.Current.Find("Troubleshooting")!.Markdown);
        Assert.Equal(HelpRefreshOutcome.NotDue, await service.RefreshAsync());
        Assert.Equal(HelpRefreshOutcome.Unchanged, await service.RefreshAsync(force: true));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task The_same_pages_as_the_bundled_ones_are_not_a_change()
    {
        using var service = Service(bundled: HelpGuideTests.Files(null, ("docs/screenshots/widget.png", [1, 2, 3])));
        var raised = 0;
        service.Changed += (_, _) => raised++;

        Assert.Equal(HelpRefreshOutcome.Unchanged, await service.RefreshAsync());
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task A_saved_copy_is_used_offline_after_a_restart()
    {
        _server.Files["docs/guide/reference/troubleshooting.md"] = Encoding.UTF8.GetBytes("# Troubleshooting\n\nNew advice.");
        using (var first = Service())
        {
            await first.RefreshAsync();
        }

        _server.Offline = true;
        using var second = Service();
        var raised = 0;
        second.Changed += (_, _) => raised++;
        await second.InitialiseAsync();

        Assert.Equal(HelpGuideSource.Downloaded, second.Source);
        Assert.Equal(1, raised);
        Assert.Contains("New advice.", second.Current.Find("Troubleshooting")!.Markdown);
        Assert.Equal(HelpRefreshOutcome.NotDue, await second.RefreshAsync());
        Assert.Equal(HelpRefreshOutcome.Failed, await second.RefreshAsync(force: true));
        Assert.Contains("New advice.", second.Current.Find("Troubleshooting")!.Markdown);
    }

    [Fact]
    public async Task An_offline_refresh_keeps_the_bundled_guide_and_waits_before_trying_again()
    {
        _server.Offline = true;
        using var service = Service();

        Assert.Equal(HelpRefreshOutcome.Failed, await service.RefreshAsync());
        Assert.Equal(HelpGuideSource.Bundled, service.Source);
        Assert.Equal(4, service.Current.Pages.Count);
        Assert.Equal(HelpRefreshOutcome.NotDue, await service.RefreshAsync());

        _server.Offline = false;
        _clock.Now += HelpGuideService.RetryInterval;
        Assert.Equal(HelpRefreshOutcome.Updated, await service.RefreshAsync());
    }

    [Fact]
    public async Task A_refresh_that_fails_part_way_keeps_the_last_good_copy()
    {
        using var service = Service();
        await service.RefreshAsync();
        var good = service.Current;

        _server.Files["docs/guide/getting-started.md"] = Encoding.UTF8.GetBytes("# Getting started\n\nHalf new.");
        _server.Failing = "docs/guide/reference/troubleshooting.md";
        Assert.Equal(HelpRefreshOutcome.Failed, await service.RefreshAsync(force: true));
        Assert.Same(good, service.Current);

        using var restarted = Service();
        await restarted.InitialiseAsync();
        Assert.DoesNotContain("Half new.", restarted.Current.Find("Getting-started")!.Markdown);
    }

    [Fact]
    public async Task A_release_without_a_guide_keeps_the_bundled_one()
    {
        _server.Files.Clear();
        using var service = Service();

        Assert.Equal(HelpRefreshOutcome.NotPublished, await service.RefreshAsync());
        Assert.Equal(HelpGuideSource.Bundled, service.Source);
    }

    [Fact]
    public async Task A_missing_page_is_a_broken_link_and_the_rest_is_kept()
    {
        _server.Files.Remove("docs/guide/reference/troubleshooting.md");
        using var service = Service(bundled: HelpGuideTests.Files(HelpGuideTests.Pages.Where(page => !page.Key.Contains("troubleshooting", StringComparison.Ordinal))));

        Assert.Equal(HelpRefreshOutcome.Updated, await service.RefreshAsync());
        Assert.Equal(new HelpBrokenLink("reference/troubleshooting.md"), service.Current.ResolveLink(service.Current.Home!, "reference/troubleshooting.md"));
    }

    [Fact]
    public async Task A_download_without_a_page_the_app_was_built_with_is_not_used()
    {
        _server.Files.Remove("docs/guide/reference/troubleshooting.md");
        using var service = Service();

        Assert.Equal(HelpRefreshOutcome.Failed, await service.RefreshAsync());
        Assert.Equal(HelpGuideSource.Bundled, service.Source);
        Assert.NotNull(service.Current.Find("Troubleshooting"));
    }

    [Fact]
    public async Task A_cancelled_refresh_still_announces_the_saved_copy_it_found()
    {
        _server.Files["docs/guide/reference/troubleshooting.md"] = Encoding.UTF8.GetBytes("# Troubleshooting\n\nNew advice.");
        using (var first = Service())
        {
            await first.RefreshAsync();
        }

        using var second = Service();
        using var cancelled = new CancellationTokenSource();
        var raised = 0;
        second.Changed += (_, _) => raised++;
        _server.Asked = cancelled.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.RefreshAsync(force: true, cancelled.Token));

        Assert.Equal(1, raised);
        Assert.Equal((HelpGuideSource.Downloaded, true), (second.Shown.Source, second.Shown.Guide == second.Current));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Desktop")]
    [InlineData("")]
    public void A_product_id_that_could_not_name_a_folder_is_refused(string id)
        => Assert.Throws<ArgumentException>(() => new HelpGuideService(new HelpProduct(id, "Example", "example/app"), Release, HelpFileSet.Empty, _folder));

    [Fact]
    public async Task Links_out_of_the_guide_are_never_requested()
    {
        _server.Files["docs/guide/getting-started.md"] = Encoding.UTF8.GetBytes(
            "# Getting started\n\n[a](../../../../secret.md) [b](..%2f..%2fsecret.md) [c](../../README.md) ![d](../../../../secret.png) ![e](https://example.net/e.png) [f](https://example.net/f.md)");
        using var service = Service();

        await service.RefreshAsync();

        Assert.All(_server.Requests, address => Assert.StartsWith("https://raw.githubusercontent.com/example/app/v2.0.0-nightly.4/docs/", address));
        Assert.DoesNotContain(_server.Requests, address => address.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pages_linked_by_their_address_or_with_a_query_are_fetched_at_the_release()
    {
        _server.Files["docs/guide/README.md"] = Encoding.UTF8.GetBytes(
            "# Guide\n\n[a](a.md?plain=1) [b](https://github.com/example/app/blob/main/docs/guide/b.md) ![c](https://raw.githubusercontent.com/example/app/main/docs/c.png)");
        _server.Files["docs/guide/a.md"] = "# A"u8.ToArray();
        _server.Files["docs/guide/b.md"] = "# B"u8.ToArray();
        _server.Files["docs/c.png"] = [7];
        using var service = Service(bundled: HelpFileSet.Empty);

        Assert.Equal(HelpRefreshOutcome.Updated, await service.RefreshAsync());

        Assert.Equal(["Home", "A", "B"], service.Current.Pages.Select(page => page.Id));
        Assert.Contains("https://raw.githubusercontent.com/example/app/v2.0.0-nightly.4/docs/c.png", _server.Requests);
        Assert.All(_server.Requests, address => Assert.Contains("/v2.0.0-nightly.4/", address, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_host_that_only_reads_text_fetches_no_images()
    {
        using var service = new HelpGuideService(Product, Release, HelpGuideTests.Files(), http: new HttpClient(_server), time: _clock, includeImages: false);

        Assert.Equal(HelpRefreshOutcome.Unchanged, await service.RefreshAsync());
        Assert.DoesNotContain(_server.Requests, address => address.EndsWith(".png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        _server.Redirecting = true;
        using var service = Service();

        Assert.Equal(HelpRefreshOutcome.Failed, await service.RefreshAsync());
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task A_page_that_is_too_large_fails_the_refresh()
    {
        _server.Files["docs/guide/getting-started.md"] = new byte[600 * 1024];
        using var service = Service();

        Assert.Equal(HelpRefreshOutcome.Failed, await service.RefreshAsync());
        Assert.Equal(HelpGuideSource.Bundled, service.Source);
    }

    [Fact]
    public async Task A_damaged_copy_is_not_used()
    {
        using (var first = Service())
        {
            await first.RefreshAsync();
        }

        var saved = Directory.EnumerateFiles(Path.Combine(_folder, "example", "v2.0.0-nightly.4", "files")).First();
        await File.WriteAllTextAsync(saved, "damaged");
        using var second = Service();
        await second.InitialiseAsync();

        Assert.Equal(HelpGuideSource.Bundled, second.Source);
        Assert.Equal(HelpRefreshOutcome.Updated, await second.RefreshAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"Schema":1,"Product":"example","Reference":"v2.0.0-nightly.4","Version":"x","TakenAt":"2026-01-01T00:00:00Z","Files":{"docs/guide/README.md":"../../../outside"}}""")]
    [InlineData("""{"Schema":1,"Product":"example","Reference":"v2.0.0-nightly.4","Version":"x","TakenAt":"2026-01-01T00:00:00Z","Files":{"docs/guide/README.md":null}}""")]
    [InlineData("""{"Schema":1,"Product":"example","Reference":"v2.0.0-nightly.4","Version":"x","TakenAt":"2026-01-01T00:00:00Z","Files":{"../README.md":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}}""")]
    public async Task A_list_this_app_did_not_write_is_not_used(string manifest)
    {
        var folder = Path.Combine(_folder, "example", "v2.0.0-nightly.4");
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        await File.WriteAllTextAsync(Path.Combine(folder, "snapshot.json"), manifest);
        using var service = Service();

        await service.InitialiseAsync();

        Assert.Equal(HelpGuideSource.Bundled, service.Source);
        Assert.Equal(HelpRefreshOutcome.Updated, await service.RefreshAsync());
    }

    [Fact]
    public async Task A_new_release_does_not_use_the_last_ones_copy_and_clears_it_once_it_has_its_own()
    {
        using (var old = Service())
        {
            await old.RefreshAsync();
        }

        using var service = Service(HelpRelease.ForVersion("2.0.0-nightly.5"));
        await service.InitialiseAsync();
        Assert.Equal(HelpGuideSource.Bundled, service.Source);
        Assert.True(Directory.Exists(Path.Combine(_folder, "example", "v2.0.0-nightly.4")));

        _server.Tag = "v2.0.0-nightly.5";
        Assert.Equal(HelpRefreshOutcome.Updated, await service.RefreshAsync());
        Assert.False(Directory.Exists(Path.Combine(_folder, "example", "v2.0.0-nightly.4")));
    }

    [Fact]
    public async Task Without_a_known_release_nothing_is_requested()
    {
        using var service = Service(known: false);

        Assert.Equal(HelpRefreshOutcome.NotPublished, await service.RefreshAsync(force: true));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_cancelled_refresh_stops_and_changes_nothing()
    {
        using var service = Service();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(cancellationToken: cancelled.Token));
        Assert.Equal(HelpGuideSource.Bundled, service.Source);
    }

    [Fact]
    public async Task An_image_that_could_not_be_had_falls_back_to_the_bundled_one()
    {
        _server.Files.Remove("docs/screenshots/widget.png");
        using var service = Service(bundled: HelpGuideTests.Files(null, ("docs/screenshots/widget.png", [9])));

        await service.RefreshAsync();

        Assert.Equal(new byte[] { 9 }, service.Current.ResolveImage(service.Current.Find("Watch-a-widget")!, "../../screenshots/widget.png")!.Content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Server : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public List<string> Requests { get; } = [];

        public List<string> Agents { get; } = [];

        public List<string[]> Headers { get; } = [];

        public string Tag { get; set; } = "v2.0.0-nightly.4";

        public bool Offline { get; set; }

        public bool Redirecting { get; set; }

        public string? Failing { get; set; }

        public Action? Asked { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("No route to host.");
            }

            Asked?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var address = request.RequestUri!.ToString();
            Requests.Add(address);
            Agents.Add(request.Headers.UserAgent.ToString());
            Headers.Add([.. request.Headers.Select(header => header.Key)]);
            if (Redirecting)
            {
                return Answer(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.net/") } });
            }

            var prefix = $"https://raw.githubusercontent.com/example/app/{Tag}/";
            var path = address.StartsWith(prefix, StringComparison.Ordinal) ? address[prefix.Length..] : string.Empty;
            return Answer(path == Failing
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : Files.TryGetValue(path, out var content)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));

            Task<HttpResponseMessage> Answer(HttpResponseMessage response)
            {
                response.RequestMessage = request;
                return Task.FromResult(response);
            }
        }
    }
}
