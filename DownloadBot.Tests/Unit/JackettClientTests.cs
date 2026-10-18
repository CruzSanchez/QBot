using System.Diagnostics;
using System.Net;
using DownloadBot.Search;
using Microsoft.Extensions.Options;

namespace DownloadBot.Tests.Unit;

public class JackettClientTests
{
    private static string Rss(params (string Title, int Seeds, string Hash)[] items) =>
        "<?xml version=\"1.0\"?><rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel>" +
        string.Concat(items.Select(i =>
            $"<item><title>{i.Title}</title><link>http://dl/{i.Hash}</link>" +
            $"<torznab:attr name=\"seeders\" value=\"{i.Seeds}\"/><torznab:attr name=\"size\" value=\"1000\"/>" +
            $"<torznab:attr name=\"infohash\" value=\"{i.Hash}\"/></item>")) +
        "</channel></rss>";

    private static string IndexerList(params string[] ids) =>
        "<?xml version=\"1.0\"?><indexers>" + string.Concat(ids.Select(id => $"<indexer id=\"{id}\" configured=\"true\"><title>{id}</title></indexer>")) + "</indexers>";

    // Routes by URL: "t=indexers" is the discovery call, otherwise the indexer id in the path picks the responder.
    private sealed class FakeHandler(Func<string, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Urls) Urls.Add(url);
            return await respond(url, cancellationToken);
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static JackettClient Client(FakeHandler handler, string indexers = "all", int timeoutSeconds = 6) =>
        new(new HttpClient(handler), Options.Create(new JackettOptions
        {
            BaseUrl = "http://jackett", ApiKey = "k", Indexers = indexers, IndexerTimeoutSeconds = timeoutSeconds
        }));

    private static string IndexerOf(string url)
    {
        var after = url.Split("/indexers/")[1];
        return after[..after.IndexOf('/')];
    }

    [Fact]
    public async Task Search_QueriesEachConfiguredIndexerAndMergesDedupedBySeeders()
    {
        var handler = new FakeHandler((url, _) => Task.FromResult(url.Contains("t=indexers")
            ? Ok(IndexerList("a", "b"))
            : IndexerOf(url) == "a"
                ? Ok(Rss(("Movie 2016", 10, "H1")))
                : Ok(Rss(("Movie 2016", 20, "H1"), ("Other Movie", 5, "H2")))));

        var results = await Client(handler).SearchAsync("movie");

        Assert.Equal(2, results.Count);
        Assert.Equal(20, results[0].Seeders); // the same release (H1) kept once, with the better-seeded copy
        Assert.Equal("Other Movie", results[1].Title);
    }

    [Fact]
    public async Task Search_SkipsAnIndexerThatMissesItsDeadlineButKeepsTheFastOnes()
    {
        var handler = new FakeHandler(async (url, ct) =>
        {
            if (url.Contains("t=indexers")) return Ok(IndexerList("fast", "slow"));
            if (IndexerOf(url) == "slow") { await Task.Delay(Timeout.Infinite, ct); }
            return Ok(Rss(("Fast Result", 7, "F1")));
        });

        var sw = Stopwatch.StartNew();
        var results = await Client(handler, timeoutSeconds: 1).SearchAsync("x");

        Assert.Equal("Fast Result", Assert.Single(results).Title);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Search_ToleratesOneIndexerErroringButThrowsWhenAllFail()
    {
        var oneBad = new FakeHandler((url, _) => Task.FromResult(url.Contains("t=indexers")
            ? Ok(IndexerList("good", "bad"))
            : IndexerOf(url) == "bad" ? new HttpResponseMessage(HttpStatusCode.BadRequest) : Ok(Rss(("Ok Result", 3, "G1")))));
        Assert.Equal("Ok Result", Assert.Single(await Client(oneBad).SearchAsync("x")).Title);

        var allBad = new FakeHandler((url, _) => Task.FromResult(url.Contains("t=indexers")
            ? Ok(IndexerList("a", "b"))
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(allBad).SearchAsync("x"));
    }

    [Fact]
    public async Task Search_TreatsATorznabErrorDocumentAsAFailedIndexer()
    {
        var handler = new FakeHandler((url, _) => Task.FromResult(url.Contains("t=indexers")
            ? Ok(IndexerList("a", "b"))
            : IndexerOf(url) == "a"
                ? Ok("<?xml version=\"1.0\"?><error code=\"900\" description=\"blocked by Cloudflare\"/>")
                : Ok(Rss(("Fine", 1, "B1")))));

        Assert.Equal("Fine", Assert.Single(await Client(handler).SearchAsync("x")).Title);
    }

    [Fact]
    public async Task Search_FallsBackToJacketsCombinedSearchWhenTheIndexerListFails()
    {
        var handler = new FakeHandler((url, _) => Task.FromResult(url.Contains("t=indexers")
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Ok(Rss(("Combined Result", 9, "C1")))));

        var results = await Client(handler).SearchAsync("x");

        Assert.Equal("Combined Result", Assert.Single(results).Title);
        Assert.Contains(handler.Urls, u => u.Contains("/indexers/all/results/torznab/") && u.Contains("t=search"));
    }

    [Fact]
    public async Task Search_WithAnExplicitIndexerSelectorDoesNotDiscoverOrSplit()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(Rss(("Only", 4, "O1")))));

        await Client(handler, indexers: "yts").SearchAsync("x");

        var url = Assert.Single(handler.Urls);
        Assert.Contains("/indexers/yts/results/torznab/", url);
    }

    [Fact]
    public async Task Search_CachesTheIndexerListBetweenSearches()
    {
        var handler = new FakeHandler((url, _) => Task.FromResult(url.Contains("t=indexers") ? Ok(IndexerList("a")) : Ok(Rss(("R", 1, "R1")))));
        var client = Client(handler);

        await client.SearchAsync("one");
        await client.SearchAsync("two");

        Assert.Single(handler.Urls, u => u.Contains("t=indexers"));
    }
}
