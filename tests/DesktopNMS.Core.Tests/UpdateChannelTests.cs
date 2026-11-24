using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Updates;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class UpdateChannelTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData("2.0.0-preview.20", "2.0.0-edge.999")]
    [InlineData("2.0.0-nightly.1", "2.0.0-preview.20")]
    [InlineData("2.0.0-nightly.10", "2.0.0-nightly.9")]
    [InlineData("2.0.0-edge.10", "2.0.0-edge.9")]
    [InlineData("2.0.0", "2.0.0-nightly.99")]
    [InlineData("2.0.0", "2.0.0-edge.99")]
    [InlineData("2.1.0-edge.1", "2.0.0")]
    public void Builds_rank_edge_preview_nightly_then_stable(string higher, string lower)
    {
        Assert.True(ReleaseVersion.Compare(higher, lower) > 0);
        Assert.True(ReleaseVersion.Compare(lower, higher) < 0);
    }

    [Theory]
    [InlineData("2.0.0", UpdateChannel.Stable)]
    [InlineData("v2.0.0-preview.20", UpdateChannel.Nightly)]
    [InlineData("2.0.0-preview.pr.1.2.3", UpdateChannel.Nightly)]
    [InlineData("2.0.0-nightly.3+abcdef", UpdateChannel.Nightly)]
    [InlineData("2.0.0-edge.3", UpdateChannel.Edge)]
    [InlineData("nonsense", UpdateChannel.Stable)]
    public void A_version_names_its_channel(string version, UpdateChannel channel)
        => Assert.Equal(channel, ReleaseVersion.ChannelOf(version));

    [Theory]
    // Within a channel, and to a release.
    [InlineData("2.0.0-nightly.4", "2.0.0-nightly.3", UpdateChannel.Nightly, true)]
    [InlineData("2.0.0-nightly.3", "2.0.0-nightly.3", UpdateChannel.Nightly, false)]
    [InlineData("2.0.0-nightly.1", "2.0.0-preview.20", UpdateChannel.Nightly, true)]
    [InlineData("2.0.0", "2.0.0-edge.9", UpdateChannel.Edge, true)]
    [InlineData("2.0.0", "2.0.0-nightly.9", UpdateChannel.Stable, true)]
    // Switching channel, whatever the numbers.
    [InlineData("2.0.0-edge.1", "2.0.0-nightly.9", UpdateChannel.Edge, true)]
    [InlineData("2.0.0-nightly.1", "2.0.0-edge.9", UpdateChannel.Nightly, true)]
    // Never to another channel's build, or back a version.
    [InlineData("2.0.0-edge.10", "2.0.0-nightly.9", UpdateChannel.Nightly, false)]
    [InlineData("2.0.0-nightly.10", "2.0.0-edge.9", UpdateChannel.Edge, false)]
    [InlineData("2.0.0-nightly.10", "1.1.0", UpdateChannel.Stable, false)]
    [InlineData("2.0.0-edge.9", "2.1.0-nightly.1", UpdateChannel.Edge, false)]
    [InlineData("2.0.0-edge.9", "2.0.0", UpdateChannel.Edge, false)]
    [InlineData("1.1.0", "2.0.0-nightly.1", UpdateChannel.Stable, false)]
    public void Only_the_chosen_channels_later_builds_are_updates(string candidate, string current, UpdateChannel channel, bool expected)
        => Assert.Equal(expected, UpdateChannels.IsUpdate("v" + candidate, current, channel));

    [Fact]
    public async Task Each_channel_finds_its_newest_release()
    {
        var service = new Releases
        {
            Latest = Release("v1.1.0"),
            Recent =
            {
                Release("not-a-version"), Release("v2.0.0-edge.8", true), Release("v2.0.0-edge.7", true),
                Release("v2.0.0-preview.20", true), Release("v2.0.0-nightly.2", true), Release("v1.1.0"),
            },
        };
        Assert.Equal("v1.1.0", (await UpdateChannels.FindAsync(service, UpdateChannel.Stable))!.TagName);
        Assert.Equal("v2.0.0-nightly.2", (await UpdateChannels.FindAsync(service, UpdateChannel.Nightly))!.TagName);
        Assert.Equal("v2.0.0-edge.8", (await UpdateChannels.FindAsync(service, UpdateChannel.Edge))!.TagName);
        service.Latest = Release("v2.0.0");
        Assert.Equal("v2.0.0", (await UpdateChannels.FindAsync(service, UpdateChannel.Edge))!.TagName);
    }

    [Fact]
    public async Task A_nightly_is_found_behind_a_busy_days_edge_builds()
    {
        var service = new Releases();
        for (var build = 60; build > 0; build--) service.Recent.Add(Release($"v2.0.0-edge.{build}", true));
        service.Recent.Add(Release("v2.0.0-nightly.5", true));
        Assert.Equal("v2.0.0-nightly.5", (await UpdateChannels.FindAsync(service, UpdateChannel.Nightly))!.TagName);
    }

    [Fact]
    public async Task A_release_older_than_every_recent_nightly_is_still_found()
    {
        var service = new Releases { Latest = Release("v2.0.0") };
        for (var build = 30; build > 20; build--) service.Recent.Add(Release($"v2.0.0-nightly.{build}", true));
        Assert.Equal("v2.0.0", (await UpdateChannels.FindAsync(service, UpdateChannel.Nightly))!.TagName);
        service.Recent[0] = Release("v2.1.0-nightly.31", true);
        Assert.Equal("v2.1.0-nightly.31", (await UpdateChannels.FindAsync(service, UpdateChannel.Nightly))!.TagName);
        // GitHub couldn't give one of the two.
        service.Latest = null;
        Assert.Equal("v2.1.0-nightly.31", (await UpdateChannels.FindAsync(service, UpdateChannel.Nightly))!.TagName);
    }

    [Fact]
    public async Task Releases_that_cannot_be_read_still_leave_the_stable_one()
    {
        Assert.Equal("v2.0.0", (await UpdateChannels.FindAsync(new Releases { Latest = Release("v2.0.0") }, UpdateChannel.Edge))!.TagName);
        Assert.Null(await UpdateChannels.FindAsync(new Releases(), UpdateChannel.Edge));
        Assert.Null(await UpdateChannels.FindAsync(new Releases { Latest = Release("v2.0.0", true) }, UpdateChannel.Stable));
    }

    [Theory]
    [InlineData("{}", UpdateChannel.Stable)]
    [InlineData("""{"includePreviewBuilds":true}""", UpdateChannel.Nightly)]
    [InlineData("""{"updateChannel":"Edge","includePreviewBuilds":true}""", UpdateChannel.Edge)]
    [InlineData("""{"includePreviewBuilds":false,"updateChannel":"Stable"}""", UpdateChannel.Stable)]
    public void Saved_preview_choices_become_channels(string json, UpdateChannel expected)
        => Assert.Equal(expected, JsonSerializer.Deserialize<AppSettings>(json, Json)!.UpdateChannel);

    [Theory]
    [InlineData(UpdateChannel.Stable)]
    [InlineData(UpdateChannel.Nightly)]
    [InlineData(UpdateChannel.Edge)]
    public void A_channel_survives_saving_cloning_and_an_older_app(UpdateChannel channel)
    {
        var settings = new AppSettings { IncludePreviewBuilds = true, UpdateChannel = channel };
        Assert.Equal(channel != UpdateChannel.Stable, settings.IncludePreviewBuilds);
        Assert.Equal(channel, settings.Clone().UpdateChannel);
        Assert.Equal(channel, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Json), Json)!.UpdateChannel);
    }

    [Theory]
    [InlineData(UpdateChannel.Stable, true, UpdateChannel.Nightly)]
    [InlineData(UpdateChannel.Edge, true, UpdateChannel.Edge)]
    [InlineData(UpdateChannel.Edge, false, UpdateChannel.Stable)]
    [InlineData(UpdateChannel.Nightly, false, UpdateChannel.Stable)]
    public void The_old_choice_moves_the_channel_only_when_they_disagree(UpdateChannel channel, bool previews, UpdateChannel expected)
    {
        var settings = new AppSettings { UpdateChannel = channel };
        settings.IncludePreviewBuilds = previews;
        Assert.Equal(expected, settings.UpdateChannel);
        Assert.Equal(expected != UpdateChannel.Stable, settings.IncludePreviewBuilds);
    }

    private static GitHubRelease Release(string tag, bool prerelease = false) => new() { TagName = tag, Name = tag, Prerelease = prerelease };

    private sealed class Releases : IGitHubReleaseService
    {
        public GitHubRelease? Latest { get; set; }

        public List<GitHubRelease> Recent { get; } = new();

        public Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default) => Task.FromResult(Latest);

        public Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(int count = 10, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitHubRelease>>(Recent.Take(count).ToList());
    }
}
