using DownloadBot.YtDlp;

namespace DownloadBot.Tests.Unit;

public class YoutubeFolderResolverTests : IDisposable
{
    private readonly string _root;

    public YoutubeFolderResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "downloadbot_youtube_test_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(_root, "Rocket League Montage"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ResolveExisting_FindsARealDirectChild()
    {
        var result = YoutubeFolderResolver.ResolveExisting(_root, "Rocket League Montage");

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(_root, "Rocket League Montage"), result);
    }

    [Fact]
    public void ResolveExisting_ReturnsNullForNonexistentFolder()
    {
        Assert.Null(YoutubeFolderResolver.ResolveExisting(_root, "Does Not Exist"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../../Windows")]
    [InlineData("..\\..\\Windows")]
    [InlineData("Rocket League Montage/../../Windows")]
    public void ResolveExisting_RejectsPathTraversalAttempts(string requested)
    {
        Assert.Null(YoutubeFolderResolver.ResolveExisting(_root, requested));
    }

    [Fact]
    public void ResolveExisting_RejectsAbsolutePathEscapingRoot()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "downloadbot_elsewhere_" + Guid.NewGuid());
        Directory.CreateDirectory(elsewhere);
        try
        {
            Assert.Null(YoutubeFolderResolver.ResolveExisting(_root, elsewhere));
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }
}
