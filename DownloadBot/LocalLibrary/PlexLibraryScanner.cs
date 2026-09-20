using Microsoft.Extensions.Logging;

namespace DownloadBot.LocalLibrary;

public interface IPlexLibraryScanner
{
    Task<IReadOnlyList<string>> FindExistingAsync(string rawQuery);
}

// Every drive except C: may have its own \plex\<category> library. Walks each one looking for a
// folder OR a bare file (some libraries are flat, one file per movie with no per-title folder)
// whose name (title + optional year, same format as the /download query) matches.
public sealed class PlexLibraryScanner(ILogger<PlexLibraryScanner> logger) : IPlexLibraryScanner
{
    private static readonly string[] CategoryFolders =
    [
        "TV Shows", "Kids Shows", "Kids Tv Shows", "Movies", "Kids Movies"
    ];

    public Task<IReadOnlyList<string>> FindExistingAsync(string rawQuery) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var (title, year) = TitleYear.Parse(rawQuery);
            var matches = new List<string>();
            var drivesChecked = 0;
            var categoriesChecked = 0;

            foreach (var drive in GetCandidateDrives())
            {
                var plexRoot = Path.Combine(drive, "plex");
                if (!Directory.Exists(plexRoot))
                {
                    logger.LogDebug("No plex folder at {Path}", plexRoot);
                    continue;
                }

                drivesChecked++;

                foreach (var category in CategoryFolders)
                {
                    var categoryPath = Path.Combine(plexRoot, category);
                    if (!Directory.Exists(categoryPath))
                        continue;

                    categoriesChecked++;
                    var before = matches.Count;
                    matches.AddRange(LibraryFolderScanner.FindMatches(categoryPath, title, year, logger));
                    logger.LogDebug("Scanned {Path}: {MatchCount} match(es)", categoryPath, matches.Count - before);
                }
            }

            logger.LogInformation(
                "Library check for \"{RawQuery}\" (parsed as \"{Title}\" {Year}): {DriveCount} drive(s), {CategoryCount} categor(y/ies) scanned, {MatchCount} match(es)",
                rawQuery, title, year, drivesChecked, categoriesChecked, matches.Count);

            return matches;
        });

    private IEnumerable<string> GetCandidateDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not enumerate drives for the library check");
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
