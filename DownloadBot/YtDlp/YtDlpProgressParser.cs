using System.Text.RegularExpressions;

namespace DownloadBot.YtDlp;

// Parses a single line of yt-dlp stdout (run with --newline, so one line per update instead of
// carriage-return overwriting) into a progress percentage and/or playlist position, if that line
// carries one. Pure and I/O-free so it's directly unit-testable without a real process.
public static partial class YtDlpProgressParser
{
    // e.g. "[download]  42.3% of   10.00MiB at    1.21MiB/s ETA 00:07"
    [GeneratedRegex(@"^\[download\]\s+([\d.]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentPattern();

    // e.g. "[download] Downloading video 3 of 12" / "[download] Downloading item 3 of 12"
    [GeneratedRegex(@"^\[download\]\s+Downloading (?:video|item)\s+(\d+)\s+of\s+(\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PlaylistPositionPattern();

    public static double? TryParsePercent(string line)
    {
        var match = PercentPattern().Match(line.TrimStart());
        if (!match.Success)
            return null;

        return double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var percent)
            ? percent
            : null;
    }

    public static (int Index, int Total)? TryParsePlaylistPosition(string line)
    {
        var match = PlaylistPositionPattern().Match(line.TrimStart());
        if (!match.Success)
            return null;

        if (int.TryParse(match.Groups[1].Value, out var index) && int.TryParse(match.Groups[2].Value, out var total))
            return (index, total);

        return null;
    }
}
