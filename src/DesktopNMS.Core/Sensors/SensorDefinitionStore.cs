using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Sensors;

/// <summary>
/// One installation writer with bounded, lazy OS shard readers. No account data or executable content is stored here.
/// </summary>
public sealed class SensorDefinitionStore : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly bool _readOnly;
    private readonly string _installRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource _requests = new();
    private SensorDefinitionOptions _options = new();
    private SensorDefinitionIndex? _index;
    private readonly Dictionary<string, Entry> _memory = new(StringComparer.Ordinal);
    private long _generation;
    private long _access;
    private DateTimeOffset _lastCheck;
    private bool _disposed;
    private sealed record Entry(SensorDefinitionCatalogue Catalogue, string Hash, int Bytes, long Access);

    public SensorDefinitionStore(SensorDefinitionPaths paths, HttpClient? http = null, bool readOnly = false)
    {
        Paths = new(Path.GetFullPath(paths.SettingsFile), Path.GetFullPath(paths.DefinitionsDirectory));
        _installRoot = Path.GetDirectoryName(Paths.SettingsFile)!;
        if (Path.GetDirectoryName(Paths.DefinitionsDirectory) != _installRoot)
            throw new ArgumentException("Definition data and its installation settings must share an installation directory.");
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = http is null;
        _readOnly = readOnly;
    }

    public SensorDefinitionPaths Paths { get; }
    public SensorDefinitionOptions Options { get { lock (_sync) return _options; } }
    public string Status { get; private set; } = "Sensor definitions are off.";
    public string? Version { get { lock (_sync) return _index?.CatalogueVersion; } }
    public DateTimeOffset? LastSuccessfulCheck => _lastCheck == default ? null : _lastCheck;
    public int LoadedSystemCount { get { lock (_sync) return _memory.Count; } }
    public long LoadedJsonBytes { get { lock (_sync) return _memory.Values.Sum(entry => (long)entry.Bytes); } }
    /// <summary>Raised on the updating thread; UI consumers marshal to their own dispatcher.</summary>
    public event EventHandler? Changed;

    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureSafe(Paths.SettingsFile);
            if (!File.Exists(Paths.SettingsFile)) return;
            var bytes = await ReadFileBoundedAsync(Paths.SettingsFile, 8192, cancellationToken).ConfigureAwait(false);
            var options = SensorDefinitionCatalogue.Read<SensorDefinitionOptions>(bytes);
            ValidateOptions(options);
            lock (_sync)
            {
                if (_options.Repository != options.Repository || _options.Enabled != options.Enabled)
                { _memory.Clear(); _index = null; _lastCheck = default; }
                _options = options;
            }
            if (!options.Enabled)
            {
                if (!_readOnly) await ConfigureAsync(options, cancellationToken).ConfigureAwait(false);
                else Status = "Sensor definitions are off.";
                return;
            }
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await LoadIndexAsync(options, cancellationToken).ConfigureAwait(false); }
            finally { _gate.Release(); }
            Status = Version is null ? "No sensor definitions downloaded yet." : "Using cached sensor definitions " + Version + ".";
        }
        catch (Exception ex) when (Expected(ex)) { Status = "Couldn't load installation sensor definitions: " + ex.Message; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void ValidateOptions(SensorDefinitionOptions options)
    {
        _ = options.Repository;
        if (!string.IsNullOrWhiteSpace(options.CustomRepository)) _ = SensorDefinitionOptions.NormaliseRepository(options.CustomRepository);
    }

    public async Task ConfigureAsync(SensorDefinitionOptions options, CancellationToken cancellationToken = default)
    {
        if (_readOnly) throw new InvalidOperationException("This process only reads installation sensor definitions.");
        ValidateOptions(options);
        long configuration;
        lock (_sync)
        {
            _requests.Cancel();
            _requests.Dispose();
            _requests = new();
            _generation++;
            configuration = _generation;
            _options = options;
            _memory.Clear();
            _index = null;
            _lastCheck = default;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync) { if (configuration != _generation) return; }
            // Persist off before cleanup, so even a failed deletion cannot enable the next launch.
            EnsureSafe(Paths.SettingsFile);
            await WriteAtomicAsync(Paths.SettingsFile, JsonSerializer.SerializeToUtf8Bytes(options, SensorDefinitionCatalogue.Json), cancellationToken).ConfigureAwait(false);
            if (!options.Enabled)
            {
                EnsureSafe(Paths.DefinitionsDirectory);
                if (Directory.Exists(Paths.DefinitionsDirectory))
                {
                    CheckTreeForLinks(Paths.DefinitionsDirectory);
                    Directory.Delete(Paths.DefinitionsDirectory, recursive: true);
                }
                Status = "Sensor definitions are off; downloaded definitions have been removed.";
            }
            else
            {
                await LoadIndexAsync(options, cancellationToken).ConfigureAwait(false);
                Status = Version is null ? "Enabled; update sensor definitions to download the catalogue." : "Using cached sensor definitions " + Version + ".";
            }
        }
        catch (Exception ex) when (Expected(ex))
        {
            Status = options.Enabled ? "Couldn't save or load installation definitions: " + ex.Message
                : "Sensor definitions are off, but installation settings or cleanup could not be completed: " + ex.Message;
            throw new IOException(Status, ex);
        }
        finally { _gate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    private string SourceDirectory(SensorDefinitionOptions options) => Path.Combine(Paths.DefinitionsDirectory, "sources", options.SourceKey);
    private string ObjectPath(SensorDefinitionOptions options, string hash) => Path.Combine(SourceDirectory(options), "objects", hash + ".gz");

    private async Task LoadIndexAsync(SensorDefinitionOptions options, CancellationToken token)
    {
        var path = Path.Combine(SourceDirectory(options), "index.json");
        EnsureSafe(path);
        if (!File.Exists(path)) return;
        var index = SensorDefinitionIndex.Load(await ReadFileBoundedAsync(path, 1024 * 1024, token).ConfigureAwait(false));
        lock (_sync) _index = index;
        var checkedPath = Path.Combine(SourceDirectory(options), "checked.txt");
        EnsureSafe(checkedPath);
        if (File.Exists(checkedPath) && DateTimeOffset.TryParse(await File.ReadAllTextAsync(checkedPath, token).ConfigureAwait(false), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var last) && last <= DateTimeOffset.UtcNow) _lastCheck = last;
    }

    /// <summary>Update only requested systems; unchanged hashes reuse disk objects. No whole-catalogue load occurs.</summary>
    public async Task<bool> SynchroniseAsync(IEnumerable<string>? systems = null, bool force = false, CancellationToken cancellationToken = default)
    {
        SensorDefinitionOptions options;
        long generation;
        CancellationToken requests;
        lock (_sync) { options = _options; generation = _generation; requests = _requests.Token; }
        if (!options.Enabled) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requests);
        var token = linked.Token;
        var acquired = false;
        var changed = false;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            CheckCurrent(options, generation, token);
            SensorDefinitionIndex? previous;
            string[] wanted;
            lock (_sync)
            {
                previous = _index;
                wanted = (systems ?? _memory.Keys.ToArray()).Where(SensorDefinitionIndex.IsOs).Distinct(StringComparer.Ordinal).ToArray();
            }
            if (_readOnly && previous is null) return false;
            var refresh = !_readOnly && (force || previous is null || DateTimeOffset.UtcNow - _lastCheck >= TimeSpan.FromHours(24));
            var indexBytes = refresh ? await DownloadAsync(options.Feed, 1024 * 1024, token).ConfigureAwait(false) : null;
            var index = indexBytes is null ? previous! : SensorDefinitionIndex.Load(indexBytes);
            var staged = new List<(string Os, SensorDefinitionCatalogue Catalogue, string Hash, int Bytes)>();
            foreach (var os in wanted)
            {
                var shard = index.Shards.SingleOrDefault(shard => shard.Os == os);
                if (shard is null) continue;
                lock (_sync)
                    if (_memory.TryGetValue(os, out var cached) && cached.Hash == shard.Sha256)
                    {
                        staged.Add((os, cached.Catalogue.ForRelease(index.CatalogueVersion), cached.Hash, cached.Bytes));
                        continue;
                    }
                var path = ObjectPath(options, shard.Sha256);
                EnsureSafe(path);
                var compressed = File.Exists(path) ? await ReadFileBoundedAsync(path, 524288, token).ConfigureAwait(false) : null;
                if (_readOnly && compressed is null) continue;
                if (!_readOnly && (compressed is null || !Matches(compressed, shard.Sha256, shard.DownloadBytes)))
                    compressed = await DownloadAsync(options.Asset(index.CatalogueVersion, "os/" + os + ".json.gz"), shard.DownloadBytes, token).ConfigureAwait(false);
                if (compressed is null || !Matches(compressed, shard.Sha256, shard.DownloadBytes)) throw new InvalidDataException("OS definitions checksum or size does not match.");
                var raw = await ExpandAsync(compressed, shard.ExpandedBytes, token).ConfigureAwait(false);
                var catalogue = SensorDefinitionCatalogue.Load(raw, os, index.CatalogueVersion);
                if (catalogue.Definitions.Length != shard.Definitions)
                    throw new InvalidDataException("OS definitions do not match the release index.");
                CheckCurrent(options, generation, token);
                if (!_readOnly) await WriteAtomicAsync(path, compressed, token).ConfigureAwait(false);
                staged.Add((os, catalogue, shard.Sha256, raw.Length));
            }
            if (refresh)
            {
                var noticesPath = Path.Combine(SourceDirectory(options), "notices-" + index.Notices.Sha256 + ".txt");
                EnsureSafe(noticesPath);
                var notices = File.Exists(noticesPath) ? await ReadFileBoundedAsync(noticesPath, 1048576, token).ConfigureAwait(false) : null;
                if (notices is null || !Matches(notices, index.Notices.Sha256, index.Notices.DownloadBytes))
                    notices = await DownloadAsync(options.Asset(index.CatalogueVersion, "notices.txt"), index.Notices.DownloadBytes, token).ConfigureAwait(false);
                if (!Matches(notices, index.Notices.Sha256, index.Notices.DownloadBytes)) throw new InvalidDataException("Definition notices checksum does not match.");
                CheckCurrent(options, generation, token);
                await WriteAtomicAsync(noticesPath, notices, token).ConfigureAwait(false);
                await WriteAtomicAsync(Path.Combine(SourceDirectory(options), "index.json"), indexBytes!, token).ConfigureAwait(false);
                _lastCheck = DateTimeOffset.UtcNow;
                await WriteAtomicAsync(Path.Combine(SourceDirectory(options), "checked.txt"), System.Text.Encoding.UTF8.GetBytes(_lastCheck.ToString("O", CultureInfo.InvariantCulture)), token).ConfigureAwait(false);
            }
            CheckCurrent(options, generation, token);
            lock (_sync)
            {
                changed = _index?.CatalogueVersion != index.CatalogueVersion;
                _index = index;
                foreach (var entry in staged) _memory[entry.Os] = new(entry.Catalogue, entry.Hash, entry.Bytes, ++_access);
                while (_memory.Count > 16 || _memory.Values.Sum(entry => (long)entry.Bytes) > 4 * 1024 * 1024)
                    _memory.Remove(_memory.MinBy(pair => pair.Value.Access).Key);
            }
            Status = "Sensor definitions " + index.CatalogueVersion + " ready; " + LoadedSystemCount + " system catalogues in memory.";
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex) when (Expected(ex))
        {
            Status = "Couldn't update sensor definitions; the selected source's cached definitions have been kept. " + ex.Message;
            return false;
        }
        finally
        {
            if (acquired) _gate.Release();
            // Loading an OS shard during polling must not recursively trigger another poll.
            if (force || changed) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public SensorStateResolution? Resolve(Sensor sensor, string? os)
    {
        lock (_sync)
        {
            if (!_options.Enabled || os is null || !_memory.TryGetValue(os, out var entry)) return null;
            _memory[os] = entry with { Access = ++_access };
            return entry.Catalogue.Resolve(sensor, os);
        }
    }

    public async Task ApplyAsync(IReadOnlyList<Sensor> sensors, Func<int, string?> operatingSystem, CancellationToken cancellationToken = default)
    {
        if (_readOnly) await InitialiseAsync(cancellationToken).ConfigureAwait(false);
        foreach (var sensor in sensors.Where(sensor => !sensor.HasClass("state"))) sensor.StateDefinition = null;
        foreach (var group in sensors.Where(sensor => sensor.HasClass("state")).GroupBy(sensor => operatingSystem(sensor.DeviceId)))
        {
            if (Options.Enabled && group.Key is not null) await SynchroniseAsync(new[] { group.Key }, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var sensor in group)
                sensor.StateDefinition = Options.Enabled ? Resolve(sensor, group.Key) ?? SensorStateResolution.Unresolved : null;
        }
    }

    private void CheckCurrent(SensorDefinitionOptions options, long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
            if (!_options.Enabled || _generation != generation || options.Repository != _options.Repository) throw new OperationCanceledException(token);
    }

    private async Task<byte[]> DownloadAsync(Uri uri, int maximum, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("DashyNMS-SensorDefinitions/1.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri;
        if (final is not null && (final.Scheme != "https" || final.Host != "raw.githubusercontent.com"))
            throw new InvalidDataException("Unexpected definitions download location.");
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Definitions download is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await ReadBoundedAsync(stream, maximum, token).ConfigureAwait(false);
    }

    private static bool Matches(byte[] bytes, string hash, int size) => bytes.Length == size && Convert.ToHexStringLower(SHA256.HashData(bytes)) == hash;
    private static async Task<byte[]> ExpandAsync(byte[] compressed, int expected, CancellationToken token)
    {
        using var input = new MemoryStream(compressed);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var bytes = await ReadBoundedAsync(gzip, Math.Min(expected, 2 * 1024 * 1024), token).ConfigureAwait(false);
        if (bytes.Length != expected) throw new InvalidDataException("Expanded definitions size does not match.");
        return bytes;
    }

    private static async Task<byte[]> ReadFileBoundedAsync(string path, int maximum, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await ReadBoundedAsync(stream, maximum, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Definitions data exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken token)
    {
        EnsureSafe(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            EnsureSafe(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void EnsureSafe(string path)
    {
        var relative = Path.GetRelativePath(_installRoot, Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Definitions path escapes the installation directory.");
        var current = _installRoot;
        CheckLink(current);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar)) { current = Path.Combine(current, part); CheckLink(current); }
    }

    private static void CheckLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A sensor definitions path contains a symbolic link or reparse point.");
    }

    private static void CheckTreeForLinks(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                CheckLink(path);
                if (Directory.Exists(path)) pending.Push(path);
            }
    }

    private static bool Expected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or HttpRequestException or OperationCanceledException;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requests.Cancel();
        _requests.Dispose();
        if (_ownsHttp) _http.Dispose();
        // A cancelled operation may still release the semaphore while shutdown completes.
    }
}
