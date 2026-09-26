namespace DownloadBot.YtDlp;

public sealed record YtDlpResult(
    bool Success,
    IReadOnlyList<string> OutputFilePaths, // one per downloaded video — more than one for a playlist
    string? ErrorMessage,
    string? StderrTail,
    // Videos yt-dlp skipped (private/deleted/age-restricted/etc.) rather than aborting the whole
    // playlist for — only meaningful when Success is true and this came from a playlist URL.
    int SkippedCount = 0);
