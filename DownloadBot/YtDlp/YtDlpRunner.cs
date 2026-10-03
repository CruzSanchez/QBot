using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.YtDlp;

// The first (and only) place in this codebase that shells out to an external process — a Discord
// user's raw URL string ultimately reaches a child process, so every step here is deliberately
// built around that: validate before touching Process at all, never go through a shell, and never
// let the URL be misread as a flag.
public sealed class YtDlpRunner(IOptions<YtDlpOptions> options, ILogger<YtDlpRunner> logger) : IYtDlpRunner
{
    // Only one yt-dlp/ffmpeg invocation at a time — this server already runs qBittorrent and Plex,
    // and two unthrottled downloads/re-mux jobs at once would compete directly for the same
    // disk/NIC/CPU with no scheduler smoothing it out (unlike qBittorrent, which throttles itself).
    // A plain WaitAsync (no zero-timeout check) makes a second call queue instead of running in
    // parallel or being rejected.
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private const int MaxStderrTailLines = 20;

    public async Task<YtDlpResult> DownloadAsync(
        string url,
        string destinationDirectory,
        string? folderName,
        string? quality,
        int? maxDownloads,
        IProgress<(double Percent, int? PlaylistIndex, int? PlaylistTotal)>? progress,
        Action? onStarted,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return new YtDlpResult(false, [], "That doesn't look like a valid http(s) URL.", null);
        }

        try
        {
            await _semaphore.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancelled while still queued behind another download — never actually started.
            return new YtDlpResult(false, [], "Cancelled.", null);
        }

