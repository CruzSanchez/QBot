using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.Plex;

public sealed class PlexOptions
{
    public string ExecutablePath { get; set; } = @"C:\Program Files\Plex\Plex Media Server\Plex Media Server.exe";

    // How long restart-plex.ps1 waits for Plex to answer on its local port after relaunching it.
    public int WaitSeconds { get; set; } = 90;
}

public interface IPlexRestarter
{
    Task<(bool Ok, string Message)> RestartAsync(CancellationToken cancellationToken = default);
}

// Runs restart-plex.ps1 (repo root, next to the DownloadBot folder). The relaunch has to go through a
// scheduled task into the desktop session, which is easier in PowerShell than in-process — see the script.
public sealed class PlexRestarter(IOptions<PlexOptions> options, IHostEnvironment env, ILogger<PlexRestarter> logger) : IPlexRestarter
{
    // A second restart while one is running would kill the Plex the first one just started.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static IReadOnlyList<string> BuildArguments(string scriptPath, string plexExe, int waitSeconds) =>
        ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-PlexExe", plexExe, "-WaitSeconds", waitSeconds.ToString()];

    public async Task<(bool Ok, string Message)> RestartAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            return (false, "A Plex restart is already in progress.");

        try
        {
            var opts = options.Value;
            var script = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "restart-plex.ps1"));
            if (!File.Exists(script))
                return (false, $"restart-plex.ps1 not found at {script}.");

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in BuildArguments(script, opts.ExecutablePath, opts.WaitSeconds))
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo)!;
            var output = Task.WhenAll(process.StandardOutput.ReadToEndAsync(cancellationToken), process.StandardError.ReadToEndAsync(cancellationToken));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(opts.WaitSeconds + 60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (false, "The restart script didn't finish in time and was stopped.");
            }

            var text = string.Concat(await output).Trim();
            var tail = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
            if (tail.Length > 400) tail = tail[..400];

            logger.LogInformation("restart-plex.ps1 exited {ExitCode}: {Output}", process.ExitCode, tail);
            return process.ExitCode == 0 ? (true, tail) : (false, tail.Length > 0 ? tail : $"Script exited with code {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Plex restart failed");
            return (false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }
}
