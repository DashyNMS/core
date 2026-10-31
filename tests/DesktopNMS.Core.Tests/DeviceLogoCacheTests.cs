using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Always in a temporary folder, against a fake server - never the real cache.</summary>
public sealed class DeviceLogoCacheTests : IDisposable
{
    private const string Svg = """<?xml version="1.0"?><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"/>""";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dashynms-logos-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTime _time = new(DateTimeOffset.UtcNow);
    private readonly FakeLogoServer _server = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private DeviceLogoCache Cache(ILibreNmsTransport? transport = null)
        => new(transport ?? _server, NullLogger<DeviceLogoCache>.Instance, _folder, _time);

    private string[] Logos() => Directory.Exists(_folder) ? Directory.GetFiles(_folder) : [];

    [Fact]
    public async Task A_logo_is_resolved_in_LibreNMSs_own_order()
    {
        _server.Files["images/os/hpe.png"] = Png;

        var logo = await Cache().GetAsync("hpe.svg");

        Assert.NotNull(logo);
        Assert.Equal(DeviceLogoFormat.Png, logo.Format);
        Assert.Equal(Png, File.ReadAllBytes(logo.FilePath));
        Assert.Equal(["images/logos/hpe.svg", "images/logos/hpe.png", "images/os/hpe.svg", "images/os/hpe.png"], _server.Requests);
    }

