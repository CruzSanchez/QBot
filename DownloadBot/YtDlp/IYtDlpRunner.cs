namespace DownloadBot.YtDlp;

public interface IYtDlpRunner
{
    // folderName: null puts the video in its own folder (named after its title) under
    // destinationDirectory — the default, right for a standalone download. Pass a name to instead
    // group it into a shared subfolder (e.g. so several related videos land together as one Plex
    // "show"/season instead of each getting its own folder).
    //
    // quality: null uses YtDlpOptions.DefaultMaxHeight (1080p unless configured otherwise); "best"
    // downloads uncapped (whatever the source's highest available is, e.g. 4K/8K); otherwise a
    // numeric string ("720", "1080", "2160", ...) caps at that height.
    Task<YtDlpResult> DownloadAsync(
        string url,
        string destinationDirectory,
        string? folderName,
        string? quality,
        IProgress<(double Percent, int? PlaylistIndex, int? PlaylistTotal)>? progress,
        Action? onStarted,
        CancellationToken cancellationToken);
}
