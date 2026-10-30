namespace DesktopNMS.Core.Spotlights;

/// <summary>
/// What a Spotlight entry points at. Intelligence keeps a conversation to
/// any of these; a dashboard card (desktop#552) only to the first three,
/// the ones that stand for a set of devices.
/// </summary>
/// <remarks>
/// The numbers are what <see cref="DesktopNMS.Core.Intelligence.SavedChatSpotlight.Kind"/>
/// has always stored, so they mustn't change. A dashboard card stores the
/// kind as text instead (<see cref="SpotlightTarget.KindName"/>), so a kind
/// a newer app adds can't fail the whole settings file (see desktop#197).
/// </remarks>
public enum SpotlightKind
{
    Device = 0,
    Group = 1,
    Location = 2,
    Sensor = 3,
    Alert = 4,
    Fault = 5,
    Port = 6,
}
