namespace DownloadBot.LocalLibrary;

public sealed record PlexFolder(string Path, string Name, string Drive, string Category);

public sealed record PlexCategory(string Path, string Drive, string Category);

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
        var wanted = Normalize(search);

        foreach (var root in plexRoots)
        foreach (var categoryDir in SafeDirectories(root))
        foreach (var itemDir in SafeDirectories(categoryDir))
        {
            var name = Path.GetFileName(itemDir);
            if (wanted.Length > 0 && Normalize(name).Contains(wanted, StringComparison.Ordinal))
                results.Add(new PlexFolder(itemDir, name, Path.GetPathRoot(itemDir)!.TrimEnd('\\'), Path.GetFileName(categoryDir)));
        }

        return results
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    // Every <drive>\plex\<category> folder — the places /move can put a library folder.
    public static IReadOnlyList<PlexCategory> GetCategories(IEnumerable<string> plexRoots) =>
        plexRoots
            .SelectMany(root => SafeDirectories(root))
            .Select(dir => new PlexCategory(dir, Path.GetPathRoot(dir)!.TrimEnd('\\'), Path.GetFileName(dir)))
            .OrderBy(c => c.Drive, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Where source could be moved: any category other than the one it's already in, and only where no
    // folder of that name exists yet (a move never merges into or overwrites an existing folder).
    public static IReadOnlyList<PlexCategory> ValidDestinations(IEnumerable<PlexCategory> categories, PlexFolder source) =>
        categories
            .Where(c => !string.Equals(c.Path, Path.GetDirectoryName(source.Path), StringComparison.OrdinalIgnoreCase))
            .Where(c => !Directory.Exists(Path.Combine(c.Path, source.Name)))
            .ToList();

    // True only for exactly <drive>\plex\<category>, never on C:.
    public static bool IsCategoryFolder(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        var plex = Path.GetDirectoryName(full);

        return root is not null
            && !root.TrimEnd('\\').Equals("C:", StringComparison.OrdinalIgnoreCase)
            && plex is not null
            && Path.GetFileName(plex).Equals("plex", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(plex), root, StringComparison.OrdinalIgnoreCase);
    }

    // Release-style folder names ("My.Name.Is.Earl.S01", "Mars_Attacks-1996") rarely match what a person
    // types, so both sides are lowercased with every run of non-letter/digit characters collapsed to one
    // space before comparing — "my name is earl" then matches "my name is earl s01" on word boundaries.
    public static string Normalize(string value) =>
        string.Join(' ', value.ToLowerInvariant().Split(
            value.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries));

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
