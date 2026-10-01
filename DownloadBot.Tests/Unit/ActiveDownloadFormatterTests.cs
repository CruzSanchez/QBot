using DownloadBot.QBittorrent;

namespace DownloadBot.Tests.Unit;

public class ActiveDownloadFormatterTests
{
    [Theory]
    [InlineData(0, "stalled")]
    [InlineData(-1, "stalled")]
    [InlineData(1024, "0.00 MB/s")] // rounds down to 0.00 at this precision, still distinct from "stalled"
    [InlineData(5 * 1024 * 1024, "5.00 MB/s")]
    [InlineData((long)(2.5 * 1024 * 1024), "2.50 MB/s")]
    public void FormatSpeed_FormatsBytesPerSecAsMBs(long bytesPerSec, string expected)
    {
        Assert.Equal(expected, ActiveDownloadFormatter.FormatSpeed(bytesPerSec));
    }

    [Theory]
    [InlineData(0, "unknown ETA")]
    [InlineData(-1, "unknown ETA")]
    [InlineData(8_640_000, "unknown ETA")] // qBittorrent's sentinel value
    [InlineData(100_000_000, "unknown ETA")]
    [InlineData(90, "1m 30s left")]
    [InlineData(3600, "1h 0m left")]
    [InlineData(5400, "1h 30m left")]
    public void FormatEta_FormatsSecondsOrReportsUnknown(long etaSeconds, string expected)
    {
        Assert.Equal(expected, ActiveDownloadFormatter.FormatEta(etaSeconds));
    }

    [Theory]
    [InlineData("metaDL", "downloading metadata")]
    [InlineData("forcedMetaDL", "downloading metadata")]
    [InlineData("checkingDL", "checking files")]
    [InlineData("queuedDL", "queued")]
    [InlineData("allocating", "allocating space")]
    [InlineData("pausedDL", "paused")]
    [InlineData("stalledDL", "no peers")]
    [InlineData("downloading", "downloading")]
    [InlineData("someUnknownState", "someUnknownState")] // unrecognized states pass through as-is
    public void FormatStateLabel_MapsQBittorrentStatesToPlainLanguage(string state, string expected)
    {
        Assert.Equal(expected, ActiveDownloadFormatter.FormatStateLabel(state));
    }

    [Theory]
    [InlineData(5 * 1024 * 1024, "stalledDL", "5.00 MB/s")] // actively transferring: state is irrelevant
    [InlineData(0, "metaDL", "stalled — downloading metadata")]
    [InlineData(0, "stalledDL", "stalled — no peers")]
    [InlineData(0, "downloading", "stalled")] // redundant with "stalled" itself, so suppressed
    public void FormatSpeedWithState_AppendsStateReasonOnlyWhenStalled(long bytesPerSec, string state, string expected)
    {
        Assert.Equal(expected, ActiveDownloadFormatter.FormatSpeedWithState(bytesPerSec, state));
    }

    [Theory]
    [InlineData(0.0, "[░░░░░░░░░░░░░░░░░░░░]")]
    [InlineData(1.0, "[████████████████████]")]
    [InlineData(0.5, "[██████████░░░░░░░░░░]")]
    [InlineData(-1.0, "[░░░░░░░░░░░░░░░░░░░░]")] // clamped
    [InlineData(1.5, "[████████████████████]")] // clamped
    public void FormatProgressBar_FillsProportionallyAndClamps(double progress, string expected)
    {
        Assert.Equal(expected, ActiveDownloadFormatter.FormatProgressBar(progress));
    }

    [Fact]
    public void FormatProgressBar_HonorsCustomWidth()
    {
        Assert.Equal("[█████░░░░░]", ActiveDownloadFormatter.FormatProgressBar(0.5, width: 10));
    }
}
