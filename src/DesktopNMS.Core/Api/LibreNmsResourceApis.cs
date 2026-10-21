namespace DesktopNMS.Core.Api;

/// <summary>
/// Core's real resource APIs - URLs, parsing, LibreNMS's quirks - over any
/// <see cref="ILibreNmsTransport"/>. For a host's tests (the MCP server's,
/// the phone app's) to answer from recorded JSON while still exercising
/// Core's own requests, without Core sharing its internals with each of
/// them. <see cref="LibreNmsClient"/> builds the same set over its HTTP
/// transport.
/// </summary>
public sealed class LibreNmsResourceApis
{
    public LibreNmsResourceApis(ILibreNmsTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        Alerts = new AlertsApi(transport);
        Rules = new AlertRulesApi(transport);
        AlertTemplates = new AlertTemplatesApi(transport);
        Devices = new DevicesApi(transport);
        Sensors = new SensorsApi(transport);
        Ports = new PortsApi(transport);
        Links = new LinksApi(transport);
        Health = new DeviceHealthApi(transport);
        Fdb = new FdbApi(transport);
        Arp = new ArpApi(transport);
        Vlans = new VlansApi(transport);
        DeviceGroups = new DeviceGroupsApi(transport);
        Locations = new LocationsApi(transport);
        Routing = new RoutingApi(transport);
        PollerGroups = new PollerGroupsApi(transport);
        Logs = new LogsApi(transport);
        System = new SystemApi(transport);
        Graphs = new GraphsApi(transport);
    }

    public IAlertsApi Alerts { get; }

    public IAlertRulesApi Rules { get; }

    public IAlertTemplatesApi AlertTemplates { get; }

    public IDevicesApi Devices { get; }

    public ISensorsApi Sensors { get; }

    public IPortsApi Ports { get; }

    public ILinksApi Links { get; }

    public IDeviceHealthApi Health { get; }

    public IFdbApi Fdb { get; }

    public IArpApi Arp { get; }

    public IVlansApi Vlans { get; }

    public IDeviceGroupsApi DeviceGroups { get; }

    public ILocationsApi Locations { get; }

    public IRoutingApi Routing { get; }

    public IPollerGroupsApi PollerGroups { get; }

    public ILogsApi Logs { get; }

    public ISystemApi System { get; }

    public IGraphsApi Graphs { get; }
}
