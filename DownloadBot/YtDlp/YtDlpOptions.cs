namespace DownloadBot.YtDlp;

public sealed class YtDlpOptions
{
    // "yt-dlp" assumes it's on PATH. Set to an absolute path (e.g.
    // "C:\\Users\\johnb\\Desktop\\repos\\yt-dlp.exe") if it isn't.
    public string ExecutablePath { get; set; } = "yt-dlp";

    // Passed to yt-dlp as --ffmpeg-location when set (either the ffmpeg binary itself or its
    // containing folder). Leave blank to let yt-dlp find ffmpeg on PATH itself.
    public string FfmpegLocation { get; set; } = "";

    // yt-dlp -f format selector. Best video+audio, falling back to best combined stream.
    public string Format { get; set; } = "bestvideo*+bestaudio/best";

    // Container yt-dlp remuxes/merges into via --merge-output-format.
    public string MergeOutputFormat { get; set; } = "mp4";

    // Hard cap on one /download-yt invocation's wall-clock time (single video or whole
    // playlist) — the process (and its ffmpeg child) is killed if exceeded.
    public int TimeoutMinutes { get; set; } = 30;

    // Passed as --max-downloads — caps a playlist URL at this many videos so an accidental
    // (or huge) playlist link can't run unbounded. A single video always counts as 1.
    public int MaxDownloadsPerInvocation { get; set; } = 25;
}
