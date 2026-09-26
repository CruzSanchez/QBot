using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownloadBot.LocalLibrary;

public interface ILibraryCache
{
    // categoryFolderName is the literal folder name under \plex\ on some drive (e.g. "Movies") —
    // same names PlexLibraryScanner maps a /download type to.
    IReadOnlyList<(string Path, string Name)> GetEntries(string categoryFolderName);

    // Completes once the first snapshot has been built. DownloadBotService awaits this before
    // connecting to Discord/registering commands, so the bot never accepts a /download before the
    // whole library's been scanned at least once — no per-request snapshot-building, no race window.
    Task Ready { get; }
}

// Owns the actual disk scanning for the library duplicate-check, on a 12-hour refresh cycle, so a
// /download request never touches the filesystem itself — it was still noticeably slower than it
// needed to be even after trimming the scan to top-level names only, and a Plex library doesn't
// change often enough to justify re-reading it on every single request.
public sealed class LibraryCacheService(ILogger<LibraryCacheService> logger) : BackgroundService, ILibraryCache
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);

    // Swapped atomically on refresh — readers never see a partially-built snapshot and never need a
    // lock, they just read whatever the current reference is.
    private volatile Dictionary<string, IReadOnlyList<(string Path, string Name)>> _snapshot =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => _readyTcs.Task;

    public IReadOnlyList<(string Path, string Name)> GetEntries(string categoryFolderName) =>
        _snapshot.GetValueOrDefault(categoryFolderName, []);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the calling thread (Task.Run), not a direct synchronous call — StartAsync should
        // return promptly regardless of how the disk scan performs. Whoever needs the result waits
        // on Ready explicitly instead of relying on host-startup ordering to happen to block for it.
        await Task.Run(Refresh, stoppingToken);
        _readyTcs.TrySetResult();

        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await Task.Run(Refresh, stoppingToken);
        }
    }

    private void Refresh()
    {
        try
        {
            var snapshot = BuildSnapshot();
            _snapshot = snapshot;
            logger.LogInformation("Library cache refreshed: {CategoryCount} categor(y/ies), {EntryCount} total entr(y/ies)",
                snapshot.Count, snapshot.Values.Sum(v => v.Count));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Library cache refresh failed; keeping the previous snapshot");
        }
    }

    private Dictionary<string, IReadOnlyList<(string Path, string Name)>> BuildSnapshot()
    {
        var byCategory = new Dictionary<string, List<(string Path, string Name)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in GetCandidateDrives())
        {
            var plexRoot = Path.Combine(drive, "plex");
            if (!Directory.Exists(plexRoot))
                continue;

            string[] categoryDirs;
            try
            {
                categoryDirs = Directory.GetDirectories(plexRoot);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not list categories under {Path}", plexRoot);
                continue;
            }

            foreach (var categoryDir in categoryDirs)
            {
                var categoryName = Path.GetFileName(categoryDir);
                if (!byCategory.TryGetValue(categoryName, out var list))
                    byCategory[categoryName] = list = [];

                list.AddRange(LibraryFolderScanner.EnumerateTopLevelEntries(categoryDir, logger));
            }
        }

        return byCategory.ToDictionary(
            kv => kv.Key,
            IReadOnlyList<(string Path, string Name)> (kv) => kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<string> GetCandidateDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not enumerate drives for the library cache");
            yield break;
        }

        foreach (var drive in drives)
        {
            // C: is always the OS drive here and never hosts a plex library.
            if (drive.Name.TrimEnd('\\').Equals("C:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!drive.IsReady)
            {
                logger.LogDebug("Drive {Drive} is not ready, skipping", drive.Name);
                continue;
            }

            yield return drive.RootDirectory.FullName;
        }
    }
}
