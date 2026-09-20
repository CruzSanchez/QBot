using DownloadBot.LocalLibrary;

namespace DownloadBot.Tests.Unit;

public class TitleYearTests
{
    [Theory]
    [InlineData("Jason X 1990", "Jason X", 1990)]
    [InlineData("Jason X (1990)", "Jason X", 1990)]
    [InlineData("Jason.X.(1990)", "Jason X", 1990)]
    [InlineData("Jason.X. (1990)", "Jason X", 1990)]
    [InlineData("Jason.X.1990", "Jason X", 1990)]
    [InlineData("Jason_X_1990", "Jason X", 1990)]
    [InlineData("Jason.X", "Jason X", null)]
    [InlineData("Jason vs Freddy 2003", "Jason vs Freddy", 2003)]
    [InlineData("Jason.vs.Freddy.2003", "Jason vs Freddy", 2003)]
    [InlineData("Jason Goes to Hell 1993", "Jason Goes to Hell", 1993)]
    [InlineData("Friday the 13th: The Final Chapter (1984)", "Friday the 13th: The Final Chapter", 1984)]
    [InlineData("Friday.the.13th.The.Final.Chapter.1984", "Friday the 13th The Final Chapter", 1984)]
    [InlineData("Jason.X (1990) [1080p]", "Jason X", 1990)]
    [InlineData("Jason X (1990) [1080p]", "Jason X", 1990)]
    [InlineData("Jason.X.1990.1080p.BluRay.x264", "Jason X", 1990)]
    [InlineData("Jason X (1990) 1080p BluRay x264-GROUP", "Jason X", 1990)]
    [InlineData("2001: A Space Odyssey (1968) [1080p]", "2001: A Space Odyssey", 1968)]
    [InlineData("2012 (2009)", "2012", 2009)]
    public void Parse_ExtractsTitleAndYear(string raw, string expectedTitle, int? expectedYear)
    {
        var (title, year) = TitleYear.Parse(raw);

        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    [Theory]
    [InlineData("Jason X 1990", "Jason.X (1990) [1080p]", true)]
    [InlineData("Jason X 1990", "Jason.X.1990.1080p.BluRay.x264", true)]
    [InlineData("Jason X 1990", "Jason.X.(1990)", true)]
    [InlineData("Jason X 1990", "Jason.vs.Freddy.(2003)", false)]
    [InlineData("Jason X 1990", "Jason.Goes.to.Hell.(1993) [1080p]", false)]
    [InlineData("Jason X 1990", "Jason X Reloaded (1990)", false)]
    public void Parse_TitleAndYearMatchAcrossFormats(string query, string candidate, bool expectedMatch)
    {
        var (qTitle, qYear) = TitleYear.Parse(query);
        var (cTitle, cYear) = TitleYear.Parse(candidate);

        var titleMatches = string.Equals(qTitle, cTitle, StringComparison.OrdinalIgnoreCase);
        var yearMatches = qYear is null || cYear is null || qYear == cYear;

        Assert.Equal(expectedMatch, titleMatches && yearMatches);
    }
}
