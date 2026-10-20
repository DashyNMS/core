namespace DesktopNMS.Core.Services;

/// <summary>
/// Whether the app is signed in to a LibreNMS server right now - all the
/// shared monitors and caches need to know before they poll. A front end's
/// own session service implements it (desktop's ISessionService extends it).
/// </summary>
public interface IConnectionState
{
    bool IsConnected { get; }
}