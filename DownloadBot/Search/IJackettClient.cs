namespace DownloadBot.Search;

public interface IJackettClient
{
    // imdbId (e.g. "tt0133093"), when given, searches by IMDb ID instead of a fuzzy title match —
    // avoids mixing up sequels/remakes/similarly-titled releases. query is still sent alongside it as
    // a fallback for indexers that don't support IMDb lookups natively.
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, string? imdbId = null, CancellationToken cancellationToken = default);
}
