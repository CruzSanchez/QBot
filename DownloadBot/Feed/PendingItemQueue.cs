using System.Collections.Concurrent;
using global::Discord;
using global::Discord.WebSocket;
using DownloadBot.QBittorrent;
using Microsoft.Extensions.Logging;

namespace DownloadBot.Feed;

public sealed class PendingItemQueue(IQBitApiClient qbit, DiscordSocketClient discord, ILogger<PendingItemQueue> logger)
{
    private readonly ConcurrentDictionary<string, PendingItem> _items = new();

    public void Add(PendingItem item) => _items[item.Id] = item;

    public bool Remove(string id) => _items.TryRemove(id, out _);

    public IReadOnlyCollection<PendingItem> GetAll() => _items.Values.ToList();

    public async Task RemoveExpiredAsync(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        foreach (var item in _items.Values)
        {
            if (item.AddedAt >= cutoff || !_items.TryRemove(item.Id, out _))
                continue;

            // The queue has no direct signal that qBittorrent grabbed an item, only a timer — so before
            // warning, check whether qBittorrent actually knows about it (it may have downloaded it fine).
            var alreadyAdded = false;
            if (item.InfoHash is not null)
            {
                try
                {
                    alreadyAdded = await qbit.GetTorrentStateAsync(item.InfoHash) is not null;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not check qBittorrent for \"{Title}\" while expiring it", item.Title);
                }
            }

            if (alreadyAdded)
            {
                logger.LogInformation("Removed \"{Title}\" from the feed after {Age} — qBittorrent already added it", item.Title, maxAge);
                continue;
            }

            logger.LogWarning("Expired unclaimed feed item \"{Title}\" after {Age} — qBittorrent never picked it up", item.Title, maxAge);
            await AlertAsync(item, "never got picked up by qBittorrent — check that your Auto Downloading Rule matches its title marker, and that the link is still valid.");
        }
    }

    private async Task AlertAsync(PendingItem item, string reason)
    {
        try
        {
            if (discord.GetChannel(item.ChannelId) is not IMessageChannel channel)
            {
                logger.LogWarning("Could not resolve Discord channel {ChannelId} to alert about \"{Title}\"", item.ChannelId, item.Title);
                return;
            }

            await channel.SendMessageAsync($"<@{item.UserId}> ⚠️ **{item.Title}** {reason}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Discord alert for \"{Title}\"", item.Title);
        }
    }
}
