using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.Search;

public sealed class JackettOptions
{
    public string BaseUrl { get; set; } = "http://localhost:9117";
    public string ApiKey { get; set; } = "";

    // "all" (default): query every configured indexer separately, in parallel, each with its own deadline
    // (below). Anything else (e.g. "yts" or a Jackett filter) is passed to Jackett as-is in a single request.
    public string Indexers { get; set; } = "all";

    // Per-indexer cap when Indexers is "all". A slow site (1337x behind Cloudflare takes 10-14 s on a new
    // search) is skipped for that search instead of holding the whole picker up; fast ones always make it.
    public int IndexerTimeoutSeconds { get; set; } = 5;
}

public sealed class JackettClient(HttpClient httpClient, IOptions<JackettOptions> options, ILogger<JackettClient>? logger = null) : IJackettClient
{
    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";
    private static readonly TimeSpan IndexerListCacheTtl = TimeSpan.FromMinutes(5);

    private readonly object _cacheLock = new();
    private IReadOnlyList<string> _indexerIds = [];
    private DateTime _indexerIdsFetchedAt = DateTime.MinValue;

    private sealed record IndexerOutcome(IReadOnlyList<SearchResult> Results, bool Failed);

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;

        if (!opts.Indexers.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Finish(await QueryAsync(opts.Indexers, query, cancellationToken));

        var ids = await GetConfiguredIndexersAsync(cancellationToken);
        if (ids.Count == 0)
        {
            // Couldn't list the indexers: fall back to Jackett's own aggregate (slower if one site is slow).
            return Finish(await QueryAsync("all", query, cancellationToken));
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, opts.IndexerTimeoutSeconds));
        var outcomes = await Task.WhenAll(ids.Select(id => QueryWithDeadlineAsync(id, query, timeout, cancellationToken)));

        if (outcomes.All(o => o.Failed))
            throw new HttpRequestException("No indexer answered (every one failed or timed out).");

        return Finish(outcomes.Where(o => !o.Failed).SelectMany(o => o.Results));
    }

    // Best-seeded copy of each distinct release, most-seeded first.
    private static IReadOnlyList<SearchResult> Finish(IEnumerable<SearchResult> results) =>
        SearchResultDeduper.Dedupe(results).OrderByDescending(r => r.Seeders).ToList();

    private async Task<IndexerOutcome> QueryWithDeadlineAsync(string indexerId, string query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            return new IndexerOutcome(await QueryAsync(indexerId, query, deadline.Token), Failed: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Warning, not Error: expected for a slow site, and Error would trigger the error-log upload.
            logger?.LogWarning("Indexer {Indexer} didn't answer within {Seconds}s for \"{Query}\"; skipped for this search",
                indexerId, timeout.TotalSeconds, query);
            return new IndexerOutcome([], Failed: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException)
        {
            logger?.LogWarning(ex, "Indexer {Indexer} failed for \"{Query}\"; skipped for this search", indexerId, query);
            return new IndexerOutcome([], Failed: true);
        }
    }

    private async Task<List<SearchResult>> QueryAsync(string selector, string query, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var url = $"{opts.BaseUrl.TrimEnd('/')}/api/v2.0/indexers/{selector}/results/torznab/" +
                  $"?apikey={Uri.EscapeDataString(opts.ApiKey)}&t=search&q={Uri.EscapeDataString(query)}";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        // Jackett reports some indexer failures as a 200 with an <error> document instead of an HTTP error.
        if (doc.Root?.Name.LocalName == "error")
            throw new HttpRequestException($"Indexer error: {doc.Root.Attribute("description")?.Value}");

        var results = new List<SearchResult>();
        foreach (var item in doc.Descendants("item"))
        {
            var title = item.Element("title")?.Value ?? "Unknown";
            var link = item.Element("link")?.Value
                       ?? item.Element("enclosure")?.Attribute("url")?.Value
                       ?? "";
            if (string.IsNullOrWhiteSpace(link))
                continue;

            var seeders = int.TryParse(GetTorznabAttr(item, "seeders"), out var s) ? s : 0;
            var size = long.TryParse(GetTorznabAttr(item, "size") ?? item.Element("enclosure")?.Attribute("length")?.Value, out var sz) ? sz : 0;

            results.Add(new SearchResult(title, link, seeders, size, GetTorznabAttr(item, "infohash")));
        }

        return results;
    }

    // The configured indexers' ids, cached for a few minutes so a newly added indexer is picked up without
    // a restart but a search doesn't pay an extra request every time. Empty when they can't be listed.
    private async Task<IReadOnlyList<string>> GetConfiguredIndexersAsync(CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_indexerIds.Count > 0 && DateTime.UtcNow - _indexerIdsFetchedAt < IndexerListCacheTtl)
                return _indexerIds;
        }

        try
        {
            var opts = options.Value;
            var url = $"{opts.BaseUrl.TrimEnd('/')}/api/v2.0/indexers/all/results/torznab/api" +
                      $"?apikey={Uri.EscapeDataString(opts.ApiKey)}&t=indexers&configured=true";

            using var response = await httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var doc = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var ids = doc.Descendants("indexer").Select(i => i.Attribute("id")?.Value).OfType<string>().ToList();

            lock (_cacheLock)
            {
                _indexerIds = ids;
                _indexerIdsFetchedAt = DateTime.UtcNow;
            }
            return ids;
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException)
        {
            logger?.LogWarning(ex, "Could not list Jackett's configured indexers; falling back to its combined search");
            return [];
        }
    }

    private static string? GetTorznabAttr(XElement item, string name) =>
        item.Elements(Torznab + "attr")
            .FirstOrDefault(a => a.Attribute("name")?.Value == name)
            ?.Attribute("value")?.Value;
}
