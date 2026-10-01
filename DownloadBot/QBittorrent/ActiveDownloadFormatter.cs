namespace DownloadBot.QBittorrent;

public static class ActiveDownloadFormatter
{
    // qBittorrent reports 8640000 seconds (100 days) as its "unknown/infinite" ETA sentinel.
    private const long UnknownEtaSentinel = 8_640_000;

    public static string FormatSpeed(long bytesPerSec) =>
        bytesPerSec > 0 ? $"{bytesPerSec / 1024.0 / 1024.0:F2} MB/s" : "stalled";

    // Appends qBittorrent's raw state (e.g. "metaDL") as a plain-language reason once speed drops to
    // zero, so "stalled" doesn't look identical whether it's waiting on peers, checking files, etc.
    public static string FormatSpeedWithState(long bytesPerSec, string state)
    {
        var speedText = FormatSpeed(bytesPerSec);
        if (bytesPerSec > 0)
            return speedText;

        var label = FormatStateLabel(state);
        return label.Equals("downloading", StringComparison.OrdinalIgnoreCase) ? speedText : $"{speedText} — {label}";
    }

    public static string FormatStateLabel(string state) => state switch
    {
        "metaDL" or "forcedMetaDL" => "downloading metadata",
        "checkingDL" or "checkingUP" or "checkingResumeData" => "checking files",
        "queuedDL" or "queuedUP" => "queued",
        "allocating" => "allocating space",
        "pausedDL" or "pausedUP" => "paused",
        "stalledDL" => "no peers",
        "downloading" or "forcedDL" => "downloading",
        "stalledUP" or "uploading" or "forcedUP" => "seeding",
        "moving" => "moving files",
        "missingFiles" => "missing files",
        "error" => "error",
        _ => state
    };

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
