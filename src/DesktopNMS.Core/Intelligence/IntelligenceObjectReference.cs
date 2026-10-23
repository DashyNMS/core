namespace DesktopNMS.Core.Intelligence;

/// <summary>A network object's identity in the current chat's server/account scope.</summary>
public sealed record IntelligenceObjectReference(string Kind, int Id, int? DeviceId = null, int? RuleId = null)
{
    public bool IsValid => Id > 0 && Kind is "device" or "group" or "location" or "sensor" or "alert" or "fault" or "port"
        && (Kind is not ("sensor" or "alert" or "fault" or "port") || DeviceId is > 0);
}
