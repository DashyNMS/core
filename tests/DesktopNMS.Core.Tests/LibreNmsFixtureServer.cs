using System.Net;
using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Json;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// Answers API requests from the fixtures in Fixtures/LibreNMS (#10), by URL
/// prefix - including LibreNMS's error envelopes, thrown as the transport would.
/// </summary>
internal sealed class LibreNmsFixtureServer : ILibreNmsTransport
{
    private readonly List<(string Prefix, string File, HttpStatusCode Status)> _routes = new();

    public LibreNmsConnection? Connection { get; } = new(new Uri("https://nms.example.net/"), "token");

    public ApiPermissions Permissions { get; } = new();

    ApiPermissions? ILibreNmsTransport.Permissions => Permissions;

    public List<string> Requests { get; } = new();

    /// <summary>Answers requests starting with <paramref name="prefix"/> from <paramref name="file"/>; the first match wins.</summary>
    public LibreNmsFixtureServer Serve(string prefix, string file, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((prefix, file, status));
        return this;
    }

    public Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default)
    {
        Requests.Add(relativeUrl);
        using var document = Answer(relativeUrl);
        var items = document.RootElement.TryGetProperty(collectionProperty, out var array)
            ? array.Deserialize<List<T>>(LibreNmsJson.Options)!
            : new List<T>();
        return Task.FromResult<IReadOnlyList<T>>(items);
    }

    public Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default)
    {
        Requests.Add($"{method} {relativeUrl}");
        return Task.FromResult(Answer(relativeUrl));
    }

    public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private JsonDocument Answer(string relativeUrl)
    {
        var route = _routes.FirstOrDefault(r => relativeUrl.StartsWith(r.Prefix, StringComparison.Ordinal));
        if (route.File is null)
        {
            throw new InvalidOperationException("No fixture for " + relativeUrl);
        }

        var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LibreNMS", route.File)));
        if (route.Status == HttpStatusCode.OK)
        {
            return document;
        }

        using (document)
        {
            var message = document.RootElement.TryGetProperty("message", out var text) ? text.GetString() : null;
            throw new LibreNmsApiException(message ?? "failed", route.Status, message);
        }
    }
}
