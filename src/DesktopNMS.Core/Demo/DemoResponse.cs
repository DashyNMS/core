namespace DesktopNMS.Core.Demo;

/// <summary>What <see cref="DemoResponder"/> answers a request with.</summary>
/// <param name="Body">JSON in LibreNMS's envelope, or an SVG graph.</param>
/// <param name="ContentType">"application/json" or "image/svg+xml".</param>
/// <param name="Status">The HTTP status LibreNMS would send: 200; 404 for something the example network hasn't got; 400 or 422 for a request LibreNMS would reject.</param>
/// <param name="Unhandled">True when the demo had no answer of its own: an empty success for a path it has no data for, or a 400 for one it couldn't read. A host can log what's missing.</param>
public sealed record DemoResponse(string Body, string ContentType = "application/json", int Status = 200, bool Unhandled = false);

/// <summary>A sensor a demo pins to its dashboard.</summary>
public sealed record DemoPinnedSensor(int SensorId, int DeviceId, string SensorClass, string DeviceName, string Description);
