using System.Text.RegularExpressions;

namespace DownloadBot.YtDlp;

// Parses a single line of yt-dlp stdout (run with --newline, so one line per update instead of
// carriage-return overwriting) into a progress percentage and/or playlist position, if that line
// carries one. Pure and I/O-free so it's directly unit-testable without a real process.
public static partial class YtDlpProgressParser
{
    // yt-dlp colorizes progress output by default (even when redirected/piped, depending on version
    // and terminal detection), which inserts ANSI escape sequences like "\x1b[0;94m" around the
    // percentage — strip these before matching, otherwise the regexes below silently never match and
    // progress looks frozen even though the download is genuinely proceeding.
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapePattern();

    // e.g. "[download]  42.3% of   10.00MiB at    1.21MiB/s ETA 00:07"
    [GeneratedRegex(@"^\[download\]\s+([\d.]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentPattern();

    // e.g. "[download] Downloading video 3 of 12" / "[download] Downloading item 3 of 12"
    [GeneratedRegex(@"^\[download\]\s+Downloading (?:video|item)\s+(\d+)\s+of\s+(\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PlaylistPositionPattern();

    public static double? TryParsePercent(string line)
    {
        var match = PercentPattern().Match(Clean(line));
        if (!match.Success)
            return null;

        return double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var percent)
            ? percent
            : null;
    }

    public static (int Index, int Total)? TryParsePlaylistPosition(string line)
    {
        var match = PlaylistPositionPattern().Match(Clean(line));
        if (!match.Success)
            return null;

        if (int.TryParse(match.Groups[1].Value, out var index) && int.TryParse(match.Groups[2].Value, out var total))
            return (index, total);

        return null;
    }

    private static string Clean(string line) => AnsiEscapePattern().Replace(line, "").TrimStart();
}
