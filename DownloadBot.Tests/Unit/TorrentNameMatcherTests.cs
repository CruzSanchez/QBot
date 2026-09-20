using DownloadBot.QBittorrent;

namespace DownloadBot.Tests.Unit;

public class TorrentNameMatcherTests
{
    [Fact]
    public void StripMarker_RemovesKnownCategoryMarkers()
    {
        Assert.Equal(
            "Deuce Bigalow Male Gigolo (1999) (1080p) [WEBRip] [5 1] [YTS MX]",
            TorrentNameMatcher.StripMarker("[DLBOT-MOVIE] Deuce Bigalow Male Gigolo (1999) (1080p) [WEBRip] [5 1] [YTS MX]"));

        Assert.Equal(
            "Jujutsu Kaisen S01",
            TorrentNameMatcher.StripMarker("[DLBOT-TV] Jujutsu Kaisen S01"));
    }

    [Fact]
    public void StripMarker_LeavesUnmarkedTitleUnchanged()
    {
        Assert.Equal("No Marker Here", TorrentNameMatcher.StripMarker("No Marker Here"));
    }

    [Theory]
    [InlineData(
        "[DLBOT-MOVIE] Deuce Bigalow Male Gigolo (1999) (1080p) [WEBRip] [5 1] [YTS MX]",
        "Deuce Bigalow Male Gigolo (1999) (1080p) [WEBRip] [5 1] [YTS MX]",
        true)]
    [InlineData(
        "[DLBOT-MOVIE] Deuce Bigalow European Gigolo (2005) (1080p) [WEBRip] [5 1] [YTS MX]",
        "Deuce.Bigalow.European.Gigolo.2005.1080p.WEBRip.5.1.YTS.MX",
        true)]
    [InlineData(
        "[DLBOT-MOVIE] Deuce Bigalow Male Gigolo (1999) (1080p) [WEBRip] [5 1] [YTS MX]",
        "Deuce Bigalow European Gigolo (2005) (1080p) [WEBRip] [5 1] [YTS MX]",
        false)]
    [InlineData(
        "[DLBOT-MOVIE] The Grudge (2004) 1080p BrRip x264 -YIFY",
        "Jujutsu Kaisen S01 1080p Dual Audio WEBRip AAC x265 EMBER",
        false)]
    public void LooselyMatch_ToleratesFormattingButNotDifferentTitles(string ourTitle, string qbitName, bool expectedMatch)
    {
        Assert.Equal(expectedMatch, TorrentNameMatcher.LooselyMatch(qbitName, ourTitle));
    }
}
