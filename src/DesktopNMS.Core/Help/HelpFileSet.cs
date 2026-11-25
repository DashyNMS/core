using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace DesktopNMS.Core.Help;

/// <summary>
/// A guide's files, by their path in the repository ("docs/guide/README.md"):
/// its pages and the images they show. Held in memory, so nothing a page links
/// to can ever name a file outside the set.
/// </summary>
public sealed class HelpFileSet
{
    /// <summary>Larger than any real page or screenshot. A bigger file is left out.</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    private readonly Dictionary<string, byte[]> _files;

    public HelpFileSet(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        _files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            if (HelpPaths.Normalise(path) is { } safe && content.Length <= MaxFileBytes)
            {
                _files[safe] = content;
            }
        }
    }

    public static HelpFileSet Empty { get; } = new([]);

    public IReadOnlyCollection<string> Paths => _files.Keys;

    public bool Contains(string path) => _files.ContainsKey(path);

    public bool TryGet(string path, [NotNullWhen(true)] out byte[]? content) => _files.TryGetValue(path, out content);

    /// <summary>
    /// Files embedded in an assembly, the usual way to bundle a snapshot.
    /// Each resource's logical name is <paramref name="prefix"/> followed by
    /// its path in the repository; MSBuild's %(RecursiveDir) writes the build
    /// machine's separators, so both kinds are read.
    /// </summary>
    public static HelpFileSet FromAssembly(Assembly assembly, string prefix)
    {
        var files = new List<KeyValuePair<string, byte[]>>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null || stream.Length > MaxFileBytes)
            {
                continue;
            }

            var content = new byte[stream.Length];
            stream.ReadExactly(content);
            files.Add(new(name[prefix.Length..].Replace('\\', '/'), content));
        }

        return new HelpFileSet(files);
    }

    /// <summary>Files in a folder that's laid out as the repository, or as a wiki, is. Links are skipped.</summary>
    public static HelpFileSet FromDirectory(string root)
    {
        var files = new List<KeyValuePair<string, byte[]>>();
        if (Directory.Exists(root))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                if (new FileInfo(path).Length <= MaxFileBytes)
                {
                    files.Add(new(Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllBytes(path)));
                }
            }
        }

        return new HelpFileSet(files);
    }
}
