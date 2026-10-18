namespace DownloadBot.Search;

// With several indexers, the same release shows up on most of them, and since the picker only offers the top
// handful by seeders those copies would crowd out genuinely different options (a check of the top 15 for one
// movie found 9 distinct releases, one of them three times). Keeps the best-seeded copy of each release.
public static class SearchResultDeduper
{
    public static IReadOnlyList<SearchResult> Dedupe(IEnumerable<SearchResult> results) =>
        results
            .GroupBy(Key)
            .Select(g => g.OrderByDescending(r => r.Seeders).First())
            .ToList();

    // Same torrent hash = same release. Without one, fall back to the same name (letters and digits only,
    // so "Movie.2016.1080p" and "Movie 2016 1080p" match) AND the same size.
    private static string Key(SearchResult r) =>
        !string.IsNullOrWhiteSpace(r.InfoHash)
            ? "hash:" + r.InfoHash.Trim().ToLowerInvariant()
            : "name:" + new string(r.Title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()) + "|" + r.SizeBytes;
}