        try
        {
            return await RunAsync(url, destinationDirectory, folderName, quality, maxDownloads, progress, onStarted, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<YtDlpResult> RunAsync(
        string url,
        string destinationDirectory,
        string? folderName,
        string? quality,
        int? maxDownloads,
        IProgress<(double Percent, int? PlaylistIndex, int? PlaylistTotal)>? progress,
        Action? onStarted,
        CancellationToken cancellationToken)
    {
        var opts = options.Value;

        // yt-dlp creates the archive file itself if missing, but not its parent directory.
        if (!string.IsNullOrWhiteSpace(opts.DownloadArchivePath))
        {
            var archiveDir = Path.GetDirectoryName(Path.GetFullPath(opts.DownloadArchivePath));
            if (!string.IsNullOrEmpty(archiveDir))
                Directory.CreateDirectory(archiveDir);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = opts.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in YtDlpArgumentBuilder.Build(url, destinationDirectory, folderName, quality, maxDownloads, opts))
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            logger.LogError(ex, "Could not start yt-dlp executable {ExecutablePath}", opts.ExecutablePath);
            return new YtDlpResult(false, [],
                $"Could not start yt-dlp ('{opts.ExecutablePath}') — is it installed and on PATH, or is YtDlp:ExecutablePath set correctly?",
                ex.Message);
        }

        onStarted?.Invoke();

        var outputFilePaths = new List<string>();
        var stderrTail = new Queue<string>();
        var skippedCount = 0;

        // Inactivity timeout: every line yt-dlp prints (progress, ERROR:, anything) pushes the deadline
        // back, so only a genuinely silent/hung process gets killed — not a long playlist that's working.
        var inactivityLimit = TimeSpan.FromMinutes(Math.Max(1, opts.TimeoutMinutes));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(inactivityLimit);

        var stdoutTask = ReadStreamAsync(process.StandardOutput, line =>
        {
            timeoutCts.CancelAfter(inactivityLimit);
            var percent = YtDlpProgressParser.TryParsePercent(line);
            var position = YtDlpProgressParser.TryParsePlaylistPosition(line);

            if (percent is not null || position is not null)
            {
                progress?.Report((percent ?? 0, position?.Index, position?.Total));
            }
            else if (line.StartsWith(destinationDirectory, StringComparison.OrdinalIgnoreCase))
            {
                outputFilePaths.Add(line.Trim());
            }
            else if (line.Contains("has already been recorded", StringComparison.OrdinalIgnoreCase))
            {
                // Expected, benign --download-archive skip message for a video downloaded on a
                // previous run — not an unrecognized-format case worth flagging below.
            }
            else if (line.Contains("[download]", StringComparison.Ordinal))
            {
                // A "[download]" line that isn't a recognized progress/playlist/output-path line —
                // logged so an unexpected yt-dlp output format shows up here instead of just looking
                // like frozen progress.
                logger.LogInformation("yt-dlp line not recognized as progress: {Line}", line);
            }
        });

        var stderrTask = ReadStreamAsync(process.StandardError, line =>
        {
            timeoutCts.CancelAfter(inactivityLimit);
            stderrTail.Enqueue(line);
            while (stderrTail.Count > MaxStderrTailLines)
                stderrTail.Dequeue();

            // Each unavailable/private/restricted video in a playlist logs its own "ERROR: ..." line
            // (that's --ignore-errors working as intended) — counted so the caller can say how many
            // were skipped instead of just going quiet about it.
            if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                skippedCount++;
        });

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // The linked token fires for two different reasons — the caller's own token (a user hit
            // Cancel) or the inactivity timeout (CancelAfter) — distinguish them so the reported reason is accurate.
            var wasUserCancelled = cancellationToken.IsCancellationRequested;
            logger.LogWarning("yt-dlp {Reason} for {Url}; killing process tree ({Count} file(s) finished so far)",
                wasUserCancelled ? "was cancelled" : $"produced no output for {opts.TimeoutMinutes}m", url, outputFilePaths.Count);
            TryKillProcessTree(process);
            await Task.WhenAll(SafeAwait(stdoutTask), SafeAwait(stderrTask));
            return wasUserCancelled
                ? new YtDlpResult(false, [], "Cancelled.", JoinTail(stderrTail))
                : new YtDlpResult(false, [],
                    $"yt-dlp went silent for {opts.TimeoutMinutes} minute(s) and was stopped ({outputFilePaths.Count} file(s) had finished). " +
                    "Run the same URL again to continue — already-downloaded videos are skipped.",
                    JoinTail(stderrTail));
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        // yt-dlp's exit code reflects "at least one error happened," which --ignore-errors deliberately
        // doesn't prevent — it only prevents one bad video from stopping the rest. So a nonzero exit
        // code alongside actual downloaded files is a partial success (some playlist entries were
        // unavailable), not a failure; only treat it as a real failure when nothing came out of it at all.
        if (outputFilePaths.Count == 0)
        {
            logger.LogWarning("yt-dlp exited with code {ExitCode} for {Url} and produced no files", process.ExitCode, url);
            return new YtDlpResult(false, [], "yt-dlp exited with an error.", JoinTail(stderrTail));
        }

        if (process.ExitCode != 0)
        {
            logger.LogWarning("yt-dlp exited with code {ExitCode} for {Url} but {Count} file(s) still downloaded ({Skipped} skipped)",
                process.ExitCode, url, outputFilePaths.Count, skippedCount);
        }
        else
        {
            logger.LogInformation("yt-dlp finished for {Url}: {Count} file(s)", url, outputFilePaths.Count);
        }

        return new YtDlpResult(true, outputFilePaths, null, null, skippedCount);
    }

    private static async Task ReadStreamAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            onLine(line);
        }
    }

    private static async Task SafeAwait(Task task)
    {
        try { await task; } catch { /* best-effort drain after a kill; ignore */ }
    }

    private void TryKillProcessTree(Process process)
    {
        try
        {
            // yt-dlp spawns ffmpeg as a child for merging — killing only the parent would orphan it.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to kill yt-dlp process tree after timeout");
        }
    }

    private static string? JoinTail(Queue<string> lines) =>
        lines.Count == 0 ? null : string.Join('\n', lines);
}
