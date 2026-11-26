namespace DesktopNMS.Core.Demo;

/// <summary>
/// A stand-in LibreNMS for demo mode (#119): an example network that answers
/// the API the apps use, in LibreNMS's own shapes, without a server. A host
/// can carry it in the same process with <see cref="Connection"/>, or put it
/// behind a listener of its own.
/// </summary>
/// <remarks>
/// <para>
/// Names are under example.net and addresses are in the documentation ranges
/// (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24 and 2001:db8::/32), never
/// anything from a real network.
/// </para>
/// <para>
/// The API contract: paths under <c>/api/v0/</c> answer as LibreNMS 26.9 does,
/// with <c>{"status":"ok", …}</c> envelopes. <c>/faults</c> answers 404, as
/// 26.9 does, so Core reads the example alerts from <c>/alerts</c>. A missing
/// device, port, FDB or network entry is LibreNMS's 404 with its message.
/// Writes (anything but GET) are accepted and forgotten: nothing a demo does
/// changes it. Anything else gets an empty success, marked
/// <see cref="DemoResponse.Unhandled"/>. A path LibreNMS would reject, such as
/// a malformed id, answers 400, also marked unhandled.
/// </para>
/// <para>
/// The graph contract: <c>devices/{id}/graphs/…</c> and
/// <c>devices/{id}/ports/{port}/…</c> return SVG drawn as rrdtool draws it,
/// in LibreNMS's colours, at the width and height asked for and without the
/// legend for <c>legend=no</c>. Asked for with <c>graph_type=png&amp;output=base64</c>,
/// as the Intelligence graph lookup asks, the same graph comes as a PNG in
/// LibreNMS's envelope: <c>{"status":"ok","image":{"image":"…","content-type":"image/png"}}</c>.
/// The same request gives the same graph, and a device's traffic graph ends at
/// the rate its ports report.
/// </para>
/// <para>
/// It's built once and only read afterwards, so any number of requests may
/// be answered at once.
/// </para>
/// </remarks>
public sealed class DemoResponder
{
    /// <summary>The token a demo signs in with. It opens nothing anywhere else.</summary>
    public static string Token => "demo-token-not-a-real-one";

    /// <summary>The server version the example network reports.</summary>
    public static string ServerVersion => DemoFleet.ServerVersion;

    private readonly DemoFleet _fleet;

    public DemoResponder(DemoFleetOptions? options = null, TimeProvider? time = null)
    {
        var chosen = options ?? DemoFleetOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegative(chosen.ExtraDevices, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chosen.ExtraDevices, DemoFleetOptions.MaxExtraDevices, nameof(options));
        _fleet = new DemoFleet(chosen, time ?? TimeProvider.System);
    }

    /// <summary>
    /// The address an in-process demo signs in to. It's in the reserved
    /// example.net domain, so a request that somehow left the app would go
    /// nowhere; <see cref="DemoMessageHandler"/> never sends one.
    /// </summary>
    public static Uri Address { get; } = new("https://demo.example.net/");

    /// <summary>
    /// A connection to <see cref="Address"/> that this answers in the same
    /// process. Every client built for it - the connection test's included -
    /// answers from here and never from the network.
    /// </summary>
    public Api.LibreNmsConnection Connection(int timeoutSeconds = 30)
        => new Api.LibreNmsConnection(Address, Token, timeoutSeconds: timeoutSeconds).WithHandler(() => new DemoMessageHandler(this));

    /// <summary>The device a demo opens to show off a device's pages.</summary>
    public int FeaturedDeviceId => _fleet.FeaturedDeviceId;

    /// <summary>Devices a demo pins, by id and name.</summary>
    public IReadOnlyList<(int Id, string Name)> PinnedDevices => _fleet.PinnedDevices;

    /// <summary>Devices a demo lists as recently viewed, newest first.</summary>
    public IReadOnlyList<(int Id, string Name)> RecentlyViewed => _fleet.RecentlyViewed;

    /// <summary>Sensors a demo's dashboard shows.</summary>
    public IReadOnlyList<DemoPinnedSensor> PinnedSensors => _fleet.PinnedSensors
        .Select(s => new DemoPinnedSensor(s.SensorId, s.DeviceId, s.Class, s.Device, s.Description))
        .ToList();

    /// <summary>How many devices the network has, the extra ones included.</summary>
    public int DeviceCount => _fleet.DeviceCount;

    /// <summary>Answers one API request.</summary>
    /// <param name="method">"GET", "POST", "PUT", "PATCH" or "DELETE".</param>
    /// <param name="pathAndQuery">The request's path and query, such as "/api/v0/devices?type=all".</param>
    /// <param name="body">A write's body, which is read and forgotten.</param>
    public DemoResponse Respond(string method, string pathAndQuery, string? body = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        try
        {
            return _fleet.Handle(method.ToUpperInvariant(), pathAndQuery, body ?? string.Empty);
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or InvalidOperationException or OverflowException or ArgumentException)
        {
            // A path LibreNMS would turn down; a demo shouldn't fail over one.
            return new DemoResponse("{\"status\":\"error\",\"message\":\"Invalid request\"}", Status: 400, Unhandled: true);
        }
    }
}
