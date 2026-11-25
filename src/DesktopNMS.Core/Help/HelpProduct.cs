namespace DesktopNMS.Core.Help;

/// <summary>A DashyNMS product with a user guide, and where that guide is public (#96).</summary>
/// <param name="Id">A short, stable name: "desktop", "mobile" or "mcp".</param>
/// <param name="Name">What people call it.</param>
/// <param name="Repository">Its GitHub repository, as "owner/name".</param>
public sealed record HelpProduct(string Id, string Name, string Repository)
{
    /// <summary>Where every DashyNMS repository keeps its guide. The wiki is copied from here.</summary>
    public const string GuideFolder = "docs/guide";

    public static HelpProduct Desktop { get; } = new("desktop", "DashyNMS Desktop", "DashyNMS/desktop");

    public static HelpProduct Mobile { get; } = new("mobile", "DashyNMS Mobile", "DashyNMS/mobile");

    public static HelpProduct Mcp { get; } = new("mcp", "DashyNMS MCP", "DashyNMS/mcp");

    public static IReadOnlyList<HelpProduct> All { get; } = [Desktop, Mobile, Mcp];

    /// <summary>The public wiki page that shows <paramref name="pageId"/>, for people outside the app.</summary>
    public Uri WikiAddress(string pageId, string? anchor = null)
        => new($"https://github.com/{Repository}/wiki/{Uri.EscapeDataString(pageId)}{(anchor is null ? string.Empty : "#" + Uri.EscapeDataString(anchor))}");
}
