using DownloadBot.YtDlp;

namespace DownloadBot.Tests.Unit;

public class YtDlpProgressParserTests
{
    [Theory]
    [InlineData("[download]  42.3% of   10.00MiB at    1.21MiB/s ETA 00:07", 42.3)]
    [InlineData("[download] 100.0% of 10.00MiB in 00:08", 100.0)]
    [InlineData("[download]   0.0% of 10.00MiB at  Unknown B/s ETA Unknown", 0.0)]
    [InlineData("   [download]  55.5% of ~ 20.00MiB at  500.00KiB/s ETA 00:20", 55.5)] // leading whitespace
    [InlineData("[download] \u001b[0;94m 42.3\u001b[0m% of \u001b[0;32m 10.00MiB\u001b[0m at 1.21MiB/s ETA 00:07", 42.3)] // ANSI-colorized
    public void TryParsePercent_ParsesProgressLines(string line, double expected)
    {
        Assert.Equal(expected, YtDlpProgressParser.TryParsePercent(line));
    }

    [Theory]
    [InlineData("[Merger] Merging formats into \"video.mp4\"")]
    [InlineData("[youtube] abc123: Downloading webpage")]
    [InlineData("")]
    [InlineData("WARNING: some warning text")]
    [InlineData("/plex/Youtube/Some Video.mp4")]
    public void TryParsePercent_ReturnsNullForNonProgressLines(string line)
    {
        Assert.Null(YtDlpProgressParser.TryParsePercent(line));
    }

    [Fact]
    public void TryParsePlaylistPosition_ParsesVideoOfTotal()
    {
        var result = YtDlpProgressParser.TryParsePlaylistPosition("[download] Downloading video 3 of 12");

        Assert.Equal((3, 12), result);
    }

    [Fact]
    public void TryParsePlaylistPosition_ParsesItemOfTotal()
    {
        var result = YtDlpProgressParser.TryParsePlaylistPosition("[download] Downloading item 1 of 5");

        Assert.Equal((1, 5), result);
    }

    [Theory]
    [InlineData("[download]  42.3% of   10.00MiB at    1.21MiB/s ETA 00:07")]
    [InlineData("[youtube] abc123: Downloading webpage")]
    [InlineData("")]
    public void TryParsePlaylistPosition_ReturnsNullForNonPlaylistLines(string line)
    {
        Assert.Null(YtDlpProgressParser.TryParsePlaylistPosition(line));
    }
}
