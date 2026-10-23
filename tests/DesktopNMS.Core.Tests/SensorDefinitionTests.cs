using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Sensors;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class SensorDefinitionTests
{
    [DefinitionsFeedFact]
    public async Task Published_catalogue_resolves_a_real_state_family_without_loading_other_systems()
    {
        using var fixture = new Installation();
        using var store = new SensorDefinitionStore(fixture.Paths);
        await store.ConfigureAsync(new() { Enabled = true });
        Assert.True(await store.SynchroniseAsync(new[] { "ios" }, force: true), store.Status);
        var sensor = new Sensor { SensorClass = "state", SensorType = "cswSwitchState", Oid = ".1.3.6.1.4.1.9.9.500.1.2.1.1.6.1", Current = 4 };
        var state = store.Resolve(sensor, "ios");
        Assert.NotNull(state);
        Assert.Equal("ready", state.Label);
        Assert.Equal(AlertSeverity.Ok, state.Severity);
        Assert.Equal(1, store.LoadedSystemCount);
        Assert.InRange(store.LoadedJsonBytes, 1, 2 * 1024 * 1024);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static byte[] Shard(string os = "example-os", string? generic = "ok", string label = "Ready") => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        sources = new[] { new { id = "fixture", kind = "reviewed-override", location = "https://example.net/definitions", revision = "fixture-1", licence = "MIT" } },
        definitions = new[] { new { id = os + "/fan", match = new { os, sensorClass = "state", sensorType = "exampleFanState", oidPrefix = "1.3.6.1.4.1.32473.1" },
            states = new[] { new { value = "2", label, genericState = generic } },
            sourceRefs = new[] { new { sourceId = "fixture", path = "fixture.json", symbol = "exampleFanState", suppliesSeverity = generic is not null } } } },
    });
    private static Sensor Reading(string? oid = ".1.3.6.1.4.1.32473.1.7", double? value = 2, int device = 12) => new()
    { SensorClass = "state", SensorType = "exampleFanState", Oid = oid, Current = value, DeviceId = device };

    [Fact]
    public void Exact_identity_and_integer_values_resolve_without_broad_prefix_matches()
    {
        var catalogue = SensorDefinitionCatalogue.Load(Shard(), "example-os");
        var resolved = catalogue.Resolve(Reading(), "example-os")!;
        Assert.Equal("Ready", resolved.Label);
        Assert.Equal(AlertSeverity.Ok, resolved.Severity);
        Assert.True(resolved.HasAuthoritativeSeverity);
        Assert.Null(catalogue.Resolve(Reading(), "other-os"));
        Assert.Null(catalogue.Resolve(Reading("1.3.6.1.4.1.32473.10.1"), "example-os"));
        Assert.Null(catalogue.Resolve(Reading(value: 2.1), "example-os"));
        Assert.Null(catalogue.Resolve(Reading(value: null), "example-os"));
        Assert.Null(catalogue.Resolve(Reading(value: double.NaN), "example-os"));
        Assert.Null(catalogue.Resolve(Reading(value: 9007199254740992d), "example-os"));
        Assert.Null(catalogue.Resolve(Reading().WithType("other"), "example-os"));
    }

    [Fact]
    public void Label_only_states_do_not_imply_a_healthy_severity()
    {
        var result = SensorDefinitionCatalogue.Load(Shard(generic: null)).Resolve(Reading(), "example-os")!;
        Assert.Equal("Ready", result.Label);
        Assert.Equal(AlertSeverity.Unknown, result.Severity);
        Assert.False(result.HasAuthoritativeSeverity);
    }

    [Theory]
    [InlineData("26.10.2")]
    [InlineData("26.10.2+catalogue.1")]
    [InlineData("26.10.2+catalogue.12")]
    public void Stable_catalogue_corrections_preserve_the_exact_asset_version(string version)
    {
        Assert.True(SensorDefinitionIndex.IsVersion(version));
        var catalogue = SensorDefinitionCatalogue.Load(Shard(), catalogueVersion: version);
        Assert.Equal(version, catalogue.Resolve(Reading(), "example-os")!.CatalogueVersion);
    }

    [Theory]
    [InlineData("26.10.2-rc1")]
    [InlineData("26.10.2+catalogue.0")]
    [InlineData("26.10.2+catalogue.01")]
    [InlineData("26.10.2+anything")]
    [InlineData("26.10.2/other")]
    public void Unsafe_and_prerelease_catalogue_versions_still_reject(string version)
        => Assert.False(SensorDefinitionIndex.IsVersion(version));

    [Fact]
    public void Unsupported_schema_duplicate_properties_and_wrong_os_reject()
    {
        Assert.Throws<InvalidDataException>(() => SensorDefinitionCatalogue.Load(Shard(), "other-os"));
        Assert.Throws<InvalidDataException>(() => SensorDefinitionCatalogue.Load(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Shard()).Replace("\"schemaVersion\":1", "\"schemaVersion\":2"))));
        Assert.Throws<InvalidDataException>(() => SensorDefinitionCatalogue.Load(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Shard()).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"))));
    }

    [Theory]
    [InlineData("http://github.com/example/repo")]
    [InlineData("https://token@github.com/example/repo")]
    [InlineData("https://github.com/example/repo/tree/main")]
    [InlineData("https://github.com/example/repo?token=secret")]
    [InlineData("https://example.net/owner/repo")]
    public void Repository_urls_do_not_accept_credentials_paths_or_unapproved_hosts(string value)
        => Assert.Throws<ArgumentException>(() => SensorDefinitionOptions.NormaliseRepository(value));

    [Fact]
    public void Platform_paths_stay_outside_app_bundles_and_appimage_mounts()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "definitions-path-fixture"));
        var mac = SensorDefinitionPaths.ForInstallation(Path.Combine(root, "DashyNMS.app", "Contents", "MacOS"), macOS: true);
        Assert.Equal(Path.Combine(root, "DashyNMS.sensor-definitions"), mac.DefinitionsDirectory);
        var linux = SensorDefinitionPaths.ForInstallation(Path.Combine(root, "mount"), appImage: Path.Combine(root, "DashyNMS.AppImage"));
        Assert.Equal(Path.Combine(root, "DashyNMS.sensor-definitions"), linux.DefinitionsDirectory);
        Assert.Equal(Path.Combine(root, "sensor-definitions"), SensorDefinitionPaths.ForInstallation(root).DefinitionsDirectory);
    }

    [Fact]
    public async Task Shards_load_on_demand_and_survive_restart_offline_until_explicit_disable()
    {
        using var fixture = new Installation();
        using var network = new Server();
        using (var store = fixture.Store(network))
        {
            await store.ConfigureAsync(new() { Enabled = true });
            Assert.True(await store.SynchroniseAsync(Array.Empty<string>()));
            Assert.Equal(0, store.LoadedSystemCount);
            Assert.DoesNotContain(network.Requests, path => path.EndsWith(".gz"));
            Assert.True(await store.SynchroniseAsync(new[] { "example-os" }));
            Assert.Equal("Ready", store.Resolve(Reading(), "example-os")!.Label);
            Assert.Equal(1, store.LoadedSystemCount);
        }
        network.Offline = true;
        using var restarted = fixture.Store(network);
        await restarted.InitialiseAsync();
        Assert.True(restarted.Options.Enabled);
        Assert.True(await restarted.SynchroniseAsync(new[] { "example-os" }));
        Assert.Equal("Ready", restarted.Resolve(Reading(), "example-os")!.Label);
        Assert.True(Directory.Exists(fixture.Paths.DefinitionsDirectory));
        await restarted.ConfigureAsync(new() { Enabled = false, CustomRepository = "https://github.com/example/custom-definitions" });
        Assert.False(Directory.Exists(fixture.Paths.DefinitionsDirectory));
        Assert.True(File.Exists(fixture.Paths.SettingsFile));
        Assert.Null(restarted.Resolve(Reading(), "example-os"));
        using var disabled = fixture.Store(network);
        await disabled.InitialiseAsync();
        Assert.False(disabled.Options.Enabled);
        Assert.Contains("custom-definitions", disabled.Options.CustomRepository);
    }

    [Fact]
    public async Task Unchanged_shards_are_reused_across_catalogue_release_versions()
    {
        using var fixture = new Installation();
        using var network = new Server();
        using var store = fixture.Store(network);
        await store.ConfigureAsync(new() { Enabled = true });
        Assert.True(await store.SynchroniseAsync(new[] { "example-os" }));
        var downloads = network.Requests.Count(path => path.EndsWith(".gz"));
        network.Version = "1.0.1";
        Assert.True(await store.SynchroniseAsync(force: true));
        Assert.Equal(downloads, network.Requests.Count(path => path.EndsWith(".gz")));
        Assert.Equal("1.0.1", store.Resolve(Reading(), "example-os")!.CatalogueVersion);
    }

    [Fact]
    public async Task Bad_downloads_keep_the_previous_selected_catalogue()
    {
        using var fixture = new Installation();
        using var network = new Server();
        using var store = fixture.Store(network);
        await store.ConfigureAsync(new() { Enabled = true });
        Assert.True(await store.SynchroniseAsync(new[] { "example-os" }));
        network.Version = "1.0.1";
        network.Label = "Changed";
        network.Corrupt = true;
        Assert.False(await store.SynchroniseAsync(force: true));
        Assert.Equal("Ready", store.Resolve(Reading(), "example-os")!.Label);
        Assert.Equal("1.0.0", store.Version);
    }

    [Fact]
    public async Task Source_changes_never_mix_official_and_custom_mappings()
    {
        using var fixture = new Installation();
        using var network = new Server();
        using var store = fixture.Store(network);
        await store.ConfigureAsync(new() { Enabled = true });
        await store.SynchroniseAsync(new[] { "example-os" });
        await store.ConfigureAsync(new() { Enabled = true, UseCustomRepository = true, CustomRepository = "https://github.com/example/custom" });
        Assert.Null(store.Resolve(Reading(), "example-os"));
        network.Label = "Custom label";
        await store.SynchroniseAsync(new[] { "example-os" });
        Assert.Equal("Custom label", store.Resolve(Reading(), "example-os")!.Label);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(fixture.Paths.DefinitionsDirectory, "sources")).Length);
        await store.ConfigureAsync(new() { Enabled = false });
        Assert.False(Directory.Exists(fixture.Paths.DefinitionsDirectory));
    }

    [Fact]
    public async Task Disabling_during_a_download_cannot_recreate_definition_data()
    {
        using var fixture = new Installation();
        using var network = new Server { BlockShard = true };
        using var store = fixture.Store(network);
        await store.ConfigureAsync(new() { Enabled = true });
        var update = store.SynchroniseAsync(new[] { "example-os" });
        await network.ShardRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disable = store.ConfigureAsync(new() { Enabled = false });
        network.ReleaseShard.SetResult();
        await disable.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await update);
        Assert.False(Directory.Exists(fixture.Paths.DefinitionsDirectory));
        Assert.False(store.Options.Enabled);
    }

    [Fact]
    public async Task A_fleet_larger_than_the_memory_cache_still_resolves_each_sensor()
    {
        using var fixture = new Installation();
        using var network = new Server { SystemCount = 20 };
        using var store = fixture.Store(network);
        await store.ConfigureAsync(new() { Enabled = true });
        var sensors = Enumerable.Range(0, 20).Select(id => Reading(device: id)).ToArray();
        await store.ApplyAsync(sensors, id => "example-os-" + id);
        Assert.All(sensors, sensor => Assert.Equal("Ready", sensor.StateDefinition!.Label));
        Assert.InRange(store.LoadedSystemCount, 1, 16);
        Assert.True(store.LoadedJsonBytes < 4 * 1024 * 1024);
        await store.ConfigureAsync(new() { Enabled = false });
        await store.ApplyAsync(sensors, id => "example-os-" + id);
        Assert.All(sensors, sensor => Assert.Null(sensor.StateDefinition));
    }

    private sealed class Installation : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "dashynms-definitions-" + Guid.NewGuid().ToString("N"));
        public SensorDefinitionPaths Paths { get; }
        public Installation() { Directory.CreateDirectory(_root); Paths = SensorDefinitionPaths.ForInstallation(_root); }
        public SensorDefinitionStore Store(Server server) => new(Paths, new HttpClient(server, disposeHandler: false));
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public async Task Bundled_readers_use_cached_data_and_observe_disable_without_writing_or_downloading()
    {
        using var fixture = new Installation();
        using var network = new Server();
        using var writer = fixture.Store(network);
        await writer.ConfigureAsync(new() { Enabled = true });
        await writer.SynchroniseAsync(new[] { "example-os" });
        using var reader = new SensorDefinitionStore(fixture.Paths, new HttpClient(network, disposeHandler: false), readOnly: true);
        var requestCount = network.Requests.Count;
        network.Offline = true;
        var sensors = new[] { Reading() };
        await reader.ApplyAsync(sensors, _ => "example-os");
        Assert.Equal("Ready", sensors[0].StateDefinition!.Label);
        Assert.Equal(requestCount, network.Requests.Count);
        await File.WriteAllBytesAsync(fixture.Paths.SettingsFile, JsonSerializer.SerializeToUtf8Bytes(new SensorDefinitionOptions(), Json));
        await reader.ApplyAsync(sensors, _ => "example-os");
        Assert.Null(sensors[0].StateDefinition);
        Assert.True(Directory.Exists(fixture.Paths.DefinitionsDirectory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ConfigureAsync(new() { Enabled = true }));
    }

    [NotWindowsFact]
    public async Task Disable_refuses_to_follow_a_symbolic_link_outside_the_definitions_root()
    {
        using var fixture = new Installation();
        using var network = new Server();
        using var store = fixture.Store(network);
        await store.ConfigureAsync(new() { Enabled = true });
        Directory.CreateDirectory(fixture.Paths.DefinitionsDirectory);
        var outside = Path.Combine(Path.GetDirectoryName(fixture.Paths.SettingsFile)!, "unrelated");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(marker, "Keep this file");
        Directory.CreateSymbolicLink(Path.Combine(fixture.Paths.DefinitionsDirectory, "link"), outside);
        await Assert.ThrowsAsync<IOException>(() => store.ConfigureAsync(new() { Enabled = false }));
        Assert.False(store.Options.Enabled);
        Assert.True(File.Exists(marker));
        Assert.Contains("cleanup", store.Status);
    }

    private sealed class Server : HttpMessageHandler
    {
        public string Version = "1.0.0";
        public string Label = "Ready";
        public bool Offline, Corrupt, BlockShard;
        public int SystemCount;
        public readonly List<string> Requests = new();
        public readonly TaskCompletionSource ShardRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseShard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        private static byte[] Compress(byte[] bytes) { using var buffer = new MemoryStream(); using (var zip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true)) zip.Write(bytes); return buffer.ToArray(); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (Offline) throw new HttpRequestException("Offline");
            var systems = SystemCount == 0 ? new[] { "example-os" } : Enumerable.Range(0, SystemCount).Select(id => "example-os-" + id).ToArray();
            var shards = systems.Select(os => { var raw = Shard(os, label: Label); var zip = Compress(raw); return new SensorDefinitionShard(os, Hash(zip), zip.Length, raw.Length, 1); }).ToArray();
            var notice = Encoding.UTF8.GetBytes("Synthetic fixture notices");
            byte[] bytes;
            if (path.EndsWith("stable.json")) bytes = JsonSerializer.SerializeToUtf8Bytes(new SensorDefinitionIndex { SchemaVersion = 1, CatalogueVersion = Version, Shards = shards, Notices = new(Hash(notice), notice.Length) }, Json);
            else if (path.EndsWith("notices.txt")) bytes = notice;
            else
            {
                if (BlockShard) { ShardRequested.TrySetResult(); await ReleaseShard.Task.ConfigureAwait(false); }
                var os = Path.GetFileName(path)[..^".json.gz".Length];
                bytes = Compress(Shard(os, label: Label));
                if (Corrupt) bytes[0] ^= 1;
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };
        }
    }
}

file static class SensorFixtureExtensions
{
    public static Sensor WithType(this Sensor sensor, string type) { sensor.SensorType = type; return sensor; }
}

file sealed class DefinitionsFeedFactAttribute : FactAttribute
{
    public DefinitionsFeedFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DASHYNMS_TEST_DEFINITIONS") != "1")
            Skip = "Set DASHYNMS_TEST_DEFINITIONS=1 to verify the real published public data feed.";
    }
}
