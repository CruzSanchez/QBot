using System.ComponentModel;
using System.Diagnostics;
using DownloadBot.YtDlp;
using Microsoft.Extensions.Configuration;

namespace DownloadBot.Tests.Integration;

// Only checks that the configured yt-dlp executable is actually invocable (--version), not a real
// download — same reasoning as why /download's tests never add a real torrent: an actual download
// would be network-dependent, slow, write real files needing cleanup, and isn't needed to prove the
// process-wrapper logic (argument construction, stdout/stderr piping, exit code handling) works.
// Skips (does not fail) when the configured executable isn't reachable, so `dotnet test` stays quiet
// on a machine where yt-dlp isn't installed.
public class YtDlpRunnerIntegrationTests
{
    private static YtDlpOptions LoadOptions()
    {
        var options = new YtDlpOptions();
        TestConfiguration.Root.GetSection("YtDlp").Bind(options);
        return options;
    }

    [SkippableFact]
    public async Task ConfiguredExecutable_IsInvocable()
    {
        var options = LoadOptions();
        Skip.If(!File.Exists(options.ExecutablePath), $"yt-dlp ('{options.ExecutablePath}') not found — skipping.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.ExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--version");

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                $"yt-dlp ('{options.ExecutablePath}') not found — skipping. ({ex.Message})");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdout));
    }
}
