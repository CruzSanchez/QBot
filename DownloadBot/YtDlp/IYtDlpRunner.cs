namespace DownloadBot.YtDlp;

public interface IYtDlpRunner
{
    // folderName: null puts the video in its own folder (named after its title) under
    // destinationDirectory — the default, right for a standalone download. Pass a name to instead
    // group it into a shared subfolder (e.g. so several related videos land together as one Plex
    // "show"/season instead of each getting its own folder).
    Task<YtDlpResult> DownloadAsync(
        string url,
        string destinationDirectory,
        string? folderName,
        IProgress<(double Percent, int? PlaylistIndex, int? PlaylistTotal)>? progress,
        Action? onStarted,
        CancellationToken cancellationToken);
}
