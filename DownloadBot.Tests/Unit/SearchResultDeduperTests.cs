using DownloadBot.Search;

namespace DownloadBot.Tests.Unit;

public class SearchResultDeduperTests
{
    [Fact]
    public void Dedupe_CollapsesTheSameInfoHashToTheBestSeededCopy()
    {
        var results = new[]
        {
            new SearchResult("Movie.2016.2160p.REMUX", "http://a", 45, 50_000, "ABC123"),
            new SearchResult("Movie.2016.2160p.REMUX", "http://b", 47, 50_000, "abc123"), // same hash, different case
            new SearchResult("Movie 2016 2160p REMUX", "http://c", 45, 50_000, "ABC123"),
            new SearchResult("Movie.2016.720p", "http://d", 30, 20_000, "FFF999")
        };

        var deduped = SearchResultDeduper.Dedupe(results);

        Assert.Equal(2, deduped.Count);
        Assert.Equal(47, deduped.Single(r => r.Title.Contains("2160p")).Seeders);
    }

    [Fact]
    public void Dedupe_WithoutAHashMatchesOnPunctuationInsensitiveNameAndSize()
    {
        var results = new[]
        {
            new SearchResult("Movie.2016.1080p", "http://a", 10, 4_000),
            new SearchResult("Movie 2016 1080p", "http://b", 90, 4_000),   // same release, different punctuation
            new SearchResult("Movie.2016.1080p", "http://c", 20, 8_000)    // same name but a different size: a different release
        };

        var deduped = SearchResultDeduper.Dedupe(results);

        Assert.Equal(2, deduped.Count);
        Assert.Contains(deduped, r => r.Seeders == 90);
        Assert.Contains(deduped, r => r.Seeders == 20);
    }

    [Fact]
    public void Dedupe_KeepsDifferentReleasesApartEvenWithSimilarNames()
    {
        var results = new[]
        {
            new SearchResult("Movie 1080p", "http://a", 5, 1_000, "AAA"),
            new SearchResult("Movie 1080p", "http://b", 5, 1_000, "BBB") // different hash = genuinely different torrent
        };

        Assert.Equal(2, SearchResultDeduper.Dedupe(results).Count);
    }

    [Fact]
    public void Dedupe_HandlesEmptyInput()
    {
        Assert.Empty(SearchResultDeduper.Dedupe([]));
    }
}
