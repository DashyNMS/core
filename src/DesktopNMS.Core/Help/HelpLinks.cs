namespace DesktopNMS.Core.Help;

/// <summary>Where a link in a page goes.</summary>
public abstract record HelpLink;

/// <summary>Another page, or a heading on this one: open it inside the app.</summary>
/// <param name="Anchor">The heading to go to, or null for the top. An anchor the page doesn't have is dropped.</param>
public sealed record HelpPageLink(string PageId, string? Anchor) : HelpLink;

/// <summary>An address outside the guide: open it in the browser or email app.</summary>
public sealed record HelpExternalLink(Uri Address) : HelpLink;

/// <summary>A link that goes nowhere the app should open: a page that isn't there, a path outside the repository or an unsafe address.</summary>
public sealed record HelpBrokenLink(string Target) : HelpLink;

/// <summary>An image the guide holds.</summary>
public sealed record HelpImage(string Path, byte[] Content);
