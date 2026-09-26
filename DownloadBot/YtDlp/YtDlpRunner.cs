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
            return await RunAsync(url, destinationDirectory, folderName, progress, onStarted, cancellationToken);
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
        IProgress<(double Percent, int? PlaylistIndex, int? PlaylistTotal)>? progress,
        Action? onStarted,
        CancellationToken cancellationToken)
    {
        var opts = options.Value;

        var startInfo = new ProcessStartInfo
        {
            FileName = opts.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(opts.Format);
        startInfo.ArgumentList.Add("--merge-output-format");
        startInfo.ArgumentList.Add(opts.MergeOutputFormat);
        startInfo.ArgumentList.Add("--max-downloads");
        startInfo.ArgumentList.Add(opts.MaxDownloadsPerInvocation.ToString());
        startInfo.ArgumentList.Add("-o");
        // Plex's scanners generally expect a video to sit in its own folder rather than a flat pile of
        // files in one directory, or it may not show up in the library at all. Default (no folderName)
        // gives each video its own folder named after its title; passing folderName instead groups
        // several related videos together (e.g. as one Plex "show"/season).
        var folderComponent = folderName is null ? "%(title)s" : SanitizeFolderName(folderName);
        startInfo.ArgumentList.Add(Path.Combine(destinationDirectory, folderComponent, "%(title)s.%(ext)s"));
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add("after_move:filepath");
        startInfo.ArgumentList.Add("--newline");
        if (!string.IsNullOrWhiteSpace(opts.FfmpegLocation))
        {
            startInfo.ArgumentList.Add("--ffmpeg-location");
            startInfo.ArgumentList.Add(opts.FfmpegLocation);
        }
        // Literal separator: without it, a URL string that happened to start with "-" could be
        // parsed by yt-dlp as an option instead of a positional argument.
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(url);

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

        var stdoutTask = ReadStreamAsync(process.StandardOutput, line =>
        {
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
            stderrTail.Enqueue(line);
            while (stderrTail.Count > MaxStderrTailLines)
                stderrTail.Dequeue();
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, opts.TimeoutMinutes)));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // The linked token fires for two different reasons — the caller's own token (a user hit
            // Cancel) or the timeout (CancelAfter) — distinguish them so the reported reason is accurate.
            var wasUserCancelled = cancellationToken.IsCancellationRequested;
            logger.LogWarning("yt-dlp {Reason} for {Url}; killing process tree",
                wasUserCancelled ? "was cancelled" : $"timed out after {opts.TimeoutMinutes}m", url);
            TryKillProcessTree(process);
            await Task.WhenAll(SafeAwait(stdoutTask), SafeAwait(stderrTask));
            return wasUserCancelled
                ? new YtDlpResult(false, [], "Cancelled.", JoinTail(stderrTail))
                : new YtDlpResult(false, [], $"Download timed out after {opts.TimeoutMinutes} minute(s).", JoinTail(stderrTail));
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
        {
            logger.LogWarning("yt-dlp exited with code {ExitCode} for {Url}", process.ExitCode, url);
            return new YtDlpResult(false, [], "yt-dlp exited with an error.", JoinTail(stderrTail));
        }

        logger.LogInformation("yt-dlp finished for {Url}: {Count} file(s)", url, outputFilePaths.Count);
        return new YtDlpResult(true, outputFilePaths, null, null);
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

    // folderName comes straight from a Discord user and ends up as a path component passed to an
    // external process — strip path separators and any other filename-invalid characters so it can
    // only ever be a single flat folder name, never a way to escape destinationDirectory (e.g. via
    // "..\..\Windows") or inject additional path segments.
    private static string SanitizeFolderName(string folderName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(folderName.Where(c => !invalid.Contains(c)).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "%(title)s" : cleaned;
    }
}
