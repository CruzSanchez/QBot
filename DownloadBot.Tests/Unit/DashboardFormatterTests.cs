using DownloadBot.Discord;
using DownloadBot.QBittorrent;

namespace DownloadBot.Tests.Unit;

public class DashboardFormatterTests
{
    [Fact]
    public void BuildActiveDownloadsEmbed_WithNoTorrents_ShowsEmptyState()
    {
        var embed = DashboardFormatter.BuildActiveDownloadsEmbed([], DateTimeOffset.UtcNow);

        Assert.Equal("📥 Active downloads (0)", embed.Title);
        Assert.Equal("Nothing downloading right now.", embed.Description);
    }

    [Fact]
    public void BuildActiveDownloadsEmbed_OrdersByProgressDescending()
    {
        var torrents = new[]
        {
            new TorrentState("hash1", "Slow One", "downloading", 0.1),
            new TorrentState("hash2", "Nearly Done", "downloading", 0.9)
        };

        var embed = DashboardFormatter.BuildActiveDownloadsEmbed(torrents, DateTimeOffset.UtcNow);

        Assert.Equal("📥 Active downloads (2)", embed.Title);
        Assert.True(embed.Description!.IndexOf("Nearly Done", StringComparison.Ordinal) <
                    embed.Description!.IndexOf("Slow One", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildActiveDownloadsEmbed_IsBlurple()
    {
        var embed = DashboardFormatter.BuildActiveDownloadsEmbed([], DateTimeOffset.UtcNow);
        Assert.Equal(DashboardFormatter.BlurpleColor, embed.Color);
    }
}
