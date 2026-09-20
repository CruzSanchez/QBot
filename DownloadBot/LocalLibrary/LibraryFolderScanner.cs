using Microsoft.Extensions.Logging;

namespace DownloadBot.LocalLibrary;

// The title/year matching core of PlexLibraryScanner, extracted so it can be tested against an
// arbitrary directory (a temp folder) without needing a real system drive — PlexLibraryScanner's
// own public API only ever scans real drives, deliberately excluding C: where test folders live.
public static class LibraryFolderScanner
{
    public static IEnumerable<string> FindMatches(string categoryPath, string title, int? year, ILogger logger)
    {
        foreach (var dir in EnumerateDirectoriesSafe(categoryPath, logger))
        {
            if (NameMatches(Path.GetFileName(dir), title, year))
                yield return dir;
        }

        foreach (var file in EnumerateFilesSafe(categoryPath, logger))
        {
            if (NameMatches(Path.GetFileNameWithoutExtension(file), title, year))
                yield return file;
        }
    }

    // Directory.GetDirectories/.GetFiles with SearchOption.AllDirectories aborts the ENTIRE call if it
    // hits even one inaccessible subfolder anywhere in the tree (permissions, a junction, a hidden
    // system folder) — on a large library that would silently zero out every result for the whole
    // category, not just the bad branch. Walking manually lets a single bad subfolder be skipped
    // instead of losing the whole scan.
    private static IEnumerable<string> EnumerateDirectoriesSafe(string root, ILogger logger)
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

    private static IEnumerable<string> EnumerateFilesSafe(string root, ILogger logger)
    {
        foreach (var dir in EnumerateDirectoriesSafe(root, logger).Prepend(root))
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
