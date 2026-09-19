using System.Xml.Linq;

namespace DownloadBot.Search;

public sealed class JackettOptions
{
    public string BaseUrl { get; set; } = "http://localhost:9117";
    public string ApiKey { get; set; } = "";
    public string Indexers { get; set; } = "all";
}

public sealed class JackettClient(HttpClient httpClient, Microsoft.Extensions.Options.IOptions<JackettOptions> options) : IJackettClient
{
    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var url = $"{opts.BaseUrl.TrimEnd('/')}/api/v2.0/indexers/{opts.Indexers}/results/torznab/" +
                   $"?apikey={Uri.EscapeDataString(opts.ApiKey)}&t=search&q={Uri.EscapeDataString(query)}";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = XDocument.Parse(xml);

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

            results.Add(new SearchResult(title, link, seeders, size));
        }

        return results.OrderByDescending(r => r.Seeders).ToList();
    }

    private static string? GetTorznabAttr(XElement item, string name) =>
        item.Elements(Torznab + "attr")
            .FirstOrDefault(a => a.Attribute("name")?.Value == name)
            ?.Attribute("value")?.Value;
}
