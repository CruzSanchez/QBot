using DownloadBot.QBittorrent;

namespace DownloadBot.Tests.Unit;

public class MagnetHashTests
{
    [Theory]
    [InlineData(
        "magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01&dn=Test",
        "ABCDEF0123456789ABCDEF0123456789ABCDEF01")]
    [InlineData(
        "magnet:?xt=urn:btih:abcdef0123456789abcdef0123456789abcdef01&dn=Test",
        "abcdef0123456789abcdef0123456789abcdef01")]
    public void TryExtract_FindsHashInMagnetLink(string link, string expectedHash)
    {
        Assert.Equal(expectedHash, MagnetHash.TryExtract(link));
    }

    [Theory]
    [InlineData("http://localhost:9117/dl/limetorrents/?jackett_apikey=abc&file=Some+Movie")]
    [InlineData("http://example.com/some.torrent")]
    [InlineData("")]
    public void TryExtract_ReturnsNullForNonMagnetLinks(string link)
    {
        Assert.Null(MagnetHash.TryExtract(link));
    }
}
