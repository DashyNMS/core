namespace DesktopNMS.Core.Updates;

/// <summary>
/// Chooses the release an update channel should offer, so every app treats
/// the channels the same way.
/// </summary>
public static class UpdateChannels
{
    // Enough to reach back past a day's edge builds to the last nightly.
    private const int RecentReleases = 100;

    /// <summary>Whether <paramref name="channel"/> is offered builds of <paramref name="version"/>. Stable releases reach everyone.</summary>
    public static bool Offers(UpdateChannel channel, string? version)
    {
        var from = ReleaseVersion.ChannelOf(version);
        return from == UpdateChannel.Stable || from == channel;
    }

    /// <summary>
    /// The newest release for <paramref name="channel"/>, or null if it has
    /// none or GitHub couldn't be read. A stable release can be older than
    /// every recent build, so it's read as well as the list.
    /// </summary>
    public static async Task<GitHubRelease?> FindAsync(IGitHubReleaseService releases, UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releases);

        var latest = await releases.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        var recent = channel == UpdateChannel.Stable
            ? Array.Empty<GitHubRelease>()
            : await releases.GetReleasesAsync(RecentReleases, cancellationToken).ConfigureAwait(false);
        return Newest(recent.Append(latest).OfType<GitHubRelease>(), channel);
    }

    /// <summary>The newest of <paramref name="releases"/> that <paramref name="channel"/> is offered.</summary>
    public static GitHubRelease? Newest(IEnumerable<GitHubRelease> releases, UpdateChannel channel)
    {
        ArgumentNullException.ThrowIfNull(releases);

        // Unrankable tags go first: one compares equal to everything, which
        // would leave the order of the rest undefined.
        return releases
            .Where(r => !r.Draft && ReleaseVersion.IsValid(r.TagName) && Offers(channel, r.TagName)
                && (channel != UpdateChannel.Stable || !r.Prerelease))
            .OrderByDescending(r => r.TagName, Comparer<string?>.Create(ReleaseVersion.Compare))
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> should be offered to an app
    /// running <paramref name="current"/> on <paramref name="channel"/>.
    /// Edge and nightly builds of one version are counted separately, so
    /// after switching between them the other channel's build is offered
    /// whatever its number. An earlier version never is.
    /// </summary>
    public static bool IsUpdate(string? candidate, string? current, UpdateChannel channel)
    {
        if (!ReleaseVersion.IsValid(candidate) || !ReleaseVersion.IsValid(current) || !Offers(channel, candidate))
        {
            return false;
        }

        var from = ReleaseVersion.ChannelOf(current);
        var to = ReleaseVersion.ChannelOf(candidate);
        return from != to && from != UpdateChannel.Stable && to != UpdateChannel.Stable && ReleaseVersion.SameCore(candidate, current)
            || ReleaseVersion.IsNewer(candidate, current);
    }
}
