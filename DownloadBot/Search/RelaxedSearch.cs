namespace DownloadBot.Search;

// Indexers match a search on EVERY word, so an over-specific query ("hacksaw ridge 2160" - releases say
// "2160p") comes back empty even though the title exists. When a search returns nothing, retry with a
// progressively looser version of the same query instead of giving up.
public static class RelaxedSearch
{
    private const int MaxFallbacks = 2;
    private const int MinTokens = 2; // never relax down to a single word - far too broad

    // Quality/format words that appear in release names in forms users type differently ("2160" vs "2160p").
    private static readonly HashSet<string> QualityTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "480", "480p", "720", "720p", "1080", "1080p", "2160", "2160p", "4k", "uhd", "hd", "hdr", "hdr10", "dv",
        "x264", "x265", "h264", "h265", "hevc", "avc", "bluray", "bdrip", "brrip", "webrip", "web-dl", "webdl",
        "web", "dvdrip", "hdtv", "remux", "cam", "camrip", "hdcam"
    };

    // Looser queries to try, in order, after the original returned nothing:
    // 1) the query without quality/format words, 2) then dropping trailing words (never below two words).
    public static IReadOnlyList<string> Fallbacks(string query)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var fallbacks = new List<string>();

        var withoutQuality = tokens.Where(t => !QualityTokens.Contains(t)).ToList();
        if (withoutQuality.Count > 0 && withoutQuality.Count < tokens.Count)
        {
            fallbacks.Add(string.Join(' ', withoutQuality));
            tokens = withoutQuality;
        }

        while (fallbacks.Count < MaxFallbacks && tokens.Count > MinTokens)
        {
            tokens = tokens.Take(tokens.Count - 1).ToList();
            fallbacks.Add(string.Join(' ', tokens));
        }

        return fallbacks;
    }

    // UsedQuery is the query that actually produced the results (the original unless a fallback was needed).
    public static async Task<(IReadOnlyList<SearchResult> Results, string UsedQuery)> SearchWithFallbackAsync(
        this IJackettClient jackett, string query, CancellationToken cancellationToken = default)
    {
        var results = await jackett.SearchAsync(query, cancellationToken);
        if (results.Count > 0)
            return (results, query);

        foreach (var fallback in Fallbacks(query))
        {
            results = await jackett.SearchAsync(fallback, cancellationToken);
            if (results.Count > 0)
                return (results, fallback);
        }

        return (results, query);
    }
}
