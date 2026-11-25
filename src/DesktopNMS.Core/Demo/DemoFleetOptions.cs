namespace DesktopNMS.Core.Demo;

/// <summary>
/// Which example network <see cref="DemoResponder"/> answers for. The default
/// is the small network the apps' demos show and their screenshots are taken of.
/// </summary>
/// <param name="ExtraDevices">
/// Switches added to the default network, each with its ports, sensors,
/// neighbours, events and the odd alert, for measuring how an app copes with
/// a large server (DashyNMS/mobile#132). Their addresses are in IPv6's
/// documentation range, as IPv4's three ranges hold too few.
/// </param>
/// <param name="Seed">Makes the extra devices' readings and states; the same seed gives the same network.</param>
public sealed record DemoFleetOptions(int ExtraDevices = 0, int Seed = 2026)
{
    /// <summary>More than any test needs, so a mistaken count can't exhaust memory.</summary>
    public const int MaxExtraDevices = 20_000;

    public static DemoFleetOptions Default { get; } = new();
}
