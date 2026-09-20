using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DownloadBot.Feed;

public sealed class PendingItemQueue(ILogger<PendingItemQueue> logger)
{
    private readonly ConcurrentDictionary<string, PendingItem> _items = new();

    public void Add(PendingItem item) => _items[item.Id] = item;

    public bool Remove(string id) => _items.TryRemove(id, out _);

    public IReadOnlyCollection<PendingItem> GetAll() => _items.Values.ToList();

    public void RemoveExpired(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        foreach (var item in _items.Values)
        {
            if (item.AddedAt < cutoff && _items.TryRemove(item.Id, out _))
            {
                logger.LogWarning("Expired unclaimed feed item \"{Title}\" after {Age} — qBittorrent never picked it up", item.Title, maxAge);
            }
        }
    }
}
