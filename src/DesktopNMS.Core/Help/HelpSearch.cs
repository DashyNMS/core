using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Help;

/// <summary>A page, or a heading on one, that matches a search.</summary>
/// <param name="Heading">The heading the match is under, or null for the page's opening.</param>
/// <param name="Passage">The sentence or two that match, as plain text.</param>
public sealed record HelpSearchResult(HelpPage Page, HelpHeading? Heading, string Passage, double Score);

/// <summary>
/// Searches a guide's titles, headings and text. Small enough to search in
/// full each time, so there's no index to keep or send anywhere.
/// </summary>
internal sealed partial class HelpSearchIndex
{
    /// <summary>A question's less important words, which would match nearly every page.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "a", "about", "all", "am", "an", "and", "any", "are", "as", "at", "be", "by", "can", "could", "dashynms", "do", "does", "for", "from",
        "get", "have", "help", "how", "i", "if", "in", "into", "is", "it", "its", "like", "me", "my", "need", "of", "on", "or", "please",
        "should", "so", "that", "the", "them", "then", "there", "this", "to", "up", "use", "want", "was", "what", "when", "where", "which",
        "why", "will", "with", "would", "you", "your",
    };

    private const int MaxTerms = 24;
    private const int PassageLength = 280;

    private readonly List<Entry> _entries;
    private readonly Dictionary<string, int> _documents = new(StringComparer.Ordinal);

    private sealed record Entry(HelpPage Page, HelpSection Section, HashSet<string> Title, HashSet<string> Heading, Dictionary<string, int> Body)
    {
        public bool Has(string term) => Title.Contains(term) || Heading.Contains(term) || Body.ContainsKey(term);
    }

    public HelpSearchIndex(IEnumerable<HelpPage> pages)
    {
        _entries = [];
        foreach (var page in pages)
        {
            var title = Words(page.Title).ToHashSet(StringComparer.Ordinal);

            // The front page only lists the others, so it would match everything.
            foreach (var section in page.Id == HelpGuide.HomeId ? page.Sections.Take(1) : page.Sections)
            {
                var body = Words(section.Text).GroupBy(word => word, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

                // A page's opening counts its title as its heading, so it comes before its own parts.
                _entries.Add(new Entry(page, section, title, section.Heading is null ? title : Words(section.Heading.Text).ToHashSet(StringComparer.Ordinal), body));
            }

            if (page.Sections.Count == 0)
            {
                _entries.Add(new Entry(page, new HelpSection(null, string.Empty, string.Empty), title, title, []));
            }
        }

        foreach (var entry in _entries)
        {
            foreach (var word in entry.Title.Concat(entry.Heading).Concat(entry.Body.Keys).Distinct(StringComparer.Ordinal))
            {
                _documents[word] = _documents.GetValueOrDefault(word) + 1;
            }
        }
    }

    public IReadOnlyList<HelpSearchResult> Search(string query, int limit)
    {
        // Distinct by last use, so the word being typed stays last.
        var all = Words(query).Reverse().Distinct(StringComparer.Ordinal).Reverse().ToList();
        var terms = all.Where(term => !Ignored.Contains(term)).Take(MaxTerms).ToList();
        if (terms.Count == 0)
        {
            terms = all.Take(MaxTerms).ToList();
        }

        if (terms.Count == 0 || limit <= 0)
        {
            return [];
        }

        // While someone types, the last word is still a beginning: "acknowl".
        // Its ending may be part of one that's taken off, so "scheduli" also meets "schedul".
        var last = char.IsLetterOrDigit(query[^1]) ? all[^1] : string.Empty;
        var isTyping = last.Length >= 3 && terms.Contains(last) && !_documents.ContainsKey(last);
        var endings = isTyping
            ? _documents.Keys.Where(word => word.StartsWith(last, StringComparison.Ordinal) || (word.Length >= 4 && last.StartsWith(word, StringComparison.Ordinal)))
                .OrderBy(word => Math.Abs(word.Length - last.Length)).Take(8).ToList()
            : [];

        var weights = terms.ToDictionary(term => term, Weight, StringComparer.Ordinal);
        var total = weights.Values.Sum();
        var results = new List<HelpSearchResult>();
        foreach (var entry in _entries)
        {
            double matched = 0;
            double score = 0;
            foreach (var term in terms)
            {
                var found = entry.Has(term) ? term : term == last ? endings.FirstOrDefault(entry.Has) : null;
                if (found is null)
                {
                    continue;
                }

                var weight = weights[term];
                matched += weight;
                score += weight * ((!entry.Title.Contains(found) ? 0 : entry.Section.Heading is null ? 5 : 1) + (entry.Heading.Contains(found) ? 4 : 0)
                    + Math.Min(entry.Body.GetValueOrDefault(found), 4) * 0.75);
            }

            // Half of what was asked for, weighed by how telling each word is;
            // a word the guide never uses counts against every page.
            if (matched > 0 && matched >= total / 2)
            {
                var coverage = matched / total;
                results.Add(new HelpSearchResult(entry.Page, entry.Section.Heading, string.Empty, score * coverage * coverage));
            }
        }

        return [.. results
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Page.Title, StringComparer.Ordinal)
            .Take(limit)
            .Select(result => result with { Passage = Passage(result, terms, endings) })];
    }

    private double Weight(string term) => Math.Log(1 + (double)(_entries.Count + 1) / (1 + _documents.GetValueOrDefault(term)));

    /// <summary>The lines of the section with the most of the words, shortened at a word.</summary>
    private static string Passage(HelpSearchResult result, List<string> terms, List<string> endings)
    {
        var section = result.Page.Sections.FirstOrDefault(candidate => candidate.Heading == result.Heading);
        var lines = section?.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var wanted = terms.Concat(endings).ToHashSet(StringComparer.Ordinal);
        var best = 0;
        var most = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            var count = Words(lines[index]).Distinct(StringComparer.Ordinal).Count(wanted.Contains);
            if (count > most)
            {
                (best, most) = (index, count);
            }
        }

        var passage = lines[best];
        for (var index = best + 1; index < lines.Length && passage.Length + 1 + lines[index].Length <= PassageLength; index++)
        {
            passage += " " + lines[index];
        }

        if (passage.Length <= PassageLength)
        {
            return passage;
        }

        var cut = passage.LastIndexOf(' ', PassageLength);
        return passage[..(cut > 0 ? cut : PassageLength)].TrimEnd(',', ';', ' ') + "…";
    }

    /// <summary>Words in lower case, with common endings taken off so "acknowledging alerts" finds "acknowledge an alert".</summary>
    private static IEnumerable<string> Words(string text)
        => WordPattern().Matches(text.ToLowerInvariant()).Select(match => Stem(match.Value.Replace("'", string.Empty, StringComparison.Ordinal).Replace("’", string.Empty, StringComparison.Ordinal)));

    private static string Stem(string word)
    {
        if (word.Length < 4 || word.Any(char.IsDigit))
        {
            return word;
        }

        foreach (var ending in (string[])["ments", "ment", "ings", "ing", "ies", "ed", "s"])
        {
            if (!word.EndsWith(ending, StringComparison.Ordinal) || word.Length - ending.Length < 3 || word.EndsWith("ss", StringComparison.Ordinal))
            {
                continue;
            }

            word = word[..^ending.Length] + (ending == "ies" ? "y" : string.Empty);

            // "pinned" and "pinning" meet "pin".
            if (ending is "ings" or "ing" or "ed" && word.Length > 3 && word[^1] == word[^2] && word[^1] is not ('l' or 's'))
            {
                word = word[..^1];
            }

            break;
        }

        // "schedule" and "scheduling" meet without the e.
        return word.Length > 3 && word.EndsWith('e') ? word[..^1] : word;
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}]+(?:['’][\p{L}]+)?")]
    private static partial Regex WordPattern();
}
