using Microsoft.Extensions.Logging;

namespace DownloadBot.LocalLibrary;

public interface IPlexLibraryScanner
{
    Task<IReadOnlyList<string>> FindExistingAsync(string rawQuery, string type);
}

// Every drive except C: may have its own \plex\<category> library. Walks each one looking for a
// folder OR a bare file (some libraries are flat, one file per movie with no per-title folder)
// whose name (title + optional year, same format as the /download query) matches.
public sealed class PlexLibraryScanner(ILogger<PlexLibraryScanner> logger) : IPlexLibraryScanner
{
    // Only the folder(s) for the /download type actually picked get scanned — scanning every category
    // on every request (the old behavior) meant a large library (Music especially: far more files/folders
    // than Movies or TV) added noticeable latency to every single /download, regardless of type.
    // kids-tv lists two candidates because the actual folder naming was never confirmed (see
    // CLARIFICATIONS.md) — cheap enough to check both since it's just the one category now.
    private static readonly Dictionary<string, string[]> CategoryFoldersByType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["movie"] = ["Movies"],
        ["tv"] = ["TV Shows"],
        ["kids-movie"] = ["Kids Movies"],
        ["kids-tv"] = ["Kids Shows", "Kids Tv Shows"],
        ["music"] = ["Music"]
    };

    public Task<IReadOnlyList<string>> FindExistingAsync(string rawQuery, string type) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var (title, year) = TitleYear.Parse(rawQuery);
            var matches = new List<string>();
            var drivesChecked = 0;
            var categoriesChecked = 0;

            var categories = CategoryFoldersByType.GetValueOrDefault(type, []);
            if (categories.Length == 0)
            {
                logger.LogWarning("No library folder(s) mapped for type \"{Type}\"; skipping duplicate check", type);
                return matches;
            }

            foreach (var drive in GetCandidateDrives())
            {
                var plexRoot = Path.Combine(drive, "plex");
                if (!Directory.Exists(plexRoot))
                {
                    logger.LogDebug("No plex folder at {Path}", plexRoot);
                    continue;
                }

                drivesChecked++;

                foreach (var category in categories)
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
                "Library check for \"{RawQuery}\" (parsed as \"{Title}\" {Year}, type {Type}): {DriveCount} drive(s), {CategoryCount} categor(y/ies) scanned, {MatchCount} match(es)",
                rawQuery, title, year, type, drivesChecked, categoriesChecked, matches.Count);

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
