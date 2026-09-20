using Microsoft.Extensions.Logging;

namespace DownloadBot.LocalLibrary;

public interface IPlexLibraryScanner
{
    Task<IReadOnlyList<string>> FindExistingAsync(string rawQuery);
}

// Every drive except C: may have its own \plex\<category> library. Walks each one looking for a
// folder whose name (title + optional year, same format as the /download query) matches.
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

            foreach (var drive in GetCandidateDrives())
            {
                var plexRoot = Path.Combine(drive, "plex");
                if (!Directory.Exists(plexRoot))
                    continue;

                foreach (var category in CategoryFolders)
                {
                    var categoryPath = Path.Combine(plexRoot, category);
                    if (!Directory.Exists(categoryPath))
                        continue;

                    matches.AddRange(FindMatchesUnder(categoryPath, title, year));
                }
            }

            return matches;
        });

    private static IEnumerable<string> GetCandidateDrives()
    {
        IEnumerable<DriveInfo> drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            if (!drive.IsReady)
                continue;
            // C: is always the OS drive here and never hosts a plex library.
            if (drive.Name.TrimEnd('\\').Equals("C:", StringComparison.OrdinalIgnoreCase))
                continue;

            yield return drive.RootDirectory.FullName;
        }
    }

    private IEnumerable<string> FindMatchesUnder(string categoryPath, string title, int? year)
    {
        string[] entries;
        try
        {
            entries = Directory.GetDirectories(categoryPath, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not scan {Path} for existing titles", categoryPath);
            return [];
        }

        return entries.Where(dir =>
        {
            var (candidateTitle, candidateYear) = TitleYear.Parse(Path.GetFileName(dir));
            if (!string.Equals(candidateTitle, title, StringComparison.OrdinalIgnoreCase))
                return false;
            // Same title, different year (a remake/reboot) doesn't count as already having it.
            return year is null || candidateYear is null || year == candidateYear;
        });
    }
}
