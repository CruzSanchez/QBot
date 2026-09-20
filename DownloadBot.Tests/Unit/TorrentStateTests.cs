using DownloadBot.QBittorrent;

namespace DownloadBot.Tests.Unit;

public class TorrentStateTests
{
    [Theory]
    [InlineData("downloading", true)]
    [InlineData("metaDL", true)]
    [InlineData("forcedDL", true)]
    [InlineData("allocating", true)]
    [InlineData("checkingDL", true)]
    [InlineData("stalledDL", true)]
    [InlineData("queuedDL", true)]
    [InlineData("pausedDL", false)] // paused — not what "active" means here
    [InlineData("uploading", false)]
    [InlineData("pausedUP", false)]
    [InlineData("error", false)]
    [InlineData("missingFiles", false)]
    public void IsActiveDownload_ClassifiesStatesCorrectly(string state, bool expected)
    {
        var torrent = new TorrentState("hash", "name", state, 0.5);

        Assert.Equal(expected, torrent.IsActiveDownload);
    }
}
