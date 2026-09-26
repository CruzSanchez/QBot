namespace DownloadBot.YtDlp;

public sealed record YtDlpResult(
    bool Success,
    IReadOnlyList<string> OutputFilePaths, // one per downloaded video — more than one for a playlist
    string? ErrorMessage,
    string? StderrTail);
