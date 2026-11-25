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

    /// <summary>A link's path without any "?raw=true" after it.</summary>
    public static string WithoutQuery(string path) => path.IndexOf('?') is var query and >= 0 ? path[..query] : path;

    /// <summary>"%20" as a space. What can't be decoded is left as it is.</summary>
    public static string Decode(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    /// <summary>
    /// The repository path in one of a product's own GitHub addresses,
    /// whichever branch or tag it names: it's read from the running release's
    /// files, so the help never mixes releases. Null for any other address.
    /// </summary>
    public static string? FromAddress(HelpProduct product, string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var path = Decode(address.AbsolutePath);
        var start = $"/{product.Repository}/";
        string? rest = null;
        if (address.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) && path.StartsWith(start, StringComparison.OrdinalIgnoreCase))
        {
            rest = path[start.Length..];
        }
        else if (address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kind in (string[])["blob/", "raw/"])
            {
                if (path.StartsWith(start + kind, StringComparison.OrdinalIgnoreCase))
                {
                    rest = path[(start.Length + kind.Length)..];
                }
            }
        }

        // What follows is the branch or tag, then the file.
        foreach (var named in (string[])["refs/heads/", "refs/tags/"])
        {
            if (rest?.StartsWith(named, StringComparison.Ordinal) == true)
            {
                rest = rest[named.Length..];
                break;
            }
        }

        var slash = rest?.IndexOf('/') ?? -1;
        return slash > 0 ? Normalise(rest![(slash + 1)..]) : null;
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
