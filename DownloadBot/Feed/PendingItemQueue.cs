using System.Collections.Concurrent;
using DownloadBot.QBittorrent;
using Microsoft.Extensions.Logging;

namespace DownloadBot.Feed;

public sealed class PendingItemQueue(IQBitApiClient qbit, ILogger<PendingItemQueue> logger)
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
            var infoHash = MagnetHash.TryExtract(item.Link);
            var alreadyAdded = false;
            if (infoHash is not null)
            {
                try
                {
                    alreadyAdded = await qbit.GetTorrentStateAsync(infoHash) is not null;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not check qBittorrent for \"{Title}\" while expiring it", item.Title);
                }
            }

            if (alreadyAdded)
                logger.LogInformation("Removed \"{Title}\" from the feed after {Age} — qBittorrent already added it", item.Title, maxAge);
            else
                logger.LogWarning("Expired unclaimed feed item \"{Title}\" after {Age} — qBittorrent never picked it up", item.Title, maxAge);
        }
    }
}
