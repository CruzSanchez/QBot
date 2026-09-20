using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using global::Discord;
using global::Discord.WebSocket;
using DownloadBot.QBittorrent;
using Microsoft.Extensions.Logging;

namespace DownloadBot.Feed;

public sealed partial class PendingItemQueue(IQBitApiClient qbit, DiscordSocketClient discord, ILogger<PendingItemQueue> logger)
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

            if (await WasAlreadyAddedAsync(item))
            {
                logger.LogInformation("Removed \"{Title}\" from the feed after {Age} — qBittorrent already added it", item.Title, maxAge);
                continue;
            }

            logger.LogWarning("Expired unclaimed feed item \"{Title}\" after {Age} — qBittorrent never picked it up", item.Title, maxAge);
            await AlertAsync(item, "never got picked up by qBittorrent — check that your Auto Downloading Rule matches its title marker, and that the link is still valid.");
        }
    }

    // The queue has no direct push signal that qBittorrent grabbed an item, only a timer — so before
    // warning, check whether qBittorrent actually knows about it (it may have downloaded it fine).
    private async Task<bool> WasAlreadyAddedAsync(PendingItem item)
    {
        if (item.InfoHash is not null)
        {
            try
            {
                if (await qbit.GetTorrentStateAsync(item.InfoHash) is not null)
                    return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not check qBittorrent by hash for \"{Title}\" while expiring it", item.Title);
            }
        }

        // No hash, or the hash lookup didn't confirm it either way (we compute the hash ourselves
        // before qBittorrent ever sees the item, so a computation failure there says nothing about
        // whether qBittorrent's own RSS Auto Downloading Rule still grabbed the raw feed link fine).
        // Fall back to a fuzzy name match against qBittorrent's full torrent list.
        try
        {
            var strippedTitle = StripMarker(item.Title);
            var all = await qbit.GetAllTorrentsAsync();
            return all.Any(t => NamesLooselyMatch(t.Name, strippedTitle));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not check qBittorrent by name for \"{Title}\" while expiring it", item.Title);
            return false;
        }
    }

    private static string StripMarker(string title) => MarkerPrefixRegex().Replace(title, "");

    private static bool NamesLooselyMatch(string qbitName, string ourTitle)
    {
        var a = AlphaNumericOnly(qbitName);
        var b = AlphaNumericOnly(ourTitle);
        return a.Length > 0 && b.Length > 0 &&
               (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    private static string AlphaNumericOnly(string value) =>
        new([.. value.Where(char.IsLetterOrDigit)]);

    [GeneratedRegex(@"^\[DLBOT-[A-Z-]+\]\s*")]
    private static partial Regex MarkerPrefixRegex();

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
