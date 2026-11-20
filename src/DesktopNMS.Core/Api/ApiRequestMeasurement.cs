using System.Diagnostics;

namespace DesktopNMS.Core.Api;

public enum ApiOperation { DeviceList, Alerts, Sensors, Other }
public enum ApiRequestOutcome { Success, ClientError, ServerError, Timeout, NetworkFailure, Cancelled, Failed }
public enum ApiResponseClass { None, Success, Redirect, ClientError, ServerError, Other }

/// <summary>A logical request after retries; contains no request data or exception text.</summary>
public sealed record ApiRequestMeasurement(ApiOperation Operation, TimeSpan Duration,
    ApiRequestOutcome Outcome, ApiResponseClass ResponseClass);

internal static class ApiRequestObservation
{
    /// <summary>
    /// The fixed operation a request is measured under. LibreNMS 26.10 polls
    /// alerts as "faults" (and probes "faults?state=-1"), so those count as
    /// <see cref="ApiOperation.Alerts"/> too: otherwise 26.10 servers' alert
    /// polling would read as "other" and couldn't be compared with older ones.
    /// Only GETs are classified: a write (acknowledging, unmuting) is never
    /// polling, so any other method is <see cref="ApiOperation.Other"/>. The
    /// query string is ignored, so filters never split a category.
    /// </summary>
    internal static ApiOperation Classify(string relativeUrl, HttpMethod? method = null) => method is not null && method != HttpMethod.Get
        ? ApiOperation.Other : relativeUrl.Split('?')[0].Trim('/') switch
    {
        "devices" => ApiOperation.DeviceList,
        "alerts" or "faults" => ApiOperation.Alerts,
        "resources/sensors" => ApiOperation.Sensors,
        _ => ApiOperation.Other,
    };

    internal static Task<T> Observe<T>(string relativeUrl, Action<ApiRequestMeasurement>? observer,
        Func<Task<T>> request, CancellationToken cancellationToken, HttpMethod? method = null)
        => observer is null ? request() : Measure(Classify(relativeUrl, method), observer, request, cancellationToken);

    private static async Task<T> Measure<T>(ApiOperation operation, Action<ApiRequestMeasurement> observer,
        Func<Task<T>> request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = ApiRequestOutcome.Failed;
        var responseClass = ApiResponseClass.None;
        try
        {
            var result = await request().ConfigureAwait(false);
            outcome = ApiRequestOutcome.Success;
            responseClass = ApiResponseClass.Success;
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? ApiRequestOutcome.Cancelled : ApiRequestOutcome.Timeout;
            throw;
        }
        catch (LibreNmsApiException error)
        {
            responseClass = error.StatusCode is { } status ? ((int)status / 100) switch
            {
                2 => ApiResponseClass.Success, 3 => ApiResponseClass.Redirect,
                4 => ApiResponseClass.ClientError, 5 => ApiResponseClass.ServerError, _ => ApiResponseClass.Other,
            } : ApiResponseClass.None;
            outcome = error.IsTimeout ? ApiRequestOutcome.Timeout : responseClass switch
            {
                ApiResponseClass.ClientError => ApiRequestOutcome.ClientError,
                ApiResponseClass.ServerError => ApiRequestOutcome.ServerError,
                _ when error.LooksLikeStaleConnection || error.InnerException is HttpRequestException => ApiRequestOutcome.NetworkFailure,
                _ => ApiRequestOutcome.Failed,
            };
            throw;
        }
        finally
        {
            // Observability must never replace a request result or its original error.
            try { observer(new(operation, Stopwatch.GetElapsedTime(started), outcome, responseClass)); }
            catch (Exception) { }
        }
    }
}
