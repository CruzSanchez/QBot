using Microsoft.Extensions.Logging;

namespace DownloadBot.LocalLibrary;

// The title/year matching core of PlexLibraryScanner, extracted so it can be tested against an
// arbitrary directory (a temp folder) without needing a real system drive — PlexLibraryScanner's
// own public API only ever scans real drives, deliberately excluding C: where test folders live.
//
// Only checks the category folder's immediate children, never recurses into what a title's own
// folder contains (season folders, extras, individual tracks, etc.) — a Plex library is one
// folder-or-file per title at that top level, so there's nothing to match further down anyway.
// Recursing the whole tree used to mean every /download request walked the entire library
// (every file, every nested folder), which got slow fast on a large library like Music.
public static class LibraryFolderScanner
{
    public static IEnumerable<string> FindMatches(string categoryPath, string title, int? year, ILogger logger)
    {
        foreach (var dir in EnumerateTopLevelDirectoriesSafe(categoryPath, logger))
        {
            if (NameMatches(Path.GetFileName(dir), title, year))
                yield return dir;
        }

        foreach (var file in EnumerateTopLevelFilesSafe(categoryPath, logger))
        {
            if (NameMatches(Path.GetFileNameWithoutExtension(file), title, year))
                yield return file;
        }
    }

    private static IEnumerable<string> EnumerateTopLevelDirectoriesSafe(string root, ILogger logger)
    {
        string[] subdirs;
        try
        {
            subdirs = Directory.GetDirectories(root);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not list directories under {Path}", root);
            yield break;
        }

        foreach (var subdir in subdirs)
            yield return subdir;
    }

    private static IEnumerable<string> EnumerateTopLevelFilesSafe(string root, ILogger logger)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(root);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not list files under {Path}", root);
            yield break;
        }

        foreach (var file in files)
            yield return file;
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
