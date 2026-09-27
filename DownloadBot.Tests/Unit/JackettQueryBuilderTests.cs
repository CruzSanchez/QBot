using DownloadBot.Search;

namespace DownloadBot.Tests.Unit;

public class JackettQueryBuilderTests
{
    [Fact]
    public void BuildSearchUrl_WithoutImdbId_UsesPlainSearchType()
    {
        var url = JackettQueryBuilder.BuildSearchUrl("http://localhost:9117", "key123", "all", "The Matrix 1999");

        Assert.Contains("t=search", url);
        Assert.Contains("q=The%20Matrix%201999", url);
        Assert.DoesNotContain("imdbid", url);
    }

    [Fact]
    public void BuildSearchUrl_WithImdbId_UsesMovieSearchTypeWithImdbIdAndTitle()
    {
        var url = JackettQueryBuilder.BuildSearchUrl("http://localhost:9117", "key123", "all", "The Matrix", "tt0133093");

        Assert.Contains("t=movie", url);
        Assert.Contains("imdbid=tt0133093", url);
        Assert.Contains("q=The%20Matrix", url);
    }

    [Fact]
    public void BuildSearchUrl_IncludesApiKeyAndIndexers()
    {
        var url = JackettQueryBuilder.BuildSearchUrl("http://localhost:9117", "key123", "myindexer", "query");

        Assert.Contains("apikey=key123", url);
        Assert.Contains("/indexers/myindexer/", url);
    }

    [Fact]
    public void BuildSearchUrl_TrimsTrailingSlashFromBaseUrl()
    {
        var url = JackettQueryBuilder.BuildSearchUrl("http://localhost:9117/", "key123", "all", "query");

        Assert.DoesNotContain("9117//", url);
    }

    [Fact]
    public void BuildSearchUrl_EscapesSpecialCharactersInQuery()
    {
        var url = JackettQueryBuilder.BuildSearchUrl("http://localhost:9117", "key123", "all", "Q&A: Part 2");

        Assert.DoesNotContain("Q&A: Part 2", url);
    }
}
