namespace DownloadBot.Search;

public interface IJackettClient
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
}
