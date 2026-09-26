namespace DownloadBot.YtDlp;

// Builds the yt-dlp -f format selector for a requested quality. Pure and I/O-free so it's directly
// unit-testable, same pattern as YtDlpProgressParser.
public static class YtDlpFormatSelector
{
    // quality: null -> capped at defaultMaxHeight (the configured default, e.g. 1080p); "best"
    // (case-insensitive) -> uncapped, whatever the source's highest available stream is; anything
    // else parseable as a number -> capped at that height instead. An unparseable value falls back
    // to the default cap rather than erroring, since this only ever comes from a fixed Discord
    // dropdown — there's no free-text input to get wrong here.
    public static string Build(string? quality, int defaultMaxHeight)
    {
        if (quality is null)
            return HeightCapped(defaultMaxHeight);

        if (quality.Equals("best", StringComparison.OrdinalIgnoreCase))
            return "bestvideo*+bestaudio/best";

        return HeightCapped(int.TryParse(quality, out var height) ? height : defaultMaxHeight);
    }

    private static string HeightCapped(int maxHeight) =>
        $"bestvideo*[height<=?{maxHeight}]+bestaudio/best[height<=?{maxHeight}]";
}
