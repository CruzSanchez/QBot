using DownloadBot.QBittorrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DownloadBot.Tests.Unit;

public class ActiveDriveStoreTests : IDisposable
{
    private readonly string _filePath;

    public ActiveDriveStoreTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), "downloadbot_activedrive_test_" + Guid.NewGuid() + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    private static QBittorrentOptions Options() => new()
    {
        DefaultDrive = "G",
        SavePaths = new()
        {
            ["G"] = new() { ["movie"] = "G:\\plex\\Movies" },
            ["D"] = new() { ["movie"] = "D:\\plex\\Movies" }
        }
    };

    private ActiveDriveStore CreateStore(QBittorrentOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options()), NullLogger<ActiveDriveStore>.Instance, _filePath);

    [Fact]
    public void CurrentDrive_DefaultsToConfiguredDefaultDrive()
    {
        var store = CreateStore();

        Assert.Equal("G", store.CurrentDrive);
    }

    [Fact]
    public void TrySetDrive_SucceedsForConfiguredDrive()
    {
        var store = CreateStore();

        Assert.True(store.TrySetDrive("D"));
        Assert.Equal("D", store.CurrentDrive);
    }

    [Fact]
    public void TrySetDrive_IsCaseInsensitive()
    {
        var store = CreateStore();

        Assert.True(store.TrySetDrive("d"));
        Assert.Equal("D", store.CurrentDrive);
    }

    [Fact]
    public void TrySetDrive_FailsForUnconfiguredDriveAndLeavesCurrentUnchanged()
    {
        var store = CreateStore();

        Assert.False(store.TrySetDrive("Z"));
        Assert.Equal("G", store.CurrentDrive);
    }

    [Fact]
    public void TrySetDrive_PersistsAcrossNewInstances()
    {
        var store1 = CreateStore();
        store1.TrySetDrive("D");

        var store2 = CreateStore();

        Assert.Equal("D", store2.CurrentDrive);
    }

    [Fact]
    public void Load_WithCorruptFile_FallsBackToDefaultInsteadOfThrowing()
    {
        File.WriteAllText(_filePath, "{ not valid json [[[");

        var store = CreateStore();

        Assert.Equal("G", store.CurrentDrive);
    }

    [Fact]
    public void Load_IgnoresPersistedDriveNoLongerInConfig()
    {
        var store1 = CreateStore();
        store1.TrySetDrive("D");

        // Reconfigure without "D" — the persisted choice should no longer apply.
        var store2 = CreateStore(new QBittorrentOptions
        {
            DefaultDrive = "G",
            SavePaths = new() { ["G"] = new() { ["movie"] = "G:\\plex\\Movies" } }
        });

        Assert.Equal("G", store2.CurrentDrive);
    }

    [Fact]
    public void TryGetSavePath_ResolvesFromCurrentDrive()
    {
        var store = CreateStore();
        store.TrySetDrive("D");

        Assert.True(store.TryGetSavePath("movie", out var path));
        Assert.Equal("D:\\plex\\Movies", path);
    }

    [Fact]
    public void TryGetSavePath_ReturnsFalseForUnknownType()
    {
        var store = CreateStore();

        Assert.False(store.TryGetSavePath("music", out _));
    }
}
