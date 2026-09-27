namespace DownloadBot.Search;

// Builds the Torznab query URL for a search. Pure and I/O-free, directly unit-testable — separate
// from JackettClient so the URL-construction logic (especially the IMDb-ID branch, easy to get subtly
// wrong) can be verified without a live Jackett instance.
public static class JackettQueryBuilder
{
    // imdbId (e.g. "tt0133093"), when given, switches to Torznab's "movie" search type and includes
    // it alongside the title — most indexers use the ID as the primary match and fall back to the
    // title text if they don't support IMDb lookups natively, which is why both are still sent.
    public static string BuildSearchUrl(string baseUrl, string apiKey, string indexers, string query, string? imdbId = null)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/v2.0/indexers/{indexers}/results/torznab/" +
                  $"?apikey={Uri.EscapeDataString(apiKey)}";

        url += string.IsNullOrWhiteSpace(imdbId)
            ? $"&t=search&q={Uri.EscapeDataString(query)}"
            : $"&t=movie&imdbid={Uri.EscapeDataString(imdbId)}&q={Uri.EscapeDataString(query)}";

        return url;
    }
}
