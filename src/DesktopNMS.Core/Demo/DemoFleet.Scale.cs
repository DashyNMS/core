using System.Globalization;

namespace DesktopNMS.Core.Demo;

/// <summary>
/// The opt-in extra switches (<see cref="DemoFleetOptions.ExtraDevices"/>), so
/// an app can be measured against a server the size of a large network without
/// one. Nothing here runs for the default network.
/// </summary>
internal sealed partial class DemoFleet
{
    public int DeviceCount => _devices.Count;

    private void AddScaledDevices()
    {
        if (_options.ExtraDevices == 0)
        {
            return;
        }

        var random = new Random(_options.Seed);
        var sites = _locations.Keys.ToList();
        for (var i = 1; i <= _options.ExtraDevices; i++)
        {
            // One in twenty-five down, one in a hundred disabled: enough to fill every filter.
            var roll = random.Next(100);
            var up = roll >= 4;
            var disabled = roll == 99;
            var load = up ? 0.05 + random.NextDouble() * 0.8 : 0;
            _devices.Add(new ExampleDevice(
                _devices.Count + 1,
                "lab-sw-" + i.ToString("00000", CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"2001:db8:{i / 0xFFFF:x}:{i % 0xFFFF:x}::1"),
                "iosxe",
                "C9300-48P",
                "17.9.5",
                "network",
                sites[i % sites.Count],
                load,
                up ? 0.05 + random.NextDouble() * 0.6 : 0,
                up ? 0.2 + random.NextDouble() * 0.6 : 0,
                up,
                disabled,
                "LAB" + i.ToString("000000", CultureInfo.InvariantCulture)));
            AddPortsFor(_devices[^1]);
        }
    }

    /// <summary>Their sensors, neighbours, events and alerts, once the default network's are in place.</summary>
    private void AddScaledActivity()
    {
        if (_options.ExtraDevices == 0)
        {
            return;
        }

        var random = new Random(_options.Seed + 1);
        var scaled = _devices.Where(d => d.Name.StartsWith("lab-sw-", StringComparison.Ordinal)).ToList();
        var portsByDevice = _ports.GroupBy(p => p.DeviceId).ToDictionary(g => g.Key, g => g.ToDictionary(p => p.Name));
        for (var n = 0; n < scaled.Count; n++)
        {
            var d = scaled[n];
            // One switch in fifty runs hot, so Health and the sensor alerts have something to show.
            AddScaledSensor(d, "temperature", "Inlet", n % 50 == 0 ? 74 : 28 + random.Next(0, 30), 70, 60);
            AddScaledSensor(d, "fanspeed", "Fan 1", 3000 + random.Next(0, 3000), 9000, 8000, 1000, 1500);

            // A chain, each switch's second uplink to the next one's first.
            if (n + 1 < scaled.Count && d.Up && scaled[n + 1].Up)
            {
                var next = scaled[n + 1];
                AddScaledLink(d, portsByDevice[d.Id]["Te1/1/2"], next, portsByDevice[next.Id]["Te1/1/1"]);
                AddScaledLink(next, portsByDevice[next.Id]["Te1/1/1"], d, portsByDevice[d.Id]["Te1/1/2"]);
            }

            _events.Add(new
            {
                event_id = 9000 + _events.Count,
                device_id = d.Id,
                datetime = Stamp(_now.AddMinutes(-(15 + random.Next(0, 60 * 24 * 7)))),
                message = d.Up ? "Gi1/0/12: ifOperStatus changed to up" : "Device status changed to Down from icmp check.",
                type = d.Up ? "interface" : "system",
                username = (string?)null,
                severity = d.Up ? 1 : 5,
                hostname = d.Name,
                sysName = d.Name,
            });

            if (!d.Up || n % 40 == 0)
            {
                var ruleId = d.Up ? 3 : 1;
                var rule = _rules[ruleId - 1];
                _alerts.Add(new
                {
                    id = 500 + _alerts.Count,
                    device_id = d.Id,
                    rule_id = ruleId,
                    state = n % 7 == 0 ? 2 : 1,
                    alerted = 1,
                    open = 1,
                    note = string.Empty,
                    timestamp = Stamp(_now.AddMinutes(-(5 + random.Next(0, 600)))),
                    info = string.Empty,
                    hostname = d.Name,
                    severity = (string)rule.GetType().GetProperty("severity")!.GetValue(rule)!,
                    name = (string)rule.GetType().GetProperty("name")!.GetValue(rule)!,
                    proc = (string?)null,
                    notes = string.Empty,
                });
            }
        }
    }

    private void AddScaledSensor(ExampleDevice d, string cls, string descr, double current, double high, double highWarn, double? low = null, double? lowWarn = null)
        => _sensors.Add(new
        {
            sensor_id = 1000 + _sensors.Count,
            device_id = d.Id,
            sensor_class = cls,
            sensor_descr = descr,
            sensor_index = (_sensors.Count + 1).ToString(CultureInfo.InvariantCulture),
            sensor_current = d.Up ? current : (double?)null,
            sensor_limit = (double?)high,
            sensor_limit_warn = (double?)highWarn,
            sensor_limit_low = low,
            sensor_limit_low_warn = lowWarn,
            lastupdate = Stamp(_now.AddMinutes(-1)),
        });

    private void AddScaledLink(ExampleDevice local, ExamplePort localPort, ExampleDevice remote, ExamplePort remotePort)
        => _links.Add(new
        {
            id = _links.Count + 1,
            local_device_id = local.Id,
            local_port_id = localPort.Id,
            remote_port_id = (int?)remotePort.Id,
            remote_port = remotePort.Name,
            remote_hostname = remote.Name + ".example.net",
            remote_device_id = remote.Id,
            remote_platform = remote.Hardware,
            protocol = "lldp",
            remote_version = $"{remote.Hardware}, {remote.Version}",
            active = true,
        });
}
