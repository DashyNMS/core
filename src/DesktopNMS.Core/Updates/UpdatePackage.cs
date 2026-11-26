using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Updates;

/// <summary>
/// Finds and fetches a release's installer for the app to update itself.
/// Only ever this repository's own "DashyNMS-Setup-*.exe", from its own
/// release downloads, and only one whose bytes match the SHA-256 GitHub
/// publishes for it - anything else and the app falls back to pointing at
/// the release page instead.
/// </summary>
public static class UpdatePackage
{
    public const string DownloadPrefix = "https://github.com/DashyNMS/desktop/releases/download/";

    private static readonly Regex InstallerName = new(@"^DashyNMS-Setup-[0-9A-Za-z.\-]+\.exe$", RegexOptions.CultureInvariant);
    private static readonly Regex NativePackageName = new(@"^DashyNMS-[0-9A-Za-z.\-]+-(osx-(x64|arm64)\.dmg|linux-(x64|arm64)\.AppImage)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Selects an exact version and runtime. Windows retains its 1.x-compatible
    /// installer name; a Linux package-manager install should not call this.
    /// The app decides whether it can apply this kind of update.
    /// </summary>
    public static GitHubReleaseAsset? FindForRuntime(GitHubRelease release, string runtime)
    {
        ArgumentNullException.ThrowIfNull(release);
        var version = release.TagName?.TrimStart('v');
        if (string.IsNullOrWhiteSpace(version) || !Regex.IsMatch(version, @"^[0-9A-Za-z.\-]+$", RegexOptions.CultureInvariant)) return null;
        var name = runtime switch
        {
            "win-x64" => $"DashyNMS-Setup-{version}.exe",
            "osx-x64" or "osx-arm64" => $"DashyNMS-{version}-{runtime}.dmg",
            "linux-x64" or "linux-arm64" => $"DashyNMS-{version}-{runtime}.AppImage",
            _ => null,
        };
        if (name is null) return null;
        var expectedUrl = DownloadPrefix + Uri.EscapeDataString(release.TagName!) + "/" + name;
        return release.Assets.FirstOrDefault(a => a.Name == name && a.BrowserDownloadUrl == expectedUrl
            && a.Size > 0 && ExpectedSha256(a) is not null);
    }

    /// <summary>The release's installer - null if it has none this app would run.</summary>
    public static GitHubReleaseAsset? FindInstaller(GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);

        return release.Assets.FirstOrDefault(a =>
            a.Name is { } name
            && InstallerName.IsMatch(name)
            && a.BrowserDownloadUrl is { } url
            && url.StartsWith(DownloadPrefix, StringComparison.Ordinal)
            && a.Size > 0
            && ExpectedSha256(a) is not null);
    }

    /// <summary>"sha256:f834..." -> the hex digest, lower case; null if GitHub gave none (or another algorithm).</summary>
    public static string? ExpectedSha256(GitHubReleaseAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        const string prefix = "sha256:";
        if (asset.Digest is not { } digest || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>
    /// The installer in <paramref name="directory"/>, downloading it first
    /// unless a verified copy is already there. Downloads to a ".partial"
    /// file and only renames it into place once its size and SHA-256 match
    /// what GitHub published - a wrong file is deleted, never left to run.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, GitHubReleaseAsset asset, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(asset);

        var expected = ExpectedSha256(asset) ?? throw new InvalidOperationException("The release publishes no SHA-256 for its installer.");
        if (asset.Name is not { } name || !(InstallerName.IsMatch(name) || NativePackageName.IsMatch(name)) || asset.BrowserDownloadUrl is not { } url || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("That isn't a DashyNMS installer from its own releases.");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);

        if (File.Exists(path))
        {
            if (new FileInfo(path).Length == asset.Size && await Sha256Async(path, cancellationToken).ConfigureAwait(false) == expected)
            {
                return path;
            }

            File.Delete(path);
        }

        var partial = path + ".partial";
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = File.Create(partial);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var size = new FileInfo(partial).Length;
            if (size != asset.Size)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"The download was {size} bytes, not the {asset.Size} GitHub lists."));
            }

            var actual = await Sha256Async(partial, cancellationToken).ConfigureAwait(false);
            if (actual != expected)
            {
                throw new InvalidDataException("The download doesn't match the SHA-256 GitHub publishes for it.");
            }

            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    /// <summary>Clears out installers (and half-finished downloads) other than <paramref name="keepName"/>.</summary>
    public static void DeleteOthers(string directory, string? keepName)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!string.Equals(Path.GetFileName(file), keepName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // In use or gone - tidied next time.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
