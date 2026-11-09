using System.Text.RegularExpressions;

namespace DownloadBot.QBittorrent;

// Used to find a just-added torrent in qBittorrent's list by name when no magnet hash is known
// upfront (a .torrent URL was handed to qBittorrent directly, which computes its own hash).
public static partial class TorrentNameMatcher
{
    public static string StripMarker(string title) => MarkerPrefixRegex().Replace(title, "");

    public static bool LooselyMatch(string qbitName, string ourTitle)
    {
        var a = AlphaNumericOnly(qbitName);
        var b = AlphaNumericOnly(StripMarker(ourTitle));
        if (a.Length == 0 || b.Length == 0) return false;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase))
            return true;

        // A .torrent file names the download after its own metadata (e.g. "Title (2003) [1080p] [BluRay] [YTS.MX]"),
        // which differs from the indexer's release name past the year, so fall back to "title + year".
        var ka = TitleYearKey(qbitName);
        return ka is not null && ka.Equals(TitleYearKey(StripMarker(ourTitle)), StringComparison.OrdinalIgnoreCase);
    }

    private static string? TitleYearKey(string value)
    {
        var m = TitleThroughYearRegex().Match(value);
        return m.Success ? AlphaNumericOnly(m.Value) : null;
    }

    [GeneratedRegex(@"^.+?(?<!\d)(?:19|20)\d{2}(?!\d)")]
    private static partial Regex TitleThroughYearRegex();

    private static string AlphaNumericOnly(string value) =>
        new([.. value.Where(char.IsLetterOrDigit)]);

    [GeneratedRegex(@"^\[DLBOT-[A-Z-]+\]\s*")]
    private static partial Regex MarkerPrefixRegex();
}
