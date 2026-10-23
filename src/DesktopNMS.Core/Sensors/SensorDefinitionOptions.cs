using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Sensors;

/// <summary>Installation preferences; these must be stored outside sign-out-cleared account data.</summary>
public sealed record SensorDefinitionOptions
{
    public const string OfficialRepository = "https://github.com/DashyNMS/sensor-definitions";
    public bool Enabled { get; init; }
    public bool UseCustomRepository { get; init; }
    public string CustomRepository { get; init; } = "";

    [JsonIgnore]
    public string Repository => NormaliseRepository(UseCustomRepository ? CustomRepository : OfficialRepository);

    public static string NormaliseRepository(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use a public HTTPS GitHub repository URL, such as https://github.com/owner/sensor-definitions.");
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        var parts = path.Split('/');
        if (parts.Length != 2 || parts.Any(part => part is "." or ".." || !Regex.IsMatch(part, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant)))
            throw new ArgumentException("Use the repository URL without a branch, file, query or credentials.");
        return "https://github.com/" + path.ToLowerInvariant();
    }

    internal string SourceKey => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Repository)));
    internal Uri Feed => new("https://raw.githubusercontent.com/" + Repository["https://github.com/".Length..] + "/main/feeds/v1/stable.json");
    internal Uri Asset(string version, string path)
        => new("https://raw.githubusercontent.com/" + Repository["https://github.com/".Length..] + "/v" + version + "/data/" + path);
}

/// <summary>Persistent data lives beside the installation, never inside a signed bundle or AppImage mount.</summary>
public sealed record SensorDefinitionPaths(string SettingsFile, string DefinitionsDirectory)
{
    public static SensorDefinitionPaths ForInstallation(string executableDirectory, bool macOS = false, string? appImage = null)
    {
        var root = Path.GetFullPath(executableDirectory);
        var adjacent = false;
        if (macOS)
        {
            for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            {
                if (!current.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
                root = current.Parent?.FullName ?? throw new IOException("Cannot locate the app installation directory.");
                adjacent = true;
                break;
            }
        }
        if (!string.IsNullOrWhiteSpace(appImage))
        {
            if (!Path.IsPathFullyQualified(appImage)) throw new IOException("APPIMAGE must name an absolute installation path.");
            root = Path.GetDirectoryName(appImage)!;
            adjacent = true;
        }
        return new(Path.Combine(root, adjacent ? "DashyNMS.installation-settings.json" : "installation-settings.json"),
            Path.Combine(root, adjacent ? "DashyNMS.sensor-definitions" : "sensor-definitions"));
    }
}
