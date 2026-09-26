using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.QBittorrent;

// Single source of truth for "which drive do new downloads go to" — resolves both the active drive
// itself and, from it, the save path for a given /download type. Persisted so a restart doesn't
// silently revert to QBittorrent:DefaultDrive after /switch-drive picked something else.
public sealed class ActiveDriveStore
{
    private readonly IOptions<QBittorrentOptions> _options;
    private readonly ILogger<ActiveDriveStore> _logger;
    private readonly string _filePath;
    private readonly object _fileLock = new();
    private volatile string _currentDrive;

    public ActiveDriveStore(IOptions<QBittorrentOptions> options, ILogger<ActiveDriveStore> logger)
        : this(options, logger, Path.Combine("data", "active-drive.json"))
    {
    }

    public ActiveDriveStore(IOptions<QBittorrentOptions> options, ILogger<ActiveDriveStore> logger, string filePath)
    {
        _options = options;
        _logger = logger;
        _filePath = filePath;
        _currentDrive = options.Value.DefaultDrive;
        Load();
    }

    public string CurrentDrive => _currentDrive;

    public IReadOnlyCollection<string> AvailableDrives => _options.Value.SavePaths.Keys;

    // Case-insensitive so "/switch-drive e" and "/switch-drive E" both work against a "E" config key.
    public bool TrySetDrive(string drive)
    {
        var match = AvailableDrives.FirstOrDefault(d => string.Equals(d, drive, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        _currentDrive = match;
        Save();
        return true;
    }

    public bool TryGetSavePath(string type, out string savePath)
    {
        savePath = "";
        if (!_options.Value.SavePaths.TryGetValue(_currentDrive, out var pathsForDrive))
            return false;

        return pathsForDrive.TryGetValue(type, out savePath!) && !string.IsNullOrWhiteSpace(savePath);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var json = File.ReadAllText(_filePath);
            var saved = JsonSerializer.Deserialize<StoredDrive>(json);
            if (saved?.Drive is { } drive && AvailableDrives.Contains(drive, StringComparer.OrdinalIgnoreCase))
                _currentDrive = drive;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load active drive from {Path}; using default {Drive}", _filePath, _currentDrive);
        }
    }

    private void Save()
    {
        lock (_fileLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_filePath, JsonSerializer.Serialize(new StoredDrive(_currentDrive)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save active drive to {Path}", _filePath);
            }
        }
    }

    private sealed record StoredDrive(string Drive);
}
