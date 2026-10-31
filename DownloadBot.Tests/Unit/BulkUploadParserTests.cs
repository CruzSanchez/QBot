using DownloadBot.Discord;

namespace DownloadBot.Tests.Unit;

public class BulkUploadParserTests
{
    [Fact]
    public void Parse_ReadsTheLibraryListingFormat()
    {
        const string text =
            "\"Library\",\"Path\",\"SizeGB\",\"Modified\"\r\n" +
            "\"Movies\",\"Movies\\27 Dresses (2008) [1080p]\\27.Dresses.2008.1080p.BRrip.x264.YIFY.mp4\",\"1.5\",\"2026-10-21\"\r\n" +
            "\"Kids Movies\",\"Kids Movies\\www.UIndex.org    -    The Cat in the Hat 2003 BluRay 1080p DD 5 1 x264-BHDStudio\\x.mp4\",\"4.91\",\"2026-10-21\"\r\n";

        var result = BulkUploadParser.Parse(text);

        Assert.Equal(
            [new BulkUploadItem("27 Dresses 2008", "movie"), new BulkUploadItem("The Cat in the Hat 2003", "kids-movie")],
            result.Items);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Parse_CollapsesEpisodesOfOneShowIntoOneTitle()
    {
        const string text =
            "Tv Shows,Tv Shows\\Family Guy - Seasons 1 to 20\\Season 08\\S08E05.mkv\n" +
            "Tv Shows,Tv Shows\\Family Guy - Seasons 1 to 20\\Season 08\\S08E06.mkv\n";

        var result = BulkUploadParser.Parse(text);

        Assert.Equal([new BulkUploadItem("Family Guy", "tv")], result.Items);
    }

    [Fact]
    public void Parse_SkipsUnknownLibrariesAndReportsThem()
    {
        var result = BulkUploadParser.Parse("Youtube,Youtube\\Montage\\a.mp4\nMovies,Movies\\Dune (2021)\\d.mkv");

        Assert.Equal([new BulkUploadItem("Dune 2021", "movie")], result.Items);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void Parse_KeepsQuotedCommasInsideAPath()
    {
        var result = BulkUploadParser.Parse("\"Movies\",\"Movies\\Lock, Stock and Two Smoking Barrels (1998)\\x.mp4\",\"1\"");

        Assert.Equal([new BulkUploadItem("Lock, Stock and Two Smoking Barrels 1998", "movie")], result.Items);
    }
}
