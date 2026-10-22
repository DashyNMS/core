using System.Net;
using System.Net.Http;
using DesktopNMS.Core.Api;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class TransientRetryPolicyTests
{
    [Fact]
    public void A_request_timeout_does_not_repeat_the_wait_or_trigger_failover()
    {
        var timeout = new LibreNmsApiException("Timed out") { IsTimeout = true };
        Assert.False(TransientRetryPolicy.IsTransientFailure(timeout));
        Assert.False(ServerFailover.IsUnreachable(timeout));
        Assert.True(TransientRetryPolicy.IsTransientFailure(new LibreNmsApiException("Connection lost")));
    }

    [Theory]
    [InlineData("resources/sensors", 30, 120)]
    [InlineData("resources/sensors?columns=sensor_id", 30, 120)]
    [InlineData("resources/sensors", 180, 180)]
    [InlineData("devices", 30, 30)]
    [InlineData("devices/1/health", 5, 5)]
    public void Only_the_fleet_sensor_list_gets_a_longer_timeout(string route, int configured, int expected)
        => Assert.Equal(TimeSpan.FromSeconds(expected), LibreNmsTransport.RequestTimeout(route, configured));

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public void IsRetryable_allows_idempotent_methods(string method)
    {
        Assert.True(TransientRetryPolicy.IsRetryable(new HttpMethod(method)));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public void IsRetryable_excludes_everything_else(string method)
    {
        Assert.False(TransientRetryPolicy.IsRetryable(new HttpMethod(method)));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void IsTransientFailure_true_for_server_struggling_status_codes(HttpStatusCode statusCode)
    {
        var ex = new LibreNmsApiException("failed", statusCode);
        Assert.True(TransientRetryPolicy.IsTransientFailure(ex));
    }

    [Fact]
    public void IsTransientFailure_true_for_no_status_code_at_all()
    {
        // Timeouts and connection-level failures never got an HTTP response.
        var ex = new LibreNmsApiException("timed out");
        Assert.True(TransientRetryPolicy.IsTransientFailure(ex));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void IsTransientFailure_false_for_definite_responses(HttpStatusCode statusCode)
    {
        var ex = new LibreNmsApiException("failed", statusCode);
        Assert.False(TransientRetryPolicy.IsTransientFailure(ex));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ComputeBackoffDelay_stays_within_the_jittered_range(int attempt)
    {
        var baseMs = 500.0 * Math.Pow(2, attempt - 1);

        // Repeated to exercise the random jitter component, not just one draw.
        for (var i = 0; i < 50; i++)
        {
            var delay = TransientRetryPolicy.ComputeBackoffDelay(attempt);

            Assert.True(delay.TotalMilliseconds >= baseMs / 2, $"attempt {attempt}: {delay.TotalMilliseconds}ms was below the floor of {baseMs / 2}ms");
            Assert.True(delay.TotalMilliseconds <= baseMs, $"attempt {attempt}: {delay.TotalMilliseconds}ms exceeded the ceiling of {baseMs}ms");
        }
    }

    [Fact]
    public void ComputeBackoffDelay_grows_with_attempt_number()
    {
        // Attempt 1's range is (250, 500]ms and attempt 3's is [1000, 2000]ms -
        // they never overlap, so this holds every single time despite the
        // jitter, confirming the backoff is exponential rather than flat.
        for (var i = 0; i < 50; i++)
        {
            var first = TransientRetryPolicy.ComputeBackoffDelay(1);
            var third = TransientRetryPolicy.ComputeBackoffDelay(3);

            Assert.True(third > first, $"attempt 3 ({third.TotalMilliseconds}ms) was not greater than attempt 1 ({first.TotalMilliseconds}ms)");
        }
    }
}
