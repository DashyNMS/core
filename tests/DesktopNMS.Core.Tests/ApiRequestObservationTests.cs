using System.Net;
using System.Text.Json;
using DesktopNMS.Core.Api;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class ApiRequestObservationTests
{
    [Theory]
    [InlineData("devices?hostname=private-host-canary", ApiOperation.DeviceList)]
    [InlineData("alerts?device_id=123", ApiOperation.Alerts)]
    [InlineData("resources/sensors?token=private-token-canary", ApiOperation.Sensors)]
    [InlineData("devices/123/ports", ApiOperation.Other)]
    public async Task Callback_has_only_fixed_metadata(string route, ApiOperation category)
    {
        ApiRequestMeasurement? point = null;
        var result = await ApiRequestObservation.Observe(route, p => point = p, () => Task.FromResult(42), default);
        Assert.Equal(42, result);
        Assert.NotNull(point);
        Assert.Equal(category, point.Operation);
        Assert.Equal(ApiRequestOutcome.Success, point.Outcome);
        Assert.Equal(ApiResponseClass.Success, point.ResponseClass);
        Assert.True(point.Duration >= TimeSpan.Zero);
        var json = JsonSerializer.Serialize(point);
        Assert.DoesNotContain("private-", json);
        Assert.Equal(new[] { "Duration", "Operation", "Outcome", "ResponseClass" },
            JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, ApiRequestOutcome.ClientError, ApiResponseClass.ClientError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ApiRequestOutcome.ServerError, ApiResponseClass.ServerError)]
    public async Task Http_failures_preserve_the_original_exception(HttpStatusCode status, ApiRequestOutcome outcome, ApiResponseClass responseClass)
    {
        var error = new LibreNmsApiException("private-message-canary", status);
        ApiRequestMeasurement? point = null;
        var thrown = await Assert.ThrowsAsync<LibreNmsApiException>(() => ApiRequestObservation.Observe<int>("alerts", p => point = p,
            () => Task.FromException<int>(error), default));
        Assert.Same(error, thrown);
        Assert.Equal(outcome, point!.Outcome);
        Assert.Equal(responseClass, point.ResponseClass);
        Assert.DoesNotContain("private-message-canary", JsonSerializer.Serialize(point));
    }

    [Fact]
    public async Task Cancellation_timeout_and_network_failure_are_distinct()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        foreach (var (error, token, expected) in new (Exception, CancellationToken, ApiRequestOutcome)[]
        {
            (new OperationCanceledException(), cancelled.Token, ApiRequestOutcome.Cancelled),
            (new LibreNmsApiException("timeout") { IsTimeout = true }, default, ApiRequestOutcome.Timeout),
            (new LibreNmsApiException("network", innerException: new HttpRequestException()), default, ApiRequestOutcome.NetworkFailure),
        })
        {
            ApiRequestMeasurement? point = null;
            var thrown = await Record.ExceptionAsync(() => ApiRequestObservation.Observe<int>("alerts", p => point = p,
                () => Task.FromException<int>(error), token));
            Assert.Same(error, thrown);
            Assert.Equal(expected, point!.Outcome);
        }
    }

    [Fact]
    public void Writes_to_devices_are_not_classified_as_device_list_reads()
    {
        Assert.Equal(ApiOperation.Other, ApiRequestObservation.Classify("devices", HttpMethod.Post));
    }

    [Fact]
    public async Task Observer_failure_cannot_replace_success_or_failure()
    {
        Assert.Equal(42, await ApiRequestObservation.Observe("devices", _ => throw new Exception(), () => Task.FromResult(42), default));
        var error = new LibreNmsApiException("original");
        var thrown = await Assert.ThrowsAsync<LibreNmsApiException>(() => ApiRequestObservation.Observe<int>("devices", _ => throw new Exception(),
            () => Task.FromException<int>(error), default));
        Assert.Same(error, thrown);
        Assert.Equal(42, await ApiRequestObservation.Observe("devices", null, () => Task.FromResult(42), default));
    }
}
