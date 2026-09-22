namespace DownloadBot.QBittorrent;

public static class ActiveDownloadFormatter
{
    // qBittorrent reports 8640000 seconds (100 days) as its "unknown/infinite" ETA sentinel.
    private const long UnknownEtaSentinel = 8_640_000;

    public static string FormatSpeed(long bytesPerSec) =>
        bytesPerSec > 0 ? $"{bytesPerSec / 1024.0 / 1024.0:F2} MB/s" : "stalled";

    public static string FormatEta(long etaSeconds)
    {
        if (etaSeconds <= 0 || etaSeconds >= UnknownEtaSentinel)
            return "unknown ETA";

        var eta = TimeSpan.FromSeconds(etaSeconds);
        return eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}h {eta.Minutes}m left" : $"{eta.Minutes}m {eta.Seconds}s left";
    }

    public static string FormatProgressBar(double progress, int width = 20)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        var filled = (int)Math.Round(clamped * width);
        return $"[{new string('█', filled)}{new string('░', width - filled)}]";
    }
}
