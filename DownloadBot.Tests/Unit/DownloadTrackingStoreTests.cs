using DownloadBot.QBittorrent;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownloadBot.Tests.Unit;

public class DownloadTrackingStoreTests : IDisposable
{
    private readonly string _filePath;

    public DownloadTrackingStoreTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), "downloadbot_tracking_test_" + Guid.NewGuid() + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    private DownloadTrackingStore CreateStore() => new(NullLogger<DownloadTrackingStore>.Instance, _filePath);

    [Fact]
    public void Track_PersistsAcrossNewInstances()
    {
        var store1 = CreateStore();
        store1.Track(new TrackedDownload("hash1", "Movie One", 111, 222));

        var store2 = CreateStore();
        var all = store2.GetAll();

        Assert.Single(all);
        Assert.Equal("hash1", all.First().InfoHash);
        Assert.Equal("Movie One", all.First().Title);
        Assert.Equal(111ul, all.First().ChannelId);
        Assert.Equal(222ul, all.First().UserId);
    }

    [Fact]
    public void Untrack_RemovesAndPersistsTheRemoval()
    {
        var store1 = CreateStore();
        store1.Track(new TrackedDownload("hash1", "Movie One", 111, 222));
        store1.Track(new TrackedDownload("hash2", "Movie Two", 111, 222));

        var removed = store1.Untrack("hash1");
        Assert.True(removed);

        var store2 = CreateStore();
        var all = store2.GetAll();

        Assert.Single(all);
        Assert.Equal("hash2", all.First().InfoHash);
    }

    [Fact]
    public void Untrack_ReturnsFalseForUnknownHash()
    {
        var store = CreateStore();

        Assert.False(store.Untrack("does-not-exist"));
    }

    [Fact]
    public void Load_WithNoExistingFile_StartsEmpty()
    {
        var store = CreateStore();

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Load_WithCorruptFile_StartsEmptyInsteadOfThrowing()
    {
        File.WriteAllText(_filePath, "{ this is not valid json [[[");

        var store = CreateStore();

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Track_OverwritesAnExistingEntryWithTheSameHash()
    {
        var store1 = CreateStore();
        store1.Track(new TrackedDownload("hash1", "Original Title", 111, 222));
        store1.Track(new TrackedDownload("hash1", "Updated Title", 111, 222));

        var store2 = CreateStore();
        var all = store2.GetAll();

        Assert.Single(all);
        Assert.Equal("Updated Title", all.First().Title);
    }
}