    [Fact]
    public async Task A_device_without_a_logo_gets_the_generic_one_and_keeps_it()
    {
        _server.Files["images/os/generic.svg"] = Encoding.UTF8.GetBytes(Svg);
        var cache = Cache();

        var first = await cache.GetAsync("nothing.svg");
        _server.Requests.Clear();
        var second = await cache.GetAsync("nothing.svg");

        Assert.Equal(DeviceLogoFormat.Svg, first!.Format);
        Assert.Equal(first, second);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_login_page_where_the_logo_should_be_is_not_taken_for_one()
    {
        _server.Files["images/logos/hpe.svg"] = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Login</body></html>");
        _server.Files["images/os/hpe.svg"] = Encoding.UTF8.GetBytes(Svg);

        var logo = await Cache().GetAsync("hpe.svg");

        Assert.Equal(Svg, File.ReadAllText(logo!.FilePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../x.svg")]
    [InlineData("a/b.svg")]
    [InlineData("a\\b.svg")]
    [InlineData("x.exe")]
    [InlineData("x..svg")]
    [InlineData("hpe")]
    public async Task Unsafe_or_missing_names_are_refused_without_asking_the_server(string? icon)
    {
        _server.Files["images/os/generic.svg"] = Encoding.UTF8.GetBytes(Svg);

        Assert.Null(await Cache().GetAsync(icon));
        Assert.Empty(_server.Requests);
        Assert.Empty(Logos());
    }

    [Fact]
    public async Task The_twenty_first_logo_evicts_the_least_recently_used()
    {
        var cache = Cache();
        for (var i = 0; i < DeviceLogoCache.MaxFiles; i++)
        {
            _server.Files[$"images/logos/os{i}.svg"] = Encoding.UTF8.GetBytes(Svg);
            await cache.GetAsync($"os{i}.svg");
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        // A hit on the oldest protects it; the next oldest goes instead.
        await cache.GetAsync("os0.svg");
        _time.Advance(TimeSpan.FromMinutes(1));
        _server.Files["images/logos/new.svg"] = Encoding.UTF8.GetBytes(Svg);
        await cache.GetAsync("new.svg");

        var names = Logos().Select(Path.GetFileName).ToList();
        Assert.Equal(DeviceLogoCache.MaxFiles, names.Count);
        Assert.Contains(names, n => n!.Contains("~os0.svg~", StringComparison.Ordinal));
        Assert.Contains(names, n => n!.Contains("~new.svg~", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n!.Contains("~os1.svg~", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_and_damaged_files_are_fetched_again()
    {
        _server.Files["images/logos/hpe.svg"] = Encoding.UTF8.GetBytes(Svg);
        _server.Files["images/logos/aruba.png"] = Png;
        var cache = Cache();
        var svg = await cache.GetAsync("hpe.svg");
        var png = await cache.GetAsync("aruba.png");

        File.WriteAllBytes(svg!.FilePath, []);
        File.WriteAllText(png!.FilePath, "<html>gateway error</html>");
        _server.Requests.Clear();

        Assert.Equal(Svg, File.ReadAllText((await cache.GetAsync("hpe.svg"))!.FilePath));
        Assert.Equal(Png, File.ReadAllBytes((await cache.GetAsync("aruba.png"))!.FilePath));
        Assert.Equal(["images/logos/hpe.svg", "images/logos/aruba.svg", "images/logos/aruba.png"], _server.Requests);
        Assert.Equal(2, Logos().Length);
    }

    [Fact]
    public async Task A_half_written_temporary_file_is_ignored_and_cleared()
    {
        Directory.CreateDirectory(_folder);
        var server = DeviceLogoCache.ServerKey(_server.Connection!.WebRoot);
        var abandoned = Path.Combine(_folder, $"{server}~hpe.svg~20260101.svg.0123.tmp");
        File.WriteAllText(abandoned, "<svg");
        File.SetLastWriteTimeUtc(abandoned, _time.GetUtcNow().UtcDateTime.AddHours(-1));
        _server.Files["images/logos/hpe.svg"] = Encoding.UTF8.GetBytes(Svg);

        var logo = await Cache().GetAsync("hpe.svg");

        Assert.Equal(Svg, File.ReadAllText(logo!.FilePath));
        Assert.False(File.Exists(abandoned));
        Assert.Single(Logos());
    }

    [Fact]
    public async Task Two_views_asking_at_once_make_one_request()
    {
        _server.Files["images/logos/hpe.svg"] = Encoding.UTF8.GetBytes(Svg);
        _server.Gate = new TaskCompletionSource();
        var cache = Cache();

        var first = cache.GetAsync("hpe.svg");
        var second = cache.GetAsync("hpe.svg");
        _server.Gate.SetResult();

        Assert.Equal(await first, await second);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task A_cancelled_caller_gets_nothing_but_the_logo_is_still_kept()
    {
        _server.Files["images/logos/hpe.svg"] = Encoding.UTF8.GetBytes(Svg);
        _server.Gate = new TaskCompletionSource();
        var cache = Cache();
        using var cancel = new CancellationTokenSource();

        var pending = cache.GetAsync("hpe.svg", cancel.Token);
        cancel.Cancel();
        Assert.Null(await pending);

        _server.Gate.SetResult();
        var later = await cache.GetAsync("hpe.svg");
        Assert.NotNull(later);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task Two_servers_do_not_share_logos()
    {
        var other = new FakeLogoServer(new Uri("https://nms2.example.net/"));
        _server.Files["images/logos/hpe.svg"] = Encoding.UTF8.GetBytes(Svg);
        other.Files["images/logos/hpe.svg"] = Encoding.UTF8.GetBytes(Svg.Replace("10 10", "20 20", StringComparison.Ordinal));

        var mine = await Cache().GetAsync("hpe.svg");
        var theirs = await Cache(other).GetAsync("hpe.svg");

        Assert.NotEqual(mine!.FilePath, theirs!.FilePath);
        Assert.Single(other.Requests);
        Assert.Equal(2, Logos().Length);
    }

    [Fact]
    public async Task An_old_logo_is_fetched_again_and_replaced()
    {
        _server.Files["images/logos/hpe.svg"] = Encoding.UTF8.GetBytes(Svg);
        var cache = Cache();
        await cache.GetAsync("hpe.svg");

        _time.Advance(DeviceLogoCache.MaxAge + TimeSpan.FromDays(1));
        var changed = Svg.Replace("10 10", "30 30", StringComparison.Ordinal);
        _server.Files["images/logos/hpe.svg"] = Encoding.UTF8.GetBytes(changed);
        _server.Requests.Clear();
        var logo = await cache.GetAsync("hpe.svg");

        Assert.Equal(changed, File.ReadAllText(logo!.FilePath));
        Assert.Single(_server.Requests);
        Assert.Single(Logos());
    }

    [Fact]
    public async Task An_old_logo_is_still_used_while_the_server_cannot_be_reached()
    {
        _server.Files["images/logos/hpe.svg"] = Encoding.UTF8.GetBytes(Svg);
        var cache = Cache();
        var first = await cache.GetAsync("hpe.svg");

        _time.Advance(DeviceLogoCache.MaxAge + TimeSpan.FromDays(1));
        _server.Unreachable = true;

        Assert.Equal(first, await cache.GetAsync("hpe.svg"));
    }

    [Fact]
    public async Task Nothing_is_cached_and_nothing_thrown_when_the_server_cannot_be_reached()
    {
        _server.Unreachable = true;

        Assert.Null(await Cache().GetAsync("hpe.svg"));
        Assert.Empty(Logos());
    }

    [Fact]
    public async Task A_transport_that_cannot_fetch_files_has_no_logos()
    {
        Assert.Null(await Cache(new LibreNmsFixtureServer()).GetAsync("hpe.svg"));
    }

    [Theory]
    [InlineData("images/logos/hpe.svg", true)]
    [InlineData("images/os/generic.svg", true)]
    [InlineData("/images/logos/hpe.svg", false)]
    [InlineData("images/../api/v0/devices", false)]
    [InlineData("https://elsewhere.example.net/x.svg", false)]
    [InlineData("images//x.svg", false)]
    public void Only_plain_paths_under_the_web_root_are_fetched(string path, bool allowed)
        => Assert.Equal(allowed, LibreNmsTransport.IsPlainWebPath(path));

    [Fact]
    public async Task The_transport_reads_an_image_from_the_web_root_not_the_API()
    {
        using var server = new LoopbackLibreNms("HTTP/1.1 200 OK", "image/svg+xml", Svg);
        using var transport = Transport(server);

        var file = await transport.GetWebFileAsync("images/logos/hpe.svg", DeviceLogoCache.MaxBytes);

        Assert.Equal(Svg, Encoding.UTF8.GetString(file!.Content));
        Assert.Equal("image/svg+xml", file.ContentType);
        Assert.StartsWith("GET /images/logos/hpe.svg ", server.RequestLines.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_transport_takes_a_login_redirect_for_no_file_without_following_it()
    {
        using var server = new LoopbackLibreNms("HTTP/1.1 302 Found", "text/html", "", location: "/login");
        using var transport = Transport(server);

        Assert.Null(await transport.GetWebFileAsync("images/logos/hpe.svg", DeviceLogoCache.MaxBytes));
        Assert.Single(server.RequestLines);
    }

    [Fact]
    public async Task The_transport_takes_a_web_page_for_no_file()
    {
        using var server = new LoopbackLibreNms("HTTP/1.1 200 OK", "text/html; charset=UTF-8", "<html></html>");
        using var transport = Transport(server);

        Assert.Null(await transport.GetWebFileAsync("images/logos/hpe.svg", DeviceLogoCache.MaxBytes));
    }

    [Fact]
    public async Task The_transport_drops_a_file_over_the_size_limit()
    {
        using var server = new LoopbackLibreNms("HTTP/1.1 200 OK", "image/png", new string('x', 2048));
        using var transport = Transport(server);

        Assert.Null(await transport.GetWebFileAsync("images/logos/big.png", 1024));
    }

    private static LibreNmsTransport Transport(LoopbackLibreNms server)
    {
        var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance) { RetryTransientFailures = false };
        transport.Configure(new LibreNmsConnection(new Uri($"http://127.0.0.1:{server.Port}/"), "token"));
        return transport;
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>A server whose missing files come back as nothing, as the transport reports LibreNMS's login redirect.</summary>
    private sealed class FakeLogoServer(Uri? webRoot = null) : ILibreNmsTransport
    {
        public LibreNmsConnection? Connection { get; } = new(webRoot ?? new Uri("https://nms.example.net/"), "token");

        public Dictionary<string, byte[]> Files { get; } = new();

        public List<string> Requests { get; } = new();

        public TaskCompletionSource? Gate { get; set; }

        public bool Unreachable { get; set; }

        public async Task<WebFile?> GetWebFileAsync(string webPath, int maxBytes, CancellationToken cancellationToken = default)
        {
            lock (Requests)
            {
                Requests.Add(webPath);
            }

            if (Gate is { } gate)
            {
                await gate.Task;
            }

            if (Unreachable)
            {
                throw new LibreNmsApiException("Could not connect to the server.");
            }

            return Files.TryGetValue(webPath, out var content) ? new WebFile(content, null) : null;
        }

        public Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>A loopback web server that answers every request the same way.</summary>
    private sealed class LoopbackLibreNms : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _response;

        public LoopbackLibreNms(string statusLine, string contentType, string body, string? location = null)
        {
            var bytes = Encoding.UTF8.GetByteCount(body);
            _response = $"{statusLine}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes}\r\n"
                + (location is null ? string.Empty : $"Location: {location}\r\n")
                + $"Connection: close\r\n\r\n{body}";
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        public int Port { get; }

        public List<string> RequestLines { get; } = new();

        public void Dispose() => _listener.Stop();

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer);
                    var request = Encoding.ASCII.GetString(buffer, 0, read);
                    lock (RequestLines)
                    {
                        RequestLines.Add(request.Split("\r\n")[0]);
                    }

                    await stream.WriteAsync(Encoding.UTF8.GetBytes(_response));
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
            }
        }
    }
}
