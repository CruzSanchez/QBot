using DownloadBot.YtDlp;

namespace DownloadBot.Tests.Unit;

public class YtDlpFormatSelectorTests
{
    [Fact]
    public void Build_WithNullQuality_CapsAtDefaultMaxHeight()
    {
        var format = YtDlpFormatSelector.Build(null, defaultMaxHeight: 1080);

        Assert.Equal("bestvideo*[height<=?1080]+bestaudio/best[height<=?1080]", format);
    }

    [Theory]
    [InlineData("best")]
    [InlineData("Best")]
    [InlineData("BEST")]
    public void Build_WithBest_IsUncapped(string quality)
    {
        var format = YtDlpFormatSelector.Build(quality, defaultMaxHeight: 1080);

        Assert.Equal("bestvideo*+bestaudio/best", format);
    }

    [Theory]
    [InlineData("720", 720)]
    [InlineData("1080", 1080)]
    [InlineData("2160", 2160)]
    public void Build_WithNumericQuality_CapsAtThatHeight(string quality, int expectedHeight)
    {
        var format = YtDlpFormatSelector.Build(quality, defaultMaxHeight: 1080);

        Assert.Equal($"bestvideo*[height<=?{expectedHeight}]+bestaudio/best[height<=?{expectedHeight}]", format);
    }

    [Fact]
    public void Build_WithUnparseableQuality_FallsBackToDefaultMaxHeight()
    {
        var format = YtDlpFormatSelector.Build("not-a-number", defaultMaxHeight: 1080);

        Assert.Equal("bestvideo*[height<=?1080]+bestaudio/best[height<=?1080]", format);
    }
}
