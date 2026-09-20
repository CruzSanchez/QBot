using System.Collections.Concurrent;
using global::Discord;
using global::Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.QBittorrent;

public sealed class CompletionPollerService(
    IQBitApiClient qbit,
    DownloadTrackingStore tracking,
    DiscordSocketClient discord,
    IOptions<QBittorrentOptions> options,
    ILogger<CompletionPollerService> logger) : BackgroundService
{
    // In-memory only — deliberately not persisted. Losing this on restart just delays a stall alert
    // by at most one threshold window, which is an acceptable, simple tradeoff.
    private readonly ConcurrentDictionary<string, StallTrackingState> _stallTracking = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var download in tracking.GetAll())
            {
                await CheckOneAsync(download, stoppingToken);
            }

            await Task.Delay(interval, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task CheckOneAsync(TrackedDownload download, CancellationToken cancellationToken)
    {
        TorrentState? state;
        try
        {
            state = await qbit.GetTorrentStateAsync(download.InfoHash, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to poll qBittorrent for {Title}", download.Title);
            return;
        }

        if (state is null)
            return;

        if (state.IsError)
        {
            logger.LogWarning("Detected failure for \"{Title}\" (hash {Hash}): state={State}", download.Title, download.InfoHash, state.State);
            tracking.Untrack(download.InfoHash);
            _stallTracking.TryRemove(download.InfoHash, out _);
            await AnnounceAsync(download, $"⚠️ **{download.Title}** failed in qBittorrent (state: `{state.State}`) — check the tracker/source or remove and re-search it.");
            return;
        }

        if (state.IsComplete)
        {
            logger.LogInformation("Detected completion of \"{Title}\" (hash {Hash}): state={State} progress={Progress}",
                download.Title, download.InfoHash, state.State, state.Progress);

            tracking.Untrack(download.InfoHash);
            _stallTracking.TryRemove(download.InfoHash, out _);

            // Seed only as long as it took the bot to notice completion, then stop — the user only wants
            // seeding for as long as the bot itself needs it, not indefinitely per qBittorrent's defaults.
            try
            {
                await qbit.StopTorrentAsync(download.InfoHash, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop seeding \"{Title}\" after completion", download.Title);
            }

            await AnnounceAsync(download, $"**{download.Title}** finished downloading.");
            return;
        }

        await CheckForStallAsync(download, state);
    }

    private async Task CheckForStallAsync(TrackedDownload download, TorrentState state)
    {
        var threshold = TimeSpan.FromMinutes(Math.Max(1, options.Value.StallAlertMinutes));
        var existing = _stallTracking.GetValueOrDefault(download.InfoHash);
        var (newState, shouldAlert) = StallDetector.Evaluate(existing, state.Progress, DateTimeOffset.UtcNow, threshold);
        _stallTracking[download.InfoHash] = newState;

        if (!shouldAlert)
            return;

        logger.LogWarning("Detected stall for \"{Title}\" (hash {Hash}): state={State} progress={Progress}, no progress for over {Minutes}m",
            download.Title, download.InfoHash, state.State, state.Progress, options.Value.StallAlertMinutes);

        await AnnounceAsync(download,
            $"⏳ **{download.Title}** hasn't made progress in over {options.Value.StallAlertMinutes} minutes (state: `{state.State}`) — might be stuck (dead tracker/no seeders).");
    }

    private async Task AnnounceAsync(TrackedDownload download, string message)
    {
        if (discord.GetChannel(download.ChannelId) is not IMessageChannel channel)
        {
            logger.LogWarning("Could not resolve Discord channel {ChannelId} to announce {Title}", download.ChannelId, download.Title);
            return;
        }

        await channel.SendMessageAsync($"<@{download.UserId}> {message}");
        logger.LogInformation("Announced \"{Title}\" to channel {ChannelId}", download.Title, download.ChannelId);
    }
}
