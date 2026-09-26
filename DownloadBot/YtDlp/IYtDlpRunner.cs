namespace DownloadBot.YtDlp;

public interface IYtDlpRunner
{
    Task<YtDlpResult> DownloadAsync(
        string url,
        string destinationDirectory,
        IProgress<(double Percent, int? PlaylistIndex, int? PlaylistTotal)>? progress,
        Action? onStarted,
        CancellationToken cancellationToken);
}
