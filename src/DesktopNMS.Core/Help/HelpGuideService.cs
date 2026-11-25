using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopNMS.Core.Help;

/// <summary>Where the guide being shown came from.</summary>
public enum HelpGuideSource
{
    /// <summary>The snapshot built into the app.</summary>
    Bundled,

    /// <summary>A copy downloaded for the release that's running.</summary>
    Downloaded,
}

/// <summary>The guide being shown, and where it came from.</summary>
/// <param name="TakenAt">When a downloaded copy was taken; null for the bundled snapshot.</param>
public sealed record HelpShownGuide(HelpGuide Guide, HelpGuideSource Source, DateTimeOffset? TakenAt)
{
    /// <summary>What its files hold, to tell a new download from the same one again. Null for the bundled snapshot.</summary>
    internal string? Fingerprint { get; init; }
}

/// <summary>How a refresh went.</summary>
public enum HelpRefreshOutcome
{
    /// <summary>The release's guide was downloaded and is in use.</summary>
    Updated,

    /// <summary>It was downloaded and is the same as the one in use.</summary>
    Unchanged,

    /// <summary>It was checked recently, so nothing was asked for.</summary>
    NotDue,

    /// <summary>The release has no published guide, as with a local build. The current guide stays.</summary>
    NotPublished,

    /// <summary>It couldn't be downloaded or saved. The current guide stays.</summary>
    Failed,
}

/// <summary>
/// The guide an app shows (#96): the snapshot bundled with it, so help works
/// offline and on first run, or a good copy downloaded for the same release.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is requested until a host calls <see cref="RefreshAsync"/>. A
/// refresh then fetches only this product's public guide, at the running
/// release's tag, from raw.githubusercontent.com, with a generic client name:
/// no address, name or other detail of anyone's network is in a request. A
/// guide that arrives whole replaces the copy; anything else leaves the last
/// good one in use.
/// </para>
/// <para>
/// <see cref="Changed"/> is raised on the thread doing the work, so an app
/// marshals it to its own.
/// </para>
/// </remarks>
public sealed class HelpGuideService : IDisposable
{
    /// <summary>A tag hardly ever changes, so a copy is only fetched again in case one was moved.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(30);

    /// <summary>How long after a failure before the next try that isn't forced.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

    /// <summary>The longest a whole refresh may take, so a slow server can't hold one open.</summary>
    private static readonly TimeSpan DownloadLimit = TimeSpan.FromMinutes(3);

