using System.Text.Json;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Graphs;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class SensorReadingTests
{
    [Theory]
    [InlineData("nan")]
    [InlineData("-nan")]
    [InlineData("inf")]
    [InlineData("-inf")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("")]
    [InlineData("1e999")]
    public void Non_finite_or_missing_readings_and_limits_do_not_fail_the_list(string value)
    {
        var row = new Dictionary<string, object?>
        {
            ["sensor_current"] = value, ["sensor_limit"] = value, ["sensor_limit_warn"] = value,
            ["sensor_limit_low"] = value, ["sensor_limit_low_warn"] = value,
        };
        var json = "[{\"sensor_id\":1,\"sensor_current\":\"24.5\"}," + JsonSerializer.Serialize(row) + "]";
        var sensors = JsonSerializer.Deserialize<Sensor[]>(json, LibreNmsJson.Options)!;
        Assert.Equal(24.5, sensors[0].Current);
        Assert.True(sensors[0].HasReading);
        Assert.Null(sensors[1].Current);
        Assert.False(sensors[1].HasReading);
        Assert.Null(sensors[1].LimitHigh);
        Assert.Null(sensors[1].LimitHighWarn);
        Assert.Null(sensors[1].LimitLow);
        Assert.Null(sensors[1].LimitLowWarn);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_numbers_have_no_legend_value_or_threshold_severity(double value)
    {
        Assert.Equal("–", GraphLegend.FormatRate(value));
        Assert.Equal("–", GraphLegend.FormatPerSecond(value));
        Assert.Equal("–", GraphLegend.FormatSensor(value, "temperature"));
        IThresholdEvaluator evaluator = new BandThresholdSettings();
        Assert.Equal(AlertSeverity.Unknown, evaluator.Evaluate(value));
        Assert.Equal(AlertSeverity.Unknown, evaluator.Evaluate((double?)value));
        Assert.Equal(AlertSeverity.Unknown, evaluator.Evaluate(null));
        Assert.Equal(AlertSeverity.Unknown, new DbmThresholdSettings().Evaluate(value));
    }

    [Fact]
    public void Unknown_classes_use_only_the_api_unit_and_absent_readings_have_no_unit()
    {
        var sensor = JsonSerializer.Deserialize<Sensor>("""{"sensor_class":"example","sensor_current":"2.5","sensor_unit":"widgets"}""", LibreNmsJson.Options)!;
        Assert.Equal("2.5 widgets", Assert.Single(GraphLegend.ForSensors(new[] { sensor }, "example")).Value);
        Assert.Equal("2.5", GraphLegend.FormatSensor(sensor.Current, "example"));
        Assert.Equal("–", GraphLegend.FormatSensor(null, "temperature", "°C"));
        Assert.Equal("–", GraphLegend.FormatRate(null));
        Assert.Equal("–", GraphLegend.FormatPerSecond(null));
    }

    [Fact]
    public void Non_finite_readings_can_be_serialised_safely_as_null()
    {
        var json = JsonSerializer.Serialize(new Sensor { Current = double.NaN, LimitHigh = double.PositiveInfinity }, LibreNmsJson.Options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("sensor_current").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("sensor_limit").ValueKind);
    }
}
