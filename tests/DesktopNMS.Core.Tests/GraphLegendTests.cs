using DesktopNMS.Core.Graphs;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class GraphLegendTests
{
    [Theory]
    [InlineData("device_temperature", "temperature")]
    [InlineData("device_fanspeed", "fanspeed")]
    [InlineData("device_processor", null)]
    [InlineData("device_bits", null)]
    [InlineData("uptime", null)]
    public void Sensor_class_comes_from_the_graph_name(string graph, string? sensorClass)
        => Assert.Equal(sensorClass, GraphLegend.SensorClassOf(graph));

    [Fact]
    public void Sensors_are_one_class_in_description_order_with_units()
    {
        var sensors = new[]
        {
            new Sensor { SensorId = 3, SensorClass = "temperature", Description = "outlet", Current = 43 },
            new Sensor { SensorId = 1, SensorClass = "temperature", Description = "ASIC", Current = 66.25 },
            new Sensor { SensorId = 2, SensorClass = "fanspeed", Description = "Fan 1", Current = 3200 },
            new Sensor { SensorId = 4, SensorClass = "Temperature", Description = "Inlet", Current = 31 },
        };

        var legend = GraphLegend.ForSensors(sensors, "temperature");

        Assert.Equal(new[] { "ASIC", "Inlet", "outlet" }, legend.Select(e => e.Name));
        Assert.Equal(new int?[] { 1, 4, 3 }, legend.Select(e => e.SensorId));
        Assert.Equal("66.3 °C", legend[0].Value);
    }

    [Fact]
    public void Processors_are_in_stored_order()
    {
        var legend = GraphLegend.ForProcessors(new[]
        {
            new ProcessorSensor { ProcessorId = 9, Description = "Core 1", UsagePercent = 41.6 },
            new ProcessorSensor { ProcessorId = 7, Description = "Core 0", UsagePercent = 12 },
        });

        Assert.Equal(new[] { "Core 0", "Core 1" }, legend.Select(e => e.Name));
        Assert.Equal("42%", legend[1].Value);
    }

    [Fact]
    public void Traffic_sums_the_ports_LibreNMS_counts()
    {
        var legend = GraphLegend.ForTraffic(new[]
        {
            new Port { IfName = "Te1/1/1", IfType = "ethernetCsmacd", IfInOctetsRate = 250_000_000, IfOutOctetsRate = 50_000_000 },
            new Port { IfName = "Te1/1/2", IfType = "ethernetCsmacd", IfInOctetsRate = 150_000_000, IfOutOctetsRate = 87_500_000 },
            new Port { IfName = "Lo0", IfType = "softwareLoopback", IfInOctetsRate = 999_000_000 },
            new Port { IfName = "Vlan10", IfType = "propVirtual", IfInOctetsRate = 999_000_000 },
        });

        Assert.Equal(new[] { "In", "Out" }, legend.Select(e => e.Name));
        Assert.Equal("3.2 Gb/s", legend[0].Value);
        Assert.Equal("1.1 Gb/s", legend[1].Value);
    }
}

