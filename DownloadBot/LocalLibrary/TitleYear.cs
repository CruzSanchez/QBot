using System.Text.RegularExpressions;

namespace DownloadBot.LocalLibrary;

// Both the /download query and Plex folder names follow "<title> <year>" or "<title> (<year>)",
// often with trailing release info after the year ("Jason.X (1990) [1080p]") and dots/underscores
// as separators instead of spaces ("Jason.X.(1990)"). This normalizes separators, then locates the
// year (the LAST year-looking token, so a title that itself starts with a number — "2001: A Space
// Odyssey (1968)" — still resolves against its real release year, not its own name) and treats
// everything before it as the title, discarding anything after (quality/codec tags etc).
public static partial class TitleYear
{
    public static (string Title, int? Year) Parse(string raw)
    {
        var normalized = Normalize(raw);
        var matches = YearRegex().Matches(normalized);
        if (matches.Count == 0)
            return (normalized, null);

        var match = matches[^1];
        var title = normalized[..match.Index].TrimEnd();
        var year = int.Parse(match.Groups["year"].Value);
        return (title, year);
    }

    private static string Normalize(string raw)
    {
        var withSpaces = raw.Replace('.', ' ').Replace('_', ' ');
        return CollapseSpacesRegex().Replace(withSpaces, " ").Trim();
    }

    [GeneratedRegex(@"\(?(?<!\d)(?<year>(19|20)\d{2})(?!\d)\)?")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CollapseSpacesRegex();
}
