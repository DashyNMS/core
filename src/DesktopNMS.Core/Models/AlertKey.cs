using System.Globalization;

namespace DesktopNMS.Core.Models;

/// <summary>Which kind of row an alert id belongs to.</summary>
public enum AlertSource
{
    /// <summary>A rule/device row from the alerts table - LibreNMS before 26.10.</summary>
    Alert,

    /// <summary>A per-entity row from alert_faults - LibreNMS 26.10 and later.</summary>
    Fault,
}

/// <summary>
/// An alert's identity across sessions: its id and which kind of row it is.
/// LibreNMS 26.10 numbers faults afresh, so the old alert 12 and fault 12
/// can be unrelated (#3) - anything kept between polls or restarts, or sent
/// out and acted on later (a notification's button), carries this rather
/// than the bare id.
/// </summary>
public readonly record struct AlertKey(AlertSource Source, int Id)
{
    /// <summary>"fault:12" or "alert:12" - stable, for saving and round-tripping.</summary>
    public override string ToString()
        => (Source == AlertSource.Fault ? "fault:" : "alert:") + Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads <see cref="ToString"/>'s form. A bare number is a legacy alert id.</summary>
    public static bool TryParse(string? text, out AlertKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var source = AlertSource.Alert;
        var colon = trimmed.IndexOf(':');
        if (colon >= 0)
        {
            switch (trimmed[..colon])
            {
                case "alert":
                    break;
                case "fault":
                    source = AlertSource.Fault;
                    break;
                default:
                    return false;
            }

            trimmed = trimmed[(colon + 1)..];
        }

        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return false;
        }

        key = new AlertKey(source, id);
        return true;
    }
}
