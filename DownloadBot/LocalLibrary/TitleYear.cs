using System.Text.RegularExpressions;

namespace DownloadBot.LocalLibrary;

// Both the /download query and Plex folder names follow "<title> <year>" or "<title> (<year>)" —
// this pulls the trailing year off so the two can be compared on title alone (with an optional
// year cross-check), instead of doing a raw substring match that would confuse "Jason X" with
// "Jason Goes to Hell".
public static partial class TitleYear
{
    public static (string Title, int? Year) Parse(string raw)
    {
        var trimmed = raw.Trim();
        var match = TrailingYearRegex().Match(trimmed);
        if (!match.Success)
            return (trimmed, null);

        var title = trimmed[..match.Index].TrimEnd();
        var year = int.Parse(match.Groups["year"].Value);
        return (title, year);
    }

    [GeneratedRegex(@"\(?(?<year>(19|20)\d{2})\)?\s*$")]
    private static partial Regex TrailingYearRegex();
}
