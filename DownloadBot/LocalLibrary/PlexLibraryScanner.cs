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
                    matches.AddRange(FindMatchesUnder(categoryPath, title, year));
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

    // Directory.GetDirectories/.GetFiles with SearchOption.AllDirectories aborts the ENTIRE call if it
    // hits even one inaccessible subfolder anywhere in the tree (permissions, a junction, a hidden
    // system folder) — on a large library that would silently zero out every result for the whole
    // category, not just the bad branch. Walking manually lets a single bad subfolder be skipped
    // instead of losing the whole scan.
    private IEnumerable<string> FindMatchesUnder(string categoryPath, string title, int? year)
    {
        foreach (var dir in EnumerateDirectoriesSafe(categoryPath))
        {
            if (NameMatches(Path.GetFileName(dir), title, year))
                yield return dir;
        }

        foreach (var file in EnumerateFilesSafe(categoryPath))
        {
            if (NameMatches(Path.GetFileNameWithoutExtension(file), title, year))
                yield return file;
        }
    }

    private IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(current);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Skipping inaccessible directory {Path}", current);
                continue;
            }

            foreach (var subdir in subdirs)
            {
                yield return subdir;
                stack.Push(subdir);
            }
        }
    }

    private IEnumerable<string> EnumerateFilesSafe(string root)
    {
        foreach (var dir in EnumerateDirectoriesSafe(root).Prepend(root))
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Skipping inaccessible directory {Path} while listing files", dir);
                continue;
            }

            foreach (var file in files)
                yield return file;
        }
    }

    private static bool NameMatches(string candidateName, string title, int? year)
    {
        var (candidateTitle, candidateYear) = TitleYear.Parse(candidateName);
        if (!string.Equals(candidateTitle, title, StringComparison.OrdinalIgnoreCase))
            return false;
        // Same title, different year (a remake/reboot) doesn't count as already having it.
        return year is null || candidateYear is null || year == candidateYear;
    }
}
