namespace DownloadBot.YtDlp;

public sealed class YtDlpOptions
{
    // "yt-dlp" assumes it's on PATH. Set to an absolute path (e.g.
    // "C:\\Users\\johnb\\Desktop\\repos\\yt-dlp.exe") if it isn't.
    public string ExecutablePath { get; set; } = "yt-dlp";

    // Passed to yt-dlp as --ffmpeg-location when set (either the ffmpeg binary itself or its
    // containing folder). Leave blank to let yt-dlp find ffmpeg on PATH itself.
    public string FfmpegLocation { get; set; } = "";

    // Resolution cap applied when /download-yt's "quality" option is left unset — 1080p unless
    // explicitly raised (e.g. to 4K) per download. YtDlpRunner builds the actual -f format selector
    // from this plus whatever the command was given.
    public int DefaultMaxHeight { get; set; } = 1080;

    // Container yt-dlp remuxes/merges into via --merge-output-format.
    public string MergeOutputFormat { get; set; } = "mp4";

    // Hard cap on one /download-yt invocation's wall-clock time (single video or whole
    // playlist) — the process (and its ffmpeg child) is killed if exceeded.
    public int TimeoutMinutes { get; set; } = 30;

    // Passed as --max-downloads — caps a playlist URL at this many videos so an accidental
    // (or huge) playlist link can't run unbounded. A single video always counts as 1.
    public int MaxDownloadsPerInvocation { get; set; } = 25;
}
