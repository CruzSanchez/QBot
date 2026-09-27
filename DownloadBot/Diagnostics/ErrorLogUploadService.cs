using global::Discord;
using global::Discord.WebSocket;
using DownloadBot.Discord;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.Diagnostics;

// Whenever anything in the app logs an Error (or worse), waits 2 minutes — long enough to let a burst
// of related errors settle instead of uploading once per error — then uploads that day's log file to
// Discord:StatusChannelId (the same channel connect/disconnect notices already go to) so it's visible
// without needing server access. Only one upload timer runs at a time; further errors arriving during
// that window don't restart or stack additional timers.
public sealed class ErrorLogUploadService(
    DiscordSocketClient client,
    IOptions<DiscordOptions> options,
    ILogger<ErrorLogUploadService> logger) : BackgroundService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private bool _uploadPending;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnErrorLogged()
        {
            lock (_gate)
            {
                if (_uploadPending)
                    return; // already have an upload scheduled — don't restart or stack another

                _uploadPending = true;
            }

            _ = RunDelayedUploadAsync(stoppingToken);
        }

        ErrorSignal.ErrorLogged += OnErrorLogged;
        stoppingToken.Register(() => ErrorSignal.ErrorLogged -= OnErrorLogged);

        return Task.CompletedTask;
    }

    private async Task RunDelayedUploadAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(DebounceDelay, stoppingToken);
            await UploadTodaysLogAsync();
        }
        catch (OperationCanceledException)
        {
            // Host shutting down before the delay elapsed — nothing to upload to.
        }
        finally
        {
            lock (_gate)
            {
                _uploadPending = false;
            }
        }
    }

    private async Task UploadTodaysLogAsync()
    {
        var channelId = options.Value.StatusChannelId;
        if (channelId is null)
            return;

        if (client.GetChannel(channelId.Value) is not IMessageChannel channel)
        {
            logger.LogWarning("Could not resolve status channel {ChannelId} to upload the error log", channelId);
            return;
        }

        var logPath = ErrorLogFileLocator.GetTodaysLogFilePath(DateTime.Now);
        if (!File.Exists(logPath))
        {
            logger.LogWarning("Expected log file {Path} not found; skipping error-log upload", logPath);
            return;
        }

        try
        {
            // FileShare.ReadWrite: Serilog's own file sink still holds this file open for writing.
            await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await channel.SendFileAsync(stream, Path.GetFileName(logPath), "⚠️ An error was logged — attaching today's log file.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload the error log file to Discord");
        }
    }
}
