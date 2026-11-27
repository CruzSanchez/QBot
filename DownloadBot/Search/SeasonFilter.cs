using System.Text.RegularExpressions;

namespace DownloadBot.Search;

// Some indexers return other seasons for a season search ("Vikings S01" -> S05), and the better-seeded
// wrong season then tops the picker. This drops releases that name only seasons other than the one asked for.
public static partial class SeasonFilter
{
    // "S01", "Season 1", plus an optional range/list tail: "S01-S06", "Seasons 1, 2 & 3", "Season 1 to 6".
    [GeneratedRegex(@"(?<![a-z0-9])(?:s|seasons?[ ._]*)(\d{1,2})(?!\d)(?:[ ._]*(?:-|~|,|&|\+|to|thru|through|and)[ ._]*(?:s|season[ ._]*)?(\d{1,2})(?![a-z0-9]))*",
        RegexOptions.IgnoreCase)]
    private static partial Regex Seasons();

    // A title with no season marker at all ("Vikings Complete Series") is kept: it may well contain the season.
    // ponytail: a list is read as a range ("S01 & S06" keeps S03), and "5x01" numbering isn't recognised.
    public static IReadOnlyList<SearchResult> Apply(string query, IReadOnlyList<SearchResult> results)
    {
        var asked = Seasons().Match(query);
        if (!asked.Success)
            return results;

        var wanted = int.Parse(asked.Groups[1].Value);
        return results.Where(r => Covers(r.Title, wanted)).ToList();
    }

    private static bool Covers(string title, int wanted)
    {
        var markers = Seasons().Matches(title);
        return markers.Count == 0 || markers.Any(m =>
        {
            var numbers = m.Groups[1].Captures.Concat(m.Groups[2].Captures).Select(c => int.Parse(c.Value)).ToList();
            return wanted >= numbers.Min() && wanted <= numbers.Max();
        });
    }
}
