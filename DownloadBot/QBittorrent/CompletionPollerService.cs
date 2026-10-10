using System.Collections.Concurrent;
using global::Discord;
using global::Discord.WebSocket;
using DownloadBot.Discord;
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
                // Never let one download's failure (e.g. Discord unreachable while announcing) escape
                // the loop — an unhandled exception here stops the whole host.
                try
                {
                    await CheckOneAsync(download, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Unexpected failure checking {Title}; will retry next poll", download.Title);
                }
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
            // Warning, not Error: this fires routinely for a few seconds right after the bot starts if
            // qBittorrent's WebUI isn't up yet, and self-heals on the next poll — Error would trigger the
            // error-log-upload feature for a non-issue. A genuinely dead qBittorrent still shows up here
            // every poll cycle, just as repeated warnings instead of an upload-triggering error.
            logger.LogWarning(ex, "Failed to poll qBittorrent for {Title}", download.Title);
            return;
        }

        if (state is null)
            return;

        if (state.IsError)
        {
            logger.LogWarning("Detected failure for \"{Title}\" (hash {Hash}): state={State}", download.Title, download.InfoHash, state.State);

            // Stays tracked until the alert actually goes out, so a Discord outage retries it next poll
            // instead of silently losing it.
            if (!await AnnounceAsync(download, "Failed",
                    $"**{download.Title}** — state: `{state.State}`\nCheck the tracker/source, or remove and re-search it.",
                    DashboardFormatter.RedColor, includeCancelButton: true))
                return;

            tracking.Untrack(download.InfoHash);
            _stallTracking.TryRemove(download.InfoHash, out _);
            return;
        }

        if (state.IsComplete)
        {
            logger.LogInformation("Detected completion of \"{Title}\" (hash {Hash}): state={State} progress={Progress}",
                download.Title, download.InfoHash, state.State, state.Progress);

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

            // Untracked only once announced, so an outage retries the ping next poll (stopping the
            // torrent again meanwhile is harmless).
            if (!await AnnounceAsync(download, "Finished downloading", $"**{download.Title}**",
                    DashboardFormatter.GreenColor, includeCancelButton: false))
                return;

            tracking.Untrack(download.InfoHash);
            _stallTracking.TryRemove(download.InfoHash, out _);
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

        await AnnounceAsync(download, "Might be stuck",
            $"**{download.Title}** — no progress in over {options.Value.StallAlertMinutes} minutes (state: `{state.State}`)\n" +
            "Dead tracker or no seeders — check it or cancel.",
            DashboardFormatter.YellowColor, includeCancelButton: true);
    }

    // Returns false only when the send itself failed (worth retrying). An unresolvable channel returns
    // true: retrying forever wouldn't help, so the download is treated as handled.
    private async Task<bool> AnnounceAsync(TrackedDownload download, string title, string description, Color color, bool includeCancelButton)
    {
        if (discord.GetChannel(download.ChannelId) is not IMessageChannel channel)
        {
            logger.LogWarning("Could not resolve Discord channel {ChannelId} to announce {Title}", download.ChannelId, download.Title);
            return true;
        }

        var embed = new EmbedBuilder()
            .WithColor(color)
            .WithTitle(title)
            .WithDescription(description)
            .WithCurrentTimestamp()
            .Build();

        // Lets whoever sees the alert jump straight into the same remove/remove+delete/nevermind flow
        // /cancel uses, without having to run a separate command and re-find this exact torrent.
        var components = includeCancelButton
            ? new ComponentBuilder().WithButton("Cancel this download", $"poller-cancel:{download.InfoHash}", ButtonStyle.Secondary).Build()
            : null;

        try
        {
            await channel.SendMessageAsync($"<@{download.UserId}>", embed: embed, components: components);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to announce \"{Title}\" to channel {ChannelId}; will retry next poll", download.Title, download.ChannelId);
            return false;
        }

        logger.LogInformation("Announced \"{Title}\" to channel {ChannelId}", download.Title, download.ChannelId);
        return true;
    }
}
