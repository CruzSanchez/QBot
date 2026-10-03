namespace DownloadBot.LocalLibrary;

public sealed record PlexFolder(string Path, string Name, string Drive, string Category);

// Live (uncached) lookup of library item folders for /delete: the direct children of every
// <drive>\plex\<category> folder. Never yields a category folder itself or anything shallower or
// deeper, so a delete can only ever remove a single library item's folder.
public static class PlexFolderFinder
{
    // Same drive rule as the rest of the library code: every ready drive except C:.
    public static IEnumerable<string> GetPlexRoots() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady && !d.Name.TrimEnd('\\').Equals("C:", StringComparison.OrdinalIgnoreCase))
            .Select(d => Path.Combine(d.RootDirectory.FullName, "plex"))
            .Where(Directory.Exists);

    public static IReadOnlyList<PlexFolder> FindMatching(IEnumerable<string> plexRoots, string search, int max = 25)
    {
        var results = new List<PlexFolder>();

        foreach (var root in plexRoots)
        foreach (var categoryDir in SafeDirectories(root))
        foreach (var itemDir in SafeDirectories(categoryDir))
        {
            var name = Path.GetFileName(itemDir);
            if (name.Contains(search, StringComparison.OrdinalIgnoreCase))
                results.Add(new PlexFolder(itemDir, name, Path.GetPathRoot(itemDir)!.TrimEnd('\\'), Path.GetFileName(categoryDir)));
        }

        return results
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    // Last check before a recursive delete: true only for exactly <drive>\plex\<category>\<item>, and
    // never on C:. Guards against a stale or malformed path ever pointing at something broader.
    public static bool IsLibraryItemFolder(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        var category = Path.GetDirectoryName(full);
        var plex = category is null ? null : Path.GetDirectoryName(category);

        return root is not null
            && !root.TrimEnd('\\').Equals("C:", StringComparison.OrdinalIgnoreCase)
            && plex is not null
            && Path.GetFileName(plex).Equals("plex", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(plex), root, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SafeDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch
        {
            return [];
        }
    }
}
