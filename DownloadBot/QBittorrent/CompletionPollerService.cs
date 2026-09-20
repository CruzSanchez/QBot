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
            tracking.Untrack(download.InfoHash);
            await AnnounceAsync(download, $"⚠️ **{download.Title}** failed in qBittorrent (state: `{state.State}`) — check the tracker/source or remove and re-search it.");
            return;
        }

        if (!state.IsComplete)
            return;

        tracking.Untrack(download.InfoHash);
        await AnnounceAsync(download, $"**{download.Title}** finished downloading.");
    }

    private async Task AnnounceAsync(TrackedDownload download, string message)
    {
        if (discord.GetChannel(download.ChannelId) is not IMessageChannel channel)
        {
            logger.LogWarning("Could not resolve Discord channel {ChannelId} to announce {Title}", download.ChannelId, download.Title);
            return;
        }

        await channel.SendMessageAsync($"<@{download.UserId}> {message}");
    }
}
