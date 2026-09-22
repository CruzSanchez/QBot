using System.Text.Json;
using global::Discord;
using global::Discord.WebSocket;
using DownloadBot.QBittorrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.Discord;

// Keeps one pinned-able message in Discord:DashboardChannelId continuously up to date with everything
// qBittorrent is currently downloading, instead of requiring someone to run /active-downloads to see it.
// The message id is persisted so a bot restart edits the same message rather than spamming a new one.
public sealed class DashboardService(
    IQBitApiClient qbit,
    DiscordSocketClient discord,
    IOptions<DiscordOptions> discordOptions,
    IOptions<QBittorrentOptions> qbitOptions,
    ILogger<DashboardService> logger) : BackgroundService
{
    private readonly string _stateFilePath = Path.Combine("data", "dashboard-message.json");
    private ulong? _messageId;

    private sealed record DashboardState(ulong MessageId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channelId = discordOptions.Value.DashboardChannelId;
        if (channelId is null)
        {
            logger.LogInformation("Discord:DashboardChannelId not configured — live dashboard disabled.");
            return;
        }

        LoadState();

        var interval = TimeSpan.FromSeconds(Math.Max(5, qbitOptions.Value.PollIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        do
        {
            await RefreshAsync(channelId.Value, interval, stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RefreshAsync(ulong channelId, TimeSpan interval, CancellationToken cancellationToken)
    {
        if (discord.GetChannel(channelId) is not IMessageChannel channel)
        {
            logger.LogDebug("Dashboard channel {ChannelId} not resolvable yet", channelId);
            return;
        }

        IReadOnlyList<TorrentState> all;
        try
        {
            all = await qbit.GetAllTorrentsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dashboard refresh failed to reach qBittorrent");
            return;
        }

        var active = all.Where(t => t.IsActiveDownload).ToList();
        var now = DateTimeOffset.UtcNow;
        var embed = DashboardFormatter.BuildActiveDownloadsEmbed(active, now, now + interval);

        IUserMessage? existing = null;
        if (_messageId is { } id)
        {
            try
            {
                existing = await channel.GetMessageAsync(id) as IUserMessage;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to look up dashboard message {MessageId}; will repost", id);
            }
        }

        if (existing is not null)
        {
            try
            {
                await existing.ModifyAsync(m => m.Embed = embed);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to edit dashboard message {MessageId}; reposting", existing.Id);
            }
        }

        var posted = await channel.SendMessageAsync(embed: embed);
        _messageId = posted.Id;
        SaveState();
        logger.LogInformation("Posted new dashboard message {MessageId} in channel {ChannelId}", posted.Id, channelId);
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
                return;

            var json = File.ReadAllText(_stateFilePath);
            var state = JsonSerializer.Deserialize<DashboardState>(json);
            if (state is not null)
                _messageId = state.MessageId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load dashboard state from {Path}; starting fresh", _stateFilePath);
        }
    }

    private void SaveState()
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(new DashboardState(_messageId!.Value)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save dashboard state to {Path}", _stateFilePath);
        }
    }
}
