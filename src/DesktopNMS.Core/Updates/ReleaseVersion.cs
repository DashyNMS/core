using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Updates;

/// <summary>Compares a GitHub release tag against the running app's version.</summary>
public static class ReleaseVersion
{
    /// <summary>
    /// True if <paramref name="candidateTag"/> (e.g. "v0.2.0", "0.2.0") is a
    /// higher version than <paramref name="currentVersion"/> (e.g. "0.1.0"),
    /// using the same preview-aware ranking as <see cref="Compare"/> - this
    /// used to do its own cruder, non-preview-aware comparison, which meant a
    /// newer preview build (e.g. "1.0.0-preview.2") was never detected as an
    /// update over an older one ("1.0.0-preview.1") already running, since
    /// both discarded their preview suffix and compared equal (issue #107).
    /// Anything that cannot be parsed as a version is treated as not newer, so
    /// a malformed or unexpected tag never produces a false "update available".
    /// </summary>
    public static bool IsNewer(string? candidateTag, string? currentVersion) =>
        Compare(candidateTag, currentVersion) > 0;

    /// <summary>True if <paramref name="tag"/> holds a version that can be ranked.</summary>
    public static bool IsValid(string? tag) => Parse(tag) is not null;

    /// <summary>The channel a version belongs to; previews count as nightly, which replaced them, and anything else is stable.</summary>
    public static UpdateChannel ChannelOf(string? version) => Parse(version)?.Stage switch
    {
        Stage.Edge => UpdateChannel.Edge,
        Stage.Preview or Stage.Nightly => UpdateChannel.Nightly,
        _ => UpdateChannel.Stable,
    };

    /// <summary>True if both tags are builds of one version, such as "2.0.0-edge.4" and "v2.0.0".</summary>
    public static bool SameCore(string? a, string? b) =>
        Parse(a) is { } parsedA && Parse(b) is { } parsedB && parsedA.Core == parsedB.Core;

    /// <summary>
    /// Ranks two release tags against each other so the best one can be
    /// picked out of a list mixing stable releases and builds of them (e.g.
    /// "1.0.0" vs "1.0.0-nightly.2"). Compares the numeric core first; for the
    /// same core, edge ranks below preview, preview below nightly and nightly
    /// below stable, and between two of a kind the higher number wins. Not
    /// SemVer's alphabetical order, which would rank nightly below the previews
    /// it replaced. Returns positive if <paramref name="a"/> ranks higher than
    /// <paramref name="b"/>, negative if lower, and zero if either tag cannot
    /// be parsed.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var parsedA = Parse(a);
        var parsedB = Parse(b);

        if (parsedA is null || parsedB is null)
        {
            return 0;
        }

        var coreCompare = parsedA.Value.Core.CompareTo(parsedB.Value.Core);
        if (coreCompare != 0)
        {
            return coreCompare;
        }

        var stageCompare = parsedA.Value.Stage.CompareTo(parsedB.Value.Stage);
        return stageCompare != 0 ? stageCompare : parsedA.Value.Sequence.CompareTo(parsedB.Value.Sequence);
    }

    private enum Stage
    {
        Edge,
        Preview,
        Nightly,
        Stable,
    }

    // Matches a numeric core (1-4 dotted segments) with an optional
    // "-nightly.N", "-edge.N" or "-preview.N" suffix (the dot and the number
    // are optional too) - the tag shapes the builds produce.
    private static readonly Regex TagPattern = new(
        @"(?<core>\d+(\.\d+){1,3})(-(?<stage>edge|nightly|preview)\.?(?<seq>\d+)?)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static (Version Core, Stage Stage, int Sequence)? Parse(string? tag)
    {
        if (tag is null)
        {
            return null;
        }

        var match = TagPattern.Match(tag);
        if (!match.Success || !Version.TryParse(match.Groups["core"].Value, out var core))
        {
            return null;
        }

        var stage = match.Groups["stage"].Value.ToLowerInvariant() switch
        {
            "edge" => Stage.Edge,
            "preview" => Stage.Preview,
            "nightly" => Stage.Nightly,
            _ => Stage.Stable,
        };
        return (core, stage, int.TryParse(match.Groups["seq"].Value, out var sequence) ? sequence : 0);
    }
}
