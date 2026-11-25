using System.Net;
using System.Text;

namespace DesktopNMS.Core.Help;

/// <summary>
/// Fetches one release's guide from the repository's public files: the front
/// page, the pages it links to and the images they show. Only those
/// addresses are ever asked for, and sizes are bounded.
/// </summary>
internal sealed class HelpDownloader(HttpClient http, HelpProduct product, HelpRelease release)
{
    public const int MaxFiles = 300;
    public const long MaxTotalBytes = 48L * 1024 * 1024;

    private const int MaxPages = 150;
    private const int MaxPageBytes = 512 * 1024;
    private const string Host = "raw.githubusercontent.com";

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    private long _total;

    /// <summary>Not SVG: it can hold scripts and addresses, and a downloaded one isn't worth trusting an app's renderer with.</summary>
    public static bool IsImage(string path) => ImageExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>The guide's files, or null when this release has no guide. Throws when any part fails for another reason.</summary>
    public async Task<HelpFileSet?> DownloadAsync(HelpFileSet bundled, CancellationToken token)
    {
        const string start = HelpProduct.GuideFolder + "/README.md";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (await GetAsync(start, MaxPageBytes, token).ConfigureAwait(false) is not { } front)
        {
            return null;
        }

        files[start] = front;
        var waiting = new Queue<string>([start]);
        var seen = new HashSet<string>([start], StringComparer.Ordinal);
        var images = new SortedSet<string>(StringComparer.Ordinal);
        while (waiting.TryDequeue(out var path))
        {
            foreach (var (line, isCode) in HelpMarkdown.Lines(Encoding.UTF8.GetString(files[path])))
            {
                if (isCode)
                {
                    continue;
                }

                foreach (var (isImage, _, target) in HelpMarkdown.Links(line))
                {
                    var (relative, _) = HelpMarkdown.SplitFragment(target);
                    var linked = HelpPaths.FromAddress(product, target)
                        ?? (relative.Length == 0 ? null : HelpPaths.Combine(path, HelpPaths.Decode(HelpPaths.WithoutQuery(relative))));
                    if (linked is null)
                    {
                        continue;
                    }

                    if (isImage && IsImage(linked))
                    {
                        images.Add(linked);
                    }
                    else if (!isImage && linked.StartsWith(HelpProduct.GuideFolder + "/", StringComparison.Ordinal)
                        && linked.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && seen.Add(linked))
                    {
                        if (seen.Count > MaxPages)
                        {
                            throw new InvalidDataException("The guide has too many pages.");
                        }

                        // A page that isn't there is a broken link - unless the app was
                        // built with it, when it's the download that's wrong.
                        if (await GetAsync(linked, MaxPageBytes, token).ConfigureAwait(false) is { } page)
                        {
                            files[linked] = page;
                            waiting.Enqueue(linked);
                        }
                        else if (bundled.Contains(linked))
                        {
                            throw new InvalidDataException("A page of the guide couldn't be downloaded.");
                        }
                    }
                }
            }
        }

        if (files.Count + images.Count > MaxFiles)
        {
            throw new InvalidDataException("The guide has too many files.");
        }

        foreach (var image in images)
        {
            if (await GetAsync(image, HelpFileSet.MaxFileBytes, token).ConfigureAwait(false) is { } content)
            {
                files[image] = content;
            }
        }

        return new HelpFileSet(files);
    }

    /// <summary>One file at the release's reference, or null when it isn't there.</summary>
    private async Task<byte[]?> GetAsync(string path, int maximum, CancellationToken token)
    {
        var address = new Uri($"https://{Host}/{product.Repository}/{release.Reference}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}");
        using var request = new HttpRequestMessage(HttpMethod.Get, address);

        // Generic on purpose: no version, system or install to tell one person from another.
        request.Headers.UserAgent.ParseAdd("DashyNMS-Help/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && (final.Scheme != Uri.UriSchemeHttps || final.Host != Host))
        {
            throw new InvalidDataException("The guide was sent from an unexpected place.");
        }

        if (response.Content.Headers.ContentLength > maximum)
        {
            throw new InvalidDataException("A guide file is too large.");
        }

        using var content = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            _total += read;
            if (content.Length + read > maximum || _total > MaxTotalBytes)
            {
                throw new InvalidDataException("The guide is too large.");
            }

            content.Write(buffer, 0, read);
        }

        return content.ToArray();
    }
}
