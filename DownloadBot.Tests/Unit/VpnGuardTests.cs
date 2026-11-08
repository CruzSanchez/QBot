using DownloadBot.Vpn;

namespace DownloadBot.Tests.Unit;

public class VpnGuardTests
{
    private const string Apps =
        "vpn:C:\\ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\qBittorrent\\qBittorrent.lnk | " +
        "bypass:C:\\ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\Plex\\Plex.lnk";

    [Fact]
    public void IsProtected_ConnectedAndQBittorrentOnVpn_True() =>
        Assert.True(VpnGuard.IsProtected("Connected\r\n", Apps, "qBittorrent"));

    [Theory]
    [InlineData("Disconnected")]
    [InlineData("Interrupted")]
    [InlineData("")]
    public void IsProtected_NotConnected_False(string state) =>
        Assert.False(VpnGuard.IsProtected(state, Apps, "qBittorrent"));

    [Fact]
    public void IsProtected_QBittorrentBypassesVpn_False() =>
        Assert.False(VpnGuard.IsProtected("Connected",
            "bypass:C:\\Programs\\qBittorrent\\qBittorrent.lnk | vpn:C:\\Programs\\Plex\\Plex.lnk", "qBittorrent"));

    [Fact]
    public void IsProtected_NoSplitTunnelEntry_False() =>
        Assert.False(VpnGuard.IsProtected("Connected", "", "qBittorrent"));
}
