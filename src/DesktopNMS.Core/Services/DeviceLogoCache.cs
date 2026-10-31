using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Services;

/// <summary>The kind of image a <see cref="DeviceLogo"/> file holds, so an app knows how to load it.</summary>
public enum DeviceLogoFormat
{
    Svg,
    Png,
}

/// <summary>A device logo on disk, ready to load - see <see cref="IDeviceLogoCache"/>.</summary>
/// <param name="FilePath">The cached file. It may be evicted later, so load it straight away rather than keep the path.</param>
public sealed record DeviceLogo(string FilePath, DeviceLogoFormat Format);

/// <summary>Device logos from the LibreNMS server, kept on this computer.</summary>
public interface IDeviceLogoCache
{
    /// <summary>
    /// The logo for a device's <see cref="Models.Device.Icon"/> (hpe.svg),
    /// from disk or fetched from the server. Null when there's no icon, it
    /// isn't a safe file name, the server can't be reached or the call is
    /// cancelled - never throws, since a logo is never worth an error.
    /// </summary>
    Task<DeviceLogo?> GetAsync(string? icon, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches device logos the way LibreNMS resolves them and keeps the last few
/// on disk.
/// </summary>
/// <remarks>
/// <para>
/// The disk is the only cache: an app asks when a device's details open, not
/// for a whole list, so there's nothing to gain from keeping images in memory.
/// The folder holds at most <see cref="MaxFiles"/> logos across every server,
/// the least recently used going first. A hit touches the file's last-write
/// time, since access times are switched off on many file systems; the date a
/// logo was fetched is kept in its file name instead, so it can be fetched
/// again after <see cref="MaxAge"/> and pick up a changed logo.
/// </para>
/// <para>
/// Files are keyed by server (a hash of its web root) and the requested icon,
/// so switching server never shows another's logo. Whatever the icon resolved
/// to is kept under the icon asked for - generic.svg included - so a device
/// with no logo of its own doesn't repeat five requests each time it's opened.
/// </para>
/// </remarks>
public sealed partial class DeviceLogoCache : IDeviceLogoCache
{
    /// <summary>Logos kept on disk, across every server.</summary>
    public const int MaxFiles = 20;

    /// <summary>Larger than any real logo; anything bigger isn't one.</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>How long a logo is used before it's fetched again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private const char Separator = '~';
    private const string TempSuffix = ".tmp";
    private const string DateFormat = "yyyyMMdd";

    /// <summary>How long a temporary file is left alone - one older than this was abandoned mid-write.</summary>
    private static readonly TimeSpan AbandonedTempAge = TimeSpan.FromMinutes(5);

    private readonly ILibreNmsTransport _transport;
    private readonly ILogger<DeviceLogoCache> _logger;
    private readonly string _directory;
    private readonly TimeProvider _time;

    /// <summary>One fetch per key at a time: two views asking for the same logo share it.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<DeviceLogo?>>> _inFlight = new();

    /// <summary>Serialises eviction, so two stores can't both delete towards the limit.</summary>
    private readonly object _evictionSync = new();

    public DeviceLogoCache(ILibreNmsTransport transport, ILogger<DeviceLogoCache> logger)
        : this(transport, logger, AppPaths.LogoCacheDirectory, TimeProvider.System)
    {
    }

    internal DeviceLogoCache(ILibreNmsTransport transport, ILogger<DeviceLogoCache> logger, string directory, TimeProvider time)
    {
        _transport = transport;
        _logger = logger;
        _directory = directory;
        _time = time;
    }

    public async Task<DeviceLogo?> GetAsync(string? icon, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsSafeIcon(icon) || _transport.Connection is not { } connection)
            {
                return null;
            }

            var key = ServerKey(connection.WebRoot) + Separator + icon;

            // The fetch itself isn't tied to this caller: if they go away, it
            // still finishes and is kept for the next time, and anyone else
            // waiting on it still gets it. On the thread pool, since it reads
            // and writes files and the caller is usually a UI thread.
            var lazy = _inFlight.GetOrAdd(key, k => new Lazy<Task<DeviceLogo?>>(() => Task.Run(() => LoadAsync(k, icon!))));
            var load = lazy.Value;
            _ = load.ContinueWith(
                _ => _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<DeviceLogo?>>>(key, lazy)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return await load.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't get the device logo {Icon}", icon);
            return null;
        }
    }

    /// <summary>
    /// A bare file name from the server, ending .svg or .png - nothing that
    /// could name another folder once it's part of a path or a URL.
    /// </summary>
    internal static bool IsSafeIcon(string? icon)
        => icon is not null && SafeIcon().IsMatch(icon) && !icon.Contains("..", StringComparison.Ordinal);

    [GeneratedRegex(@"\A[A-Za-z0-9._-]+\.(svg|png)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SafeIcon();

    /// <summary>A short, stable name for a server - its web root, hashed - so one server's logos are never another's.</summary>
    internal static string ServerKey(Uri webRoot)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(webRoot.ToString().ToLowerInvariant())))[..12].ToLowerInvariant();

    /// <summary>The order LibreNMS itself resolves a logo in, ending at the generic one every install has.</summary>
    internal static IEnumerable<string> Candidates(string icon)
    {
        var name = Path.GetFileNameWithoutExtension(icon);
        yield return $"images/logos/{name}.svg";
        yield return $"images/logos/{name}.png";
        yield return $"images/os/{name}.svg";
        yield return $"images/os/{name}.png";
        yield return "images/os/generic.svg";
    }

    private async Task<DeviceLogo?> LoadAsync(string key, string icon)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var cached = FindCached(key);

        if (cached is { } hit && now - hit.FetchedOn < MaxAge)
        {
            Touch(hit.Path, now);
            return hit.Logo;
        }

        try
        {
            foreach (var path in Candidates(icon))
            {
                var file = await _transport.GetWebFileAsync(path, MaxBytes).ConfigureAwait(false);
                if (file is null || FormatOf(file.Content) is not { } format)
                {
                    continue;
                }

                var stored = Store(key, file.Content, format, now);
                if (cached is { } stale && !string.Equals(stale.Path, stored.FilePath, StringComparison.Ordinal))
                {
                    TryDelete(stale.Path);
                }

                Evict();
                return stored;
            }

            _logger.LogDebug("No logo on the server for {Icon}, not even the generic one", icon);
        }
        catch (Exception ex)
        {
            // Not reachable right now: an out-of-date logo is better than none.
            _logger.LogDebug(ex, "Couldn't fetch the device logo {Icon}", icon);
        }

        if (cached is { } fallback)
        {
            Touch(fallback.Path, now);
            return fallback.Logo;
        }

        return null;
    }

