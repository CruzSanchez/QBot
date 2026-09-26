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

    // Path to a Netscape-format cookies.txt, passed as --cookies. Blank (default) means no
    // authentication — fine for public videos, but age-restricted/members-only/private content needs
    // this exported from a real logged-in browser session and placed on the server manually.
    public string CookiesFilePath { get; set; } = "";

    // Passed as --download-archive — a running log of every video ID ever downloaded, so re-running
    // the same channel/playlist URL later only grabs new videos instead of re-downloading everything.
    // Blank disables it. Same data/ convention as DownloadTrackingStore/ActiveDriveStore.
    public string DownloadArchivePath { get; set; } = "data/yt-dlp-archive.txt";

    // Comma-separated SponsorBlock categories passed to --sponsorblock-remove (e.g. "sponsor" or
    // "sponsor,selfpromo,interaction"). Blank disables it. Failing to reach SponsorBlock's API just
    // skips removal for that video — it doesn't fail the download.
    public string SponsorBlockRemoveCategories { get; set; } = "sponsor";

    // Passed as both --retries and --fragment-retries. Deliberately small (not yt-dlp's own default of
    // 10, and never "infinite") — one extra attempt on a flaky connection is enough; beyond that it
    // should surface as a real failure rather than silently retrying for a long time.
    public int Retries { get; set; } = 1;
}
