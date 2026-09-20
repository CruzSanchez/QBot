using System.Text.RegularExpressions;

namespace DownloadBot.LocalLibrary;

// Both the /download query and Plex folder names follow "<title> <year>" or "<title> (<year>)" —
// but folder/release names often use dots or underscores as separators instead of spaces
// (e.g. "Jason.X.(1990)", "Jason.X. (1990)"). This normalizes separators first, then pulls the
// trailing year off so the two can be compared on title alone (with an optional year cross-check),
// instead of doing a raw substring match that would confuse "Jason X" with "Jason Goes to Hell".
public static partial class TitleYear
{
    public static (string Title, int? Year) Parse(string raw)
    {
        var normalized = Normalize(raw);
        var match = TrailingYearRegex().Match(normalized);
        if (!match.Success)
            return (normalized, null);

        var title = normalized[..match.Index].TrimEnd();
        var year = int.Parse(match.Groups["year"].Value);
        return (title, year);
    }

    private static string Normalize(string raw)
    {
        var withSpaces = raw.Replace('.', ' ').Replace('_', ' ');
        return CollapseSpacesRegex().Replace(withSpaces, " ").Trim();
    }

    [GeneratedRegex(@"\(?(?<year>(19|20)\d{2})\)?\s*$")]
    private static partial Regex TrailingYearRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CollapseSpacesRegex();
}
