using System.Collections.Concurrent;

namespace DownloadBot.QBittorrent;

public sealed record TrackedDownload(string InfoHash, string Title, ulong ChannelId, ulong UserId);

public sealed class DownloadTrackingStore
{
    private readonly ConcurrentDictionary<string, TrackedDownload> _tracked = new(StringComparer.OrdinalIgnoreCase);

    public void Track(TrackedDownload download) => _tracked[download.InfoHash] = download;

    public bool Untrack(string infoHash) => _tracked.TryRemove(infoHash, out _);

    public IReadOnlyCollection<TrackedDownload> GetAll() => _tracked.Values.ToList();
}
