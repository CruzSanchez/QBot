using System.Text.RegularExpressions;

namespace DownloadBot.LocalLibrary;

// Both the /download query and Plex folder names follow "<title> <year>" or "<title> (<year>)",
// often with trailing release info after the year ("Jason.X (1990) [1080p]") and dots/underscores
// as separators instead of spaces ("Jason.X.(1990)"). This normalizes separators, then locates the
// year (the LAST year-looking token, so a title that itself starts with a number — "2001: A Space
// Odyssey (1968)" — still resolves against its real release year, not its own name) and treats
// everything before it as the title, discarding anything after (quality/codec tags etc).
//
// TV shows are often queried and stored without any year at all — a season/episode marker
// ("S02", "S01-S06", "Season 1-6") anchors the title the same way a year does when there's no
// year to use. Failing that, a query with neither (e.g. "Breaking Bad Complete") falls back to
// stripping a small set of "give me everything" filler words from the end.
public static partial class TitleYear
{
    public static (string Title, int? Year) Parse(string raw)
    {
        var normalized = Normalize(raw);

        var yearMatches = YearRegex().Matches(normalized);
        if (yearMatches.Count > 0)
        {
            var match = yearMatches[^1];
            var title = normalized[..match.Index].TrimEnd();
            return (StripTrailingFiller(title), int.Parse(match.Groups["year"].Value));
        }

        var seasonMatch = SeasonMarkerRegex().Match(normalized);
        if (seasonMatch.Success)
        {
            var title = normalized[..seasonMatch.Index].TrimEnd();
            return (StripTrailingFiller(title), null);
        }

        return (StripTrailingFiller(normalized), null);
    }

    private static string Normalize(string raw)
    {
        var withSpaces = raw.Replace('.', ' ').Replace('_', ' ');
        return CollapseSpacesRegex().Replace(withSpaces, " ").Trim();
    }

    private static string StripTrailingFiller(string title)
    {
        string previous;
        do
        {
            previous = title;
            title = TrailingFillerRegex().Replace(title, "").TrimEnd();
        } while (title != previous && title.Length > 0);

        return title;
    }

    [GeneratedRegex(@"\(?(?<!\d)(?<year>(19|20)\d{2})(?!\d)\)?")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\bS\d{1,2}(?:E\d{1,3})?(?:-S?\d{1,2}(?:E\d{1,3})?)?\b|\bSeasons?\s+\d{1,2}(?:-\d{1,2})?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonMarkerRegex();

    [GeneratedRegex(@"\s*\(?(?:complete series|complete|full series|all seasons?)\)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingFillerRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CollapseSpacesRegex();
}
