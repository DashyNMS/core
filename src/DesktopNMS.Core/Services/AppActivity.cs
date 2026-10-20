namespace DesktopNMS.Core.Services;

/// <summary>
/// Whether the app is in the background: no window on screen on desktop, or
/// not in the foreground on a phone. The device and sensor pollers slow down
/// while it is (desktop#52); the alert poller never does, since
/// notifications depend on it. Each front end supplies the implementation.
/// </summary>
public interface IAppActivity
{
    /// <summary>The app is in the background. Safe to read from any thread.</summary>
    bool IsInBackground { get; }

    /// <summary>Raised when <see cref="IsInBackground"/> changes, on whichever thread the front end raises it.</summary>
    event EventHandler? Changed;
}

/// <summary>How much the device and sensor pollers slow down in the background (desktop#52).</summary>
public static class AppActivityPolling
{
    /// <summary>Poll this many times less often - a 30-second setting becomes 2 minutes.</summary>
    public const int BackgroundSlowdown = 4;
}