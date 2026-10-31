using System.Text.Json;

namespace DesktopNMS.Core.Api;

/// <summary>
/// The thin HTTP layer the resource APIs sit on. Kept separate so new resource
/// APIs (ports, services, health, ...) can be added without touching transport,
/// and so they can be unit tested against a fake.
/// </summary>
public interface ILibreNmsTransport
{
    /// <summary>The connection currently in use, or null if not signed in.</summary>
    LibreNmsConnection? Connection { get; }

    /// <summary>
    /// Issues a request and returns the parsed envelope. Throws
    /// <see cref="LibreNmsApiException"/> for transport, HTTP and API errors.
    /// The caller owns the returned document.
    /// </summary>
    Task<JsonDocument> SendAsync(
        HttpMethod method,
        string relativeUrl,
        object? body = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a GET and deserialises the named collection property out of the
    /// envelope, e.g. "alerts" from {"status":"ok","alerts":[...]}.
    /// </summary>
    Task<IReadOnlyList<T>> GetCollectionAsync<T>(
        string relativeUrl,
        string collectionProperty,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a GET and returns the raw response body, unparsed - for an
    /// endpoint whose response isn't LibreNMS's usual JSON envelope (e.g. an
    /// SVG graph). Still gets the same connection/timeout/transient-retry
    /// handling as <see cref="SendAsync"/>; only the "parse a JSON envelope"
    /// step is skipped, since there is no envelope to parse.
    /// </summary>
    Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a static file from under the LibreNMS web root (not the API),
    /// such as <c>images/logos/hpe.svg</c>, with the same timeout, retry and
    /// backup-address handling as the API calls. Returns null when the server
    /// has no such file: LibreNMS answers a missing one with a redirect to its
    /// login page rather than a 404, so a redirect, a 404, a body that isn't
    /// an image or one over <paramref name="maxBytes"/> all count as missing.
    /// Throws <see cref="LibreNmsApiException"/> when the server can't be
    /// asked at all. A transport that can't fetch files (a test fake) has none.
    /// </summary>
    /// <param name="webPath">A path relative to the web root, made only of plain segments - no leading slash and no "..".</param>
    Task<WebFile?> GetWebFileAsync(string webPath, int maxBytes, CancellationToken cancellationToken = default)
        => Task.FromResult<WebFile?>(null);

    /// <summary>What this connection has learned it can't do (#51, #9) - null for a transport that doesn't track it, such as a test fake.</summary>
    ApiPermissions? Permissions => null;
}

/// <summary>A static file fetched from the LibreNMS web root - see <see cref="ILibreNmsTransport.GetWebFileAsync"/>.</summary>
/// <param name="Content">The body, as sent.</param>
/// <param name="ContentType">The media type the server gave it, if any (image/svg+xml).</param>
public sealed record WebFile(byte[] Content, string? ContentType);
