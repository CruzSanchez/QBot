using DownloadBot.Search;

namespace DownloadBot.Tests.Unit;

public class RelaxedSearchTests
{
    [Theory]
    [InlineData("hacksaw ridge 2160", new[] { "hacksaw ridge" })]
    [InlineData("hacksaw ridge 2160p HEVC", new[] { "hacksaw ridge" })]
    [InlineData("saban an espn original", new[] { "saban an espn", "saban an" })]
    [InlineData("the lorax 2012 1080p", new[] { "the lorax 2012", "the lorax" })]
    [InlineData("tires S01", new string[0])]        // already two words: nothing safe to drop
    [InlineData("dune", new string[0])]             // one word: never relaxed
    [InlineData("1080p", new string[0])]            // only a quality word: don't strip to empty
    [InlineData("", new string[0])]
    public void Fallbacks_StripQualityWordsThenDropTrailingWords(string query, string[] expected)
    {
        Assert.Equal(expected, RelaxedSearch.Fallbacks(query));
    }

    [Fact]
    public void Fallbacks_NeverReturnsMoreThanTwo()
    {
        Assert.True(RelaxedSearch.Fallbacks("a b c d e f g 1080p").Count <= 2);
    }

    private sealed class FakeJackett(Func<string, int> resultCount) : IJackettClient
    {
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            IReadOnlyList<SearchResult> results = Enumerable.Range(0, resultCount(query))
                .Select(i => new SearchResult($"{query} #{i}", "magnet:?x", 1, 1)).ToList();
            return Task.FromResult(results);
        }
    }

    [Fact]
    public async Task SearchWithFallbackAsync_DoesNotRetryWhenTheOriginalFindsSomething()
    {
        var jackett = new FakeJackett(_ => 5);

        var (results, used) = await jackett.SearchWithFallbackAsync("hacksaw ridge 2160");

        Assert.Equal(5, results.Count);
        Assert.Equal("hacksaw ridge 2160", used);
        Assert.Equal(["hacksaw ridge 2160"], jackett.Queries);
    }

    [Fact]
    public async Task SearchWithFallbackAsync_RetriesLooserQueriesUntilOneFindsResults()
    {
        var jackett = new FakeJackett(q => q == "saban an" ? 3 : 0);

        var (results, used) = await jackett.SearchWithFallbackAsync("saban an espn original");

        Assert.Equal(3, results.Count);
        Assert.Equal("saban an", used);
        Assert.Equal(["saban an espn original", "saban an espn", "saban an"], jackett.Queries);
    }

    [Fact]
    public async Task SearchWithFallbackAsync_ReportsTheOriginalQueryWhenNothingMatches()
    {
        var jackett = new FakeJackett(_ => 0);

        var (results, used) = await jackett.SearchWithFallbackAsync("aaa bbb ccc ddd");

        Assert.Empty(results);
        Assert.Equal("aaa bbb ccc ddd", used);
        Assert.Equal(3, jackett.Queries.Count); // original + 2 fallbacks, then it stops
    }
}