    private sealed record CachedFile(string Path, DateTime FetchedOn, DeviceLogo Logo);

    /// <summary>The good file for <paramref name="key"/>, if there is one - an empty or unreadable one is deleted on the way.</summary>
    private CachedFile? FindCached(string key)
    {
        if (!Directory.Exists(_directory))
        {
            return null;
        }

        CachedFile? best = null;
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            if (!TryParseName(Path.GetFileName(path), out var fileKey, out var fetchedOn, out var format)
                || !string.Equals(fileKey, key, StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsIntact(path, format))
            {
                _logger.LogDebug("Replacing the damaged device logo {Path}", path);
                TryDelete(path);
                continue;
            }

            if (best is null || fetchedOn > best.FetchedOn)
            {
                if (best is not null)
                {
                    TryDelete(best.Path);
                }

                best = new CachedFile(path, fetchedOn, new DeviceLogo(path, format));
            }
            else
            {
                TryDelete(path);
            }
        }

        return best;
    }

    private DeviceLogo Store(string key, byte[] content, DeviceLogoFormat format, DateTime now)
    {
        Directory.CreateDirectory(_directory);
        var name = key + Separator + now.ToString(DateFormat, CultureInfo.InvariantCulture) + Extension(format);
        var path = Path.Combine(_directory, name);

        // Written beside it, then moved into place, so a reader never sees half a file.
        var temp = path + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        try
        {
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        Touch(path, now);
        return new DeviceLogo(path, format);
    }

    /// <summary>Keeps the folder to <see cref="MaxFiles"/>, least recently used out first, and clears abandoned temporary files.</summary>
    private void Evict()
    {
        lock (_evictionSync)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var logos = new List<FileInfo>();
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles())
            {
                if (file.Name.EndsWith(TempSuffix, StringComparison.Ordinal))
                {
                    if (now - file.LastWriteTimeUtc > AbandonedTempAge)
                    {
                        TryDelete(file.FullName);
                    }
                }
                else if (TryParseName(file.Name, out _, out _, out _))
                {
                    logos.Add(file);
                }
            }

            foreach (var file in logos.OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxFiles))
            {
                TryDelete(file.FullName);
            }
        }
    }

    /// <summary>Reads "{server}~{icon}~{yyyyMMdd}.{svg|png}" back into its parts.</summary>
    private static bool TryParseName(string name, out string key, out DateTime fetchedOn, out DeviceLogoFormat format)
    {
        key = string.Empty;
        fetchedOn = default;
        format = default;

        var extension = Path.GetExtension(name);
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            format = DeviceLogoFormat.Svg;
        }
        else if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            format = DeviceLogoFormat.Png;
        }
        else
        {
            return false;
        }

        var stem = name[..^extension.Length];
        var dateAt = stem.LastIndexOf(Separator);
        if (dateAt <= 0 || stem.IndexOf(Separator) == dateAt
            || !DateTime.TryParseExact(stem[(dateAt + 1)..], DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out fetchedOn))
        {
            return false;
        }

        key = stem[..dateAt];
        return true;
    }

    /// <summary>Not empty, and still starts the way an SVG or PNG does - enough to catch a truncated or overwritten file.</summary>
    private static bool IsIntact(string path, DeviceLogoFormat format)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[4096];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return read > 0 && FormatOf(head.AsSpan(0, read)) == format;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// What the bytes really are, whatever the server called them: a PNG by
    /// its signature, an SVG by an &lt;svg&gt; element near the start (after
    /// any XML declaration or comment). A login page is neither.
    /// </summary>
    internal static DeviceLogoFormat? FormatOf(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(PngSignature))
        {
            return DeviceLogoFormat.Png;
        }

        var head = Encoding.UTF8.GetString(content[..Math.Min(content.Length, 4096)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (head.StartsWith('<')
            && head.Contains("<svg", StringComparison.OrdinalIgnoreCase)
            && !head.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceLogoFormat.Svg;
        }

        return null;
    }

    private static string Extension(DeviceLogoFormat format) => format == DeviceLogoFormat.Png ? ".png" : ".svg";

    private void Touch(string path, DateTime now)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Couldn't mark the device logo {Path} as used", path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
