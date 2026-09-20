using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DownloadBot.QBittorrent;

public sealed record TrackedDownload(string InfoHash, string Title, ulong ChannelId, ulong UserId);

public sealed class DownloadTrackingStore
{
    private readonly ConcurrentDictionary<string, TrackedDownload> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _filePath;
    private readonly object _fileLock = new();
    private readonly ILogger<DownloadTrackingStore> _logger;

    // Relative path, resolved against the working directory at process start — same convention as
    // "logs/downloadbot-.log" in Program.cs, deliberately not AppContext.BaseDirectory (which
    // resolves into bin/ and gets wiped on every rebuild).
    public DownloadTrackingStore(ILogger<DownloadTrackingStore> logger) : this(logger, Path.Combine("data", "tracked-downloads.json"))
    {
    }

    // Seam for tests to point at a temp file instead of the real data folder.
    public DownloadTrackingStore(ILogger<DownloadTrackingStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
        Load();
    }

    public void Track(TrackedDownload download)
    {
        _tracked[download.InfoHash] = download;
        Save();
    }

    public bool Untrack(string infoHash)
    {
        var removed = _tracked.TryRemove(infoHash, out _);
        if (removed)
            Save();
        return removed;
    }

    public IReadOnlyCollection<TrackedDownload> GetAll() => _tracked.Values.ToList();

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var json = File.ReadAllText(_filePath);
            var items = JsonSerializer.Deserialize<List<TrackedDownload>>(json) ?? [];
            foreach (var item in items)
                _tracked[item.InfoHash] = item;

            _logger.LogInformation("Loaded {Count} tracked download(s) from {Path}", items.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tracked downloads from {Path}; starting empty", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            lock (_fileLock)
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var json = JsonSerializer.Serialize(_tracked.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist tracked downloads to {Path}", _filePath);
        }
    }
}
