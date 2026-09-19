using System.Text.RegularExpressions;

namespace DownloadBot.QBittorrent;

public static partial class MagnetHash
{
    // Only magnet links carry the info hash inline; a .torrent file URL would need to be downloaded and parsed to get one.
    public static string? TryExtract(string link)
    {
        var match = HashRegex().Match(link);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"btih:([A-Za-z0-9]{32,40})", RegexOptions.IgnoreCase)]
    private static partial Regex HashRegex();
}
