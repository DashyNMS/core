namespace DesktopNMS.Core.Help;

/// <summary>Repository paths in links, kept inside the repository.</summary>
internal static class HelpPaths
{
    /// <summary>A relative path with "." and ".." worked out, or null when it's absolute, leaves the root or isn't a plain path.</summary>
    public static string? Normalise(string path)
    {
        if (path.Length == 0 || path[0] == '/' || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            switch (part)
            {
                case "" or ".":
                    break;
                case "..":
                    if (parts.Count == 0)
                    {
                        return null;
                    }

                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(part);
                    break;
            }
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>Where a link written in <paramref name="from"/> points, as a path from the root.</summary>
    public static string? Combine(string from, string relative)
    {
        if (relative.StartsWith('/'))
        {
            return null;
        }

        var slash = from.LastIndexOf('/');
        return Normalise(slash < 0 ? relative : from[..(slash + 1)] + relative);
    }
}