    private const string ManifestName = "snapshot.json";
    private const string FilesFolder = "files";
    private const string TempSuffix = ".tmp";
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);
    private const int MaxManifestBytes = 256 * 1024;

    private readonly HelpFileSet _bundled;
    private readonly string? _directory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private HelpShownGuide _state;
    private DateTimeOffset? _nextTry;
    private bool _loaded;
    private int _disposed;

    private sealed record Manifest(int Schema, string Product, string Reference, string Version, DateTimeOffset TakenAt, Dictionary<string, string> Files);

    /// <param name="release">The running app's release, or null when it isn't known; the bundled guide is then always used.</param>
    /// <param name="bundled">The snapshot built into the app.</param>
    /// <param name="cacheDirectory">Where downloaded copies are kept, such as <see cref="Configuration.AppPaths.HelpCacheDirectory"/>, or null to keep them only in memory.</param>
    /// <param name="http">Shared client, or null for one of its own. It must not follow redirects or add headers that identify anyone.</param>
    public HelpGuideService(HelpProduct product, HelpRelease? release, HelpFileSet bundled, string? cacheDirectory = null, HttpClient? http = null, TimeProvider? time = null)
    {
        // It names a folder.
        if (product.Id.Length == 0 || !product.Id.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-'))
        {
            throw new ArgumentException("A help product's id is lower-case letters, digits and hyphens.", nameof(product));
        }

        Product = product;
        Release = release;
        _bundled = bundled;
        _directory = cacheDirectory is null ? null : Path.Combine(Path.GetFullPath(cacheDirectory), product.Id);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _time = time ?? TimeProvider.System;
        var guide = HelpGuide.Load(product, release, bundled);
        _state = new HelpShownGuide(guide, HelpGuideSource.Bundled, null);
    }

    public HelpProduct Product { get; }

    public HelpRelease? Release { get; }

    /// <summary>The guide to show now, with where it came from, read together.</summary>
    public HelpShownGuide Shown => Volatile.Read(ref _state);

    /// <summary>The guide to show now.</summary>
    public HelpGuide Current => Shown.Guide;

    public HelpGuideSource Source => Shown.Source;

    /// <summary>When a downloaded copy was taken; null for the bundled snapshot.</summary>
    public DateTimeOffset? TakenAt => Shown.TakenAt;

    /// <summary><see cref="Current"/> is now a different guide. Raised on the thread that did the work.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Uses a copy downloaded earlier for this release, if there's a whole one.
    /// Nothing is requested. <see cref="RefreshAsync"/> does this first too.
    /// </summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            changed = await LoadSavedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Downloads the running release's guide when one is due, or now when
    /// <paramref name="force"/> is set. Never throws for a network, server or
    /// disk problem; only for cancellation.
    /// </summary>
    public async Task<HelpRefreshOutcome> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (Release is null || Volatile.Read(ref _disposed) != 0)
        {
            return HelpRefreshOutcome.NotPublished;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var token = linked.Token;
        var changed = false;
        HelpRefreshOutcome outcome;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            changed = await LoadSavedAsync(token).ConfigureAwait(false);
            (outcome, var replaced) = await DownloadAsync(force, token).ConfigureAwait(false);
            changed |= replaced;
        }
        finally
        {
            _gate.Release();

            // Also when it was cancelled part way: a saved copy may be in use by then.
            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        return outcome;
    }

    private async Task<(HelpRefreshOutcome Outcome, bool Changed)> DownloadAsync(bool force, CancellationToken token)
    {
        var now = _time.GetUtcNow();
        var state = _state;

        // A clock put back leaves a time in the future, which isn't recent.
        if (!force && (now < _nextTry || (state.TakenAt is { } taken && taken <= now && now - taken < RefreshInterval)))
        {
            return (HelpRefreshOutcome.NotDue, false);
        }

        using var limited = CancellationTokenSource.CreateLinkedTokenSource(token);
        limited.CancelAfter(DownloadLimit);
        try
        {
            var files = await new HelpDownloader(_http, Product, Release!).DownloadAsync(_bundled, limited.Token).ConfigureAwait(false);
            if (files is null)
            {
                _nextTry = now + RefreshInterval;
                return (HelpRefreshOutcome.NotPublished, false);
            }

            var guide = LoadDownloaded(files);
            if (guide.Home is null || guide.Pages.Count < 2)
            {
                throw new InvalidDataException("The downloaded guide has no pages.");
            }

            var fingerprint = Fingerprint(files);
            await SaveAsync(files, now, token).ConfigureAwait(false);
            _nextTry = null;
            var same = fingerprint == (state.Fingerprint ?? BundledFingerprint(files));
            Volatile.Write(ref _state, new HelpShownGuide(guide, HelpGuideSource.Downloaded, now) { Fingerprint = fingerprint });
            return (same ? HelpRefreshOutcome.Unchanged : HelpRefreshOutcome.Updated, !same);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            token.ThrowIfCancellationRequested();
            _nextTry = now + RetryInterval;
            return (HelpRefreshOutcome.Failed, false);
        }
    }

    /// <summary>An image that couldn't be downloaded is still shown when the bundled snapshot has it.</summary>
    private HelpGuide LoadDownloaded(HelpFileSet files)
    {
        var all = files.Paths.Select(path => new KeyValuePair<string, byte[]>(path, Read(files, path)))
            .Concat(_bundled.Paths.Where(path => !files.Contains(path) && HelpDownloader.IsImage(path))
                .Select(path => new KeyValuePair<string, byte[]>(path, Read(_bundled, path))));
        return HelpGuide.Load(Product, Release, new HelpFileSet(all));
    }

    private async Task<bool> LoadSavedAsync(CancellationToken token)
    {
        if (_loaded || _directory is null || Release is null)
        {
            return false;
        }

        try
        {
            var folder = ReleaseFolder();
            var manifestPath = Path.Combine(folder, ManifestName);
            if (File.Exists(manifestPath) && new FileInfo(manifestPath).Length <= MaxManifestBytes)
            {
                var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllBytesAsync(manifestPath, token).ConfigureAwait(false));
                if (manifest is { Schema: 1, Files.Count: > 0 and <= HelpDownloader.MaxFiles } && manifest.Product == Product.Id && manifest.Reference == Release.Reference)
                {
                    var files = new List<KeyValuePair<string, byte[]>>();
                    long total = 0;
                    foreach (var (path, hash) in manifest.Files)
                    {
                        // Files are named by their hash, so a path in the manifest is never a path on disk.
                        if (HelpPaths.Normalise(path) != path || !IsHash(hash))
                        {
                            throw new InvalidDataException("The saved guide's list isn't one this app wrote.");
                        }

                        var file = Path.Combine(folder, FilesFolder, hash);
                        total += new FileInfo(file).Length;
                        if (total > HelpDownloader.MaxTotalBytes)
                        {
                            throw new InvalidDataException("The saved guide is too large.");
                        }

                        var content = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
                        if (Hash(content) != hash)
                        {
                            throw new InvalidDataException("The saved guide is damaged.");
                        }

                        files.Add(new(path, content));
                    }

                    var set = new HelpFileSet(files);
                    var guide = LoadDownloaded(set);
                    if (guide.Home is not null && guide.Pages.Count > 1)
                    {
                        var fingerprint = Fingerprint(set);
                        var differs = fingerprint != BundledFingerprint(set);
                        Volatile.Write(ref _state, new HelpShownGuide(guide, HelpGuideSource.Downloaded, manifest.TakenAt) { Fingerprint = fingerprint });
                        _loaded = true;
                        return differs;
                    }
                }
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            token.ThrowIfCancellationRequested();

            // Not usable: the bundled guide stays, and a refresh replaces the copy.
        }

        _loaded = true;
        return false;
    }

    private async Task SaveAsync(HelpFileSet files, DateTimeOffset now, CancellationToken token)
    {
        if (_directory is null)
        {
            return;
        }

        var folder = ReleaseFolder();
        var contents = Path.Combine(folder, FilesFolder);
        Directory.CreateDirectory(contents);
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in files.Paths)
        {
            var content = Read(files, path);
            var hash = Hash(content);
            listed[path] = hash;
            await WriteAsync(Path.Combine(contents, hash), content, token).ConfigureAwait(false);
        }

        // The list goes last: until it's there, the files are nothing to a reader.
        var manifest = new Manifest(1, Product.Id, Release!.Reference, Release.Version, now, listed);
        await WriteAsync(Path.Combine(folder, ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest), token).ConfigureAwait(false);
        Tidy(folder, listed.Values.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Other releases' copies go once this one is whole, and so do files it no longer lists.</summary>
    private void Tidy(string folder, HashSet<string> wanted)
    {
        try
        {
            // Not a file another copy of the app is writing now.
            foreach (var file in new DirectoryInfo(Path.Combine(folder, FilesFolder)).EnumerateFiles().Where(file => !wanted.Contains(file.Name)
                && (!file.Name.EndsWith(TempSuffix, StringComparison.Ordinal) || _time.GetUtcNow() - file.LastWriteTimeUtc > AbandonedAfter)))
            {
                file.Delete();
            }

            foreach (var other in Directory.EnumerateDirectories(_directory!).Where(other => !string.Equals(Path.GetFileName(other), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase)))
            {
                var isLink = (File.GetAttributes(other) & FileAttributes.ReparsePoint) != 0;
                if (!isLink && File.Exists(Path.Combine(other, ManifestName)))
                {
                    Directory.Delete(other, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another copy of the app may be reading them. They go next time.
        }
    }

    private string ReleaseFolder() => Path.Combine(_directory!, Release!.Reference);

    private static async Task WriteAsync(string path, byte[] content, CancellationToken token)
    {
        // Written beside it, then moved into place, so a reader never sees half a file.
        var temporary = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + TempSuffix;
        try
        {
            await File.WriteAllBytesAsync(temporary, content, token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static byte[] Read(HelpFileSet files, string path) => files.TryGet(path, out var content) ? content : [];

    /// <summary>The bundled snapshot's own fingerprint over the same files, to tell whether a download differs from it.</summary>
    private string? BundledFingerprint(HelpFileSet downloaded)
    {
        var guidePages = _bundled.Paths.Where(IsGuidePage).ToHashSet(StringComparer.Ordinal);
        if (!guidePages.SetEquals(downloaded.Paths.Where(IsGuidePage)) || !downloaded.Paths.All(_bundled.Contains))
        {
            return null;
        }

        return Fingerprint(new HelpFileSet(downloaded.Paths.Select(path => new KeyValuePair<string, byte[]>(path, Read(_bundled, path)))));

        static bool IsGuidePage(string path) => path.StartsWith(HelpProduct.GuideFolder + "/", StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.Ordinal);
    }

    private static string Fingerprint(HelpFileSet files)
        => Hash(Encoding.UTF8.GetBytes(string.Join('\n', files.Paths.Order(StringComparer.Ordinal).Select(path => path + " " + Hash(Read(files, path))))));

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static bool IsHash(string? text) => text is { Length: 64 } && text.All(character => char.IsAsciiHexDigitLower(character));

    private static bool IsExpected(Exception ex)
        => ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException or NotSupportedException or ObjectDisposedException;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        if (_ownsHttp)
        {
            _http.Dispose();
        }

        // Not the semaphore or the token source: a cancelled refresh may still be leaving.
    }
}
