namespace DesktopNMS.Core.Updates;

/// <summary>
/// Which builds the app is offered. Saved in settings by name, so a value is
/// never renamed.
/// </summary>
public enum UpdateChannel
{
    /// <summary>Finished releases only.</summary>
    Stable = 0,

    /// <summary>Also the nightly builds, and the previews they replaced.</summary>
    Nightly = 1,

    /// <summary>Also a build of every commit.</summary>
    Edge = 2,
}
