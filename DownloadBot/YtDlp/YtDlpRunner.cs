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
        IProgress<(double Percent, int? PlaylistIndex, int? PlaylistTotal)>? progress,
        Action? onStarted,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return new YtDlpResult(false, [], "That doesn't look like a valid http(s) URL.", null);
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return await RunAsync(url, destinationDirectory, progress, onStarted, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<YtDlpResult> RunAsync(
        string url,
        string destinationDirectory,
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
        startInfo.ArgumentList.Add(Path.Combine(destinationDirectory, "%(title)s.%(ext)s"));
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
            logger.LogWarning("yt-dlp timed out after {TimeoutMinutes}m for {Url}; killing process tree", opts.TimeoutMinutes, url);
            TryKillProcessTree(process);
            await Task.WhenAll(SafeAwait(stdoutTask), SafeAwait(stderrTask));
            return new YtDlpResult(false, [], $"Download timed out after {opts.TimeoutMinutes} minute(s).", JoinTail(stderrTail));
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
}