public sealed class GraphLegendAvailabilityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Missing_or_invalid_traffic_is_unavailable_or_explicitly_partial(double? rate)
    {
        var missing = new Port { IfInOctetsRate = rate, IfOutOctetsRate = rate };
        Assert.All(GraphLegend.ForTraffic([missing]), e => Assert.Equal("–", e.Value));
        var partial = GraphLegend.ForTraffic([missing, new Port { IfInOctetsRate = 125, IfOutOctetsRate = 0 }]);
        Assert.Equal(new[] { "In", "Out" }, partial.Select(e => e.Name));
        Assert.Equal(new[] { "1 kb/s (incomplete)", "0 b/s (incomplete)" }, partial.Select(e => e.Value));
        Assert.All(partial, e => { Assert.Null(e.Detail); Assert.Null(e.SensorId); });
    }

    [Fact]
    public void Empty_or_excluded_ports_do_not_imply_a_measured_zero()
    {
        Assert.All(GraphLegend.ForTraffic([]), e => Assert.Equal("–", e.Value));
        Assert.All(GraphLegend.ForTraffic([
            new Port { Deleted = true }, new Port { Disabled = true }, new Port { Ignore = true },
            new Port { IfType = "softwareLoopback" }, new Port { IfDescr = "Vlan10" }, new Port { IfName = "bond0" }
        ]), e => Assert.Equal("–", e.Value));
        Assert.All(GraphLegend.ForTraffic([new Port { IfInOctetsRate = 0, IfOutOctetsRate = 0 },
            new Port { Deleted = true }]), e => Assert.Equal("0 b/s", e.Value));
    }

    [Fact]
    public void Traffic_directions_have_independent_completeness()
    {
        var entries = GraphLegend.ForTraffic([
            new Port { IfInOctetsRate = 125, IfOutOctetsRate = 125 },
            new Port { IfInOctetsRate = null, IfOutOctetsRate = 125 }
        ]);
        Assert.Equal("1 kb/s (incomplete)", entries[0].Value);
        Assert.Equal("2 kb/s", entries[1].Value);
    }

    [Theory]
    [InlineData(double.MaxValue, 0d)]
    [InlineData(double.MaxValue, double.MaxValue)]
    [InlineData(1e307, 2e307)]
    public void Traffic_overflow_is_unavailable_without_affecting_other_direction(double first, double second)
    {
        var entries = GraphLegend.ForTraffic([
            new Port { IfInOctetsRate = first, IfOutOctetsRate = 125 },
            new Port { IfInOctetsRate = second, IfOutOctetsRate = 0 }, new Port()
        ]);
        Assert.Equal("–", entries[0].Value);
        Assert.Equal("1 kb/s (incomplete)", entries[1].Value);
    }

    [Theory]
    [InlineData(null, "–")]
    [InlineData(-0.1, "–")]
    [InlineData(100.1, "–")]
    [InlineData(double.NaN, "–")]
    [InlineData(double.PositiveInfinity, "–")]
    [InlineData(double.NegativeInfinity, "–")]
    [InlineData(0d, "0%")]
    [InlineData(100d, "100%")]
    [InlineData(41.6, "42%")]
    public void Processor_bounds_preserve_identity_and_valid_rounding(double? usage, string expected)
    {
        var entries = GraphLegend.ForProcessors([
            new ProcessorSensor { ProcessorId = 9, UsagePercent = usage },
            new ProcessorSensor { ProcessorId = 2, Description = "CPU", UsagePercent = 0 }
        ]);
        Assert.Equal(new[] { "CPU", "Processor 9" }, entries.Select(e => e.Name));
        Assert.Equal(expected, entries[1].Value);
        Assert.All(entries, e => { Assert.Null(e.SensorId); Assert.Null(e.Detail); });
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Port_counter_series_reject_unavailable_rates_but_keep_names(double? value)
    {
        var port = new Port { IfInOctetsRate = value, IfOutOctetsRate = value,
            IfInUcastPktsRate = value, IfOutUcastPktsRate = value, IfInErrorsRate = value, IfOutErrorsRate = value };
        foreach (var (type, names) in new[] {
            ("port_bits", new[] { "In", "Out" }), ("port_upkts", new[] { "In", "Out" }),
            ("port_errors", new[] { "Errors in", "Errors out" }) })
        {
            var entries = GraphLegend.ForPort(type, names, port);
            Assert.Equal(names, entries.Select(e => e.Name));
            Assert.All(entries, e => Assert.Equal("–", e.Value));
        }
        Assert.Equal("–", GraphLegend.FormatRate(value));
        Assert.Equal("–", GraphLegend.FormatPerSecond(value));
    }

    [Fact]
    public void Port_conversion_overflow_and_unreported_series_remain_distinct()
    {
        var entries = GraphLegend.ForPort("port_bits", ["In", "Out", "Other"],
            new Port { IfInOctetsRate = double.MaxValue, IfOutOctetsRate = 0 });
        Assert.Equal(new string?[] { "–", "0 b/s", null }, entries.Select(e => e.Value));
        Assert.Null(GraphLegend.ForPort("port_nupkts", ["In"], new Port())[0].Value);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ifInOctets_rate\":null,\"ifOutOctets_rate\":null}")]
    public void Nullable_port_model_does_not_default_missing_readings_to_zero(string json)
    {
        var port = System.Text.Json.JsonSerializer.Deserialize<Port>(json)!;
        Assert.Null(port.IfInOctetsRate);
        Assert.Null(port.IfOutOctetsRate);
        Assert.All(GraphLegend.ForTraffic([port]), e => Assert.Equal("–", e.Value));
        var processor = System.Text.Json.JsonSerializer.Deserialize<ProcessorSensor>("{}")!;
        Assert.Null(processor.UsagePercent);
        Assert.Equal("–", GraphLegend.ForProcessors([processor])[0].Value);
    }

    [Fact]
    public void Valid_port_values_keep_units_rounding_and_series_order()
    {
        var port = new Port { IfInOctetsRate = 150, IfOutOctetsRate = 0,
            IfInUcastPktsRate = 0.02, IfOutUcastPktsRate = 840,
            IfInErrorsRate = 0, IfOutErrorsRate = 1200 };
        Assert.Equal(new[] { "1.2 kb/s", "0 b/s" },
            GraphLegend.ForPort("port_bits", ["In", "Out"], port).Select(e => e.Value));
        Assert.Equal(new[] { "0.02/s", "840/s" },
            GraphLegend.ForPort("port_upkts", ["In", "Out"], port).Select(e => e.Value));
        Assert.Equal(new[] { "0/s", "1.2k/s" },
            GraphLegend.ForPort("port_errors", ["Errors in", "Errors out"], port).Select(e => e.Value));
        var processor = System.Text.Json.JsonSerializer.Deserialize<ProcessorSensor>("{\"processor_usage\":null}")!;
        Assert.Null(processor.UsagePercent);
        Assert.Equal("–", GraphLegend.ForProcessors([processor])[0].Value);
    }

    [Theory]
    [InlineData("en-GB")]
    [InlineData("fr-FR")]
    public void Valid_rate_units_and_sensor_signs_keep_existing_formats(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            Assert.Equal("1.2 kb/s", GraphLegend.FormatRate(1200));
            Assert.Equal("0.02/s", GraphLegend.FormatPerSecond(0.02));
            Assert.Equal("840/s", GraphLegend.FormatPerSecond(840));
            Assert.Equal("1.2k/s", GraphLegend.FormatPerSecond(1200));
            Assert.Equal("0/s", GraphLegend.FormatPerSecond(0));
            Assert.Equal("-12.5 °C", GraphLegend.FormatSensor(-12.5, "temperature"));
            Assert.Equal("-20 dBm", GraphLegend.FormatSensor(-20, "dbm"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
}
