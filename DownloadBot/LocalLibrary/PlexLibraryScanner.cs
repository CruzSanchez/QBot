using Microsoft.Extensions.Logging;

namespace DownloadBot.LocalLibrary;

public interface IPlexLibraryScanner
{
    Task<IReadOnlyList<string>> FindExistingAsync(string rawQuery, string type);
}

// Matches against LibraryCacheService's snapshot instead of touching the filesystem itself — the
// snapshot already holds every category's top-level folder/file names, refreshed every 12 hours, so
// a /download request only ever does in-memory filtering here, not disk I/O.
public sealed class PlexLibraryScanner(ILibraryCache cache, ILogger<PlexLibraryScanner> logger) : IPlexLibraryScanner
{
    // Only the folder(s) for the /download type actually picked get checked — checking every category
    // on every request (the old behavior) added unnecessary work regardless of type.
    // kids-tv lists two candidates because the actual folder naming was never confirmed (see
    // CLARIFICATIONS.md) — cheap enough to check both since it's just an in-memory lookup now.
    private static readonly Dictionary<string, string[]> CategoryFoldersByType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["movie"] = ["Movies"],
        ["tv"] = ["TV Shows"],
        ["kids-movie"] = ["Kids Movies"],
        ["kids-tv"] = ["Kids Shows", "Kids Tv Shows"],
        ["music"] = ["Music"]
    };

    public Task<IReadOnlyList<string>> FindExistingAsync(string rawQuery, string type)
    {
        var (title, year) = TitleYear.Parse(rawQuery);

        var categories = CategoryFoldersByType.GetValueOrDefault(type, []);
        if (categories.Length == 0)
        {
            logger.LogWarning("No library folder(s) mapped for type \"{Type}\"; skipping duplicate check", type);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var matches = new List<string>();
        foreach (var category in categories)
        {
            foreach (var entry in cache.GetEntries(category))
            {
                if (LibraryFolderScanner.NameMatches(entry.Name, title, year))
                    matches.Add(entry.Path);
            }
        }

        logger.LogInformation(
            "Library check for \"{RawQuery}\" (parsed as \"{Title}\" {Year}, type {Type}): {MatchCount} match(es) from cache",
            rawQuery, title, year, type, matches.Count);

        return Task.FromResult<IReadOnlyList<string>>(matches);
    }
}
