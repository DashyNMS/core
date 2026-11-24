namespace DesktopNMS.Core.Updates;

/// <summary>
/// Chooses the release an update channel should offer, so every app treats
/// the channels the same way.
/// </summary>
public static class UpdateChannels
{
    /// <summary>
    /// The one release that always holds the newest edge build: its title is
    /// the build's version and its files are replaced by each build. The tag
    /// is 0.0.0 because apps from before the channels read every tag as a
    /// version, and this has to rank below whatever they're running.
    /// </summary>
    public const string EdgeTag = "v0.0.0-edge";

    /// <summary>Whether <paramref name="channel"/> is offered builds of <paramref name="version"/>. Stable releases reach everyone.</summary>
    public static bool Offers(UpdateChannel channel, string? version)
    {
        var from = ReleaseVersion.ChannelOf(version);
        return from == UpdateChannel.Stable || from == channel;
    }

    /// <summary>
    /// The newest release for <paramref name="channel"/>, or null if it has
    /// none or GitHub couldn't be read. Edge is fetched by its tag: the
    /// release is as old as its first build, so a list of recent releases
    /// stops including it.
    /// </summary>
    public static async Task<GitHubRelease?> FindAsync(IGitHubReleaseService releases, UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releases);

        switch (channel)
        {
            case UpdateChannel.Nightly:
                return Newest(await releases.GetReleasesAsync(cancellationToken: cancellationToken).ConfigureAwait(false), channel);

            case UpdateChannel.Edge:
                var edge = await releases.GetReleaseByTagAsync(EdgeTag, cancellationToken).ConfigureAwait(false);
                var stable = await releases.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
                return Newest(new[] { edge, stable }.OfType<GitHubRelease>(), channel);

            default:
                var latest = await releases.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
                return latest is null ? null : Newest(new[] { latest }, UpdateChannel.Stable);
        }
    }

    /// <summary>The newest of <paramref name="releases"/> that <paramref name="channel"/> is offered.</summary>
    public static GitHubRelease? Newest(IEnumerable<GitHubRelease> releases, UpdateChannel channel)
    {
        ArgumentNullException.ThrowIfNull(releases);

        // Unrankable tags go first: one compares equal to everything, which
        // would leave the order of the rest undefined.
        return releases
            .Where(r => !r.Draft && ReleaseVersion.IsValid(r.Version) && Offers(channel, r.Version)
                && (channel != UpdateChannel.Stable || !r.Prerelease))
            .OrderByDescending(r => r.Version, Comparer<string?>.Create(ReleaseVersion.Compare))
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
