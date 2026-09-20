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
        return a.Length > 0 && b.Length > 0 &&
               (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    private static string AlphaNumericOnly(string value) =>
        new([.. value.Where(char.IsLetterOrDigit)]);

    [GeneratedRegex(@"^\[DLBOT-[A-Z-]+\]\s*")]
    private static partial Regex MarkerPrefixRegex();
}
