using DownloadBot.Search;

namespace DownloadBot.Tests.Unit;

public class SeasonFilterTests
{
    [Theory]
    [InlineData("Vikings S01 complete", "Vikings S01 COMPLETE 720p", true)]
    [InlineData("Vikings S01 complete", "Vikings.S05.COMPLETE.1080p", false)]
    [InlineData("Vikings S01", "Vikings S05E01 1080p", false)]
    [InlineData("Vikings S01", "Vikings S01E03 HDTV", true)]
    [InlineData("Vikings Season 1", "Vikings.S01.1080p", true)]
    [InlineData("Vikings s1", "Vikings Season 5 Complete", false)]
    [InlineData("Vikings S01", "Vikings S01-S06 Complete", true)]
    [InlineData("Vikings S03", "Vikings Seasons 1 to 6 1080p", true)]
    [InlineData("Vikings S02", "Vikings Seasons 1, 2 & 3", true)]
    [InlineData("Vikings S01", "Vikings S02-S04", false)]
    [InlineData("Vikings S01", "Vikings Complete Series 1080p", true)]
    [InlineData("Vikings S05", "Vikings Season 1 - 1080p x265", false)]
    [InlineData("Vikings S10", "Vikings S05 10bit", false)]
    [InlineData("the lorax 2012", "Vikings S05", true)] // no season asked for: nothing filtered
    public void Apply_KeepsOnlyReleasesThatCanContainTheAskedSeason(string query, string title, bool kept)
    {
        var results = SeasonFilter.Apply(query, [new SearchResult(title, "magnet:?x", 1, 1)]);

        Assert.Equal(kept, results.Count == 1);
    }
}
