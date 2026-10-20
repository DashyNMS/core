using System.Net;

namespace DesktopNMS.Core.Api;

/// <summary>How one source of data turned out (#9) - so "none", "not allowed" and "couldn't load" never read alike.</summary>
public enum SourceOutcome
{
    /// <summary>Loaded, with something in it.</summary>
    Loaded,

    /// <summary>Loaded, and there's nothing this account can see.</summary>
    Empty,

    /// <summary>LibreNMS refused this account (403).</summary>
    Denied,

    /// <summary>Anything else went wrong - the server, the network, a timeout.</summary>
    Failed,
}

/// <summary>
/// Totals as what they are (#9): LibreNMS 26.10 shows each account only the
/// devices, alerts and graphs it may see, so a count is "visible to this
/// account", never the whole network's. Shared by the app's own wording and
/// what Intelligence is told.
/// </summary>
public static class VisibleTotals
{
    /// <param name="count">How many came back, if the request succeeded.</param>
    /// <param name="error">What it failed with, if it did.</param>
    public static SourceOutcome Classify(int? count, Exception? error) => error switch
    {
        LibreNmsApiException { IsPermissionDenied: true } => SourceOutcome.Denied,
        not null => SourceOutcome.Failed,
        _ when count is null or 0 => SourceOutcome.Empty,
        _ => SourceOutcome.Loaded,
    };

    /// <summary>"12 devices visible to this account", "No alerts visible to this account", "This account isn't allowed to see sensors", "Devices couldn't be loaded".</summary>
    /// <param name="noun">Plural, lower case: "devices".</param>
    /// <param name="singular">For a count of one: "device". Defaults to <paramref name="noun"/> without its final "s".</param>
    public static string Describe(SourceOutcome outcome, int count, string noun, string? singular = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noun);

        var one = singular ?? (noun.EndsWith('s') ? noun[..^1] : noun);
        return outcome switch
        {
            SourceOutcome.Loaded when count > 0 => $"{count:N0} {(count == 1 ? one : noun)} visible to this account",
            SourceOutcome.Loaded or SourceOutcome.Empty => $"No {noun} visible to this account",
            SourceOutcome.Denied => $"This account isn't allowed to see {noun}",
            _ => char.ToUpperInvariant(noun[0]) + noun[1..] + " couldn't be loaded",
        };
    }
}

/// <summary>Reading LibreNMS's graph responses (#9).</summary>
public static class GraphImages
{
    /// <summary>
    /// LibreNMS answers a graph this account may not see with HTTP 200 and an
    /// error image reading "No Authorization" ("No Auth" when small), not a
    /// 403. True for that SVG.
    /// </summary>
    public static bool IsDenied(string? svg)
        => svg is not null
           && svg.Contains("<foreignObject", StringComparison.OrdinalIgnoreCase)
           && (svg.Contains(">No Authorization<", StringComparison.Ordinal) || svg.Contains(">No Auth<", StringComparison.Ordinal));

    /// <summary>Throws a 403 for a denied graph, so it shows as not allowed rather than as a broken graph.</summary>
    public static void ThrowIfDenied(string? svg)
    {
        if (IsDenied(svg))
        {
            throw new LibreNmsApiException("This account isn't allowed to see this graph.", HttpStatusCode.Forbidden, "No Authorization");
        }
    }
}
