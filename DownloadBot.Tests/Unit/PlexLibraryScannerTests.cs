using DownloadBot.LocalLibrary;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownloadBot.Tests.Unit;

public class PlexLibraryScannerTests
{
    private sealed class FakeLibraryCache(Dictionary<string, IReadOnlyList<(string Path, string Name)>> data) : ILibraryCache
    {
        public IReadOnlyList<(string Path, string Name)> GetEntries(string categoryFolderName) =>
            data.GetValueOrDefault(categoryFolderName, []);
    }

    [Fact]
    public async Task FindExistingAsync_ReturnsMatchFromMappedCategory()
    {
        var cache = new FakeLibraryCache(new()
        {
            ["Movies"] = [("G:\\plex\\Movies\\The Grudge (2004)", "The Grudge (2004)")]
        });
        var scanner = new PlexLibraryScanner(cache, NullLogger<PlexLibraryScanner>.Instance);

        var matches = await scanner.FindExistingAsync("The Grudge 2004", "movie");

        Assert.Contains(matches, m => m == "G:\\plex\\Movies\\The Grudge (2004)");
    }

    [Fact]
    public async Task FindExistingAsync_DoesNotCheckOtherCategoriesForThePickedType()
    {
        var cache = new FakeLibraryCache(new()
        {
            ["Movies"] = [("G:\\plex\\Movies\\Same Name (2004)", "Same Name (2004)")],
            ["Music"] = [("G:\\plex\\Music\\Same Name (2004)", "Same Name (2004)")]
        });
        var scanner = new PlexLibraryScanner(cache, NullLogger<PlexLibraryScanner>.Instance);

        var matches = await scanner.FindExistingAsync("Same Name 2004", "tv");

        Assert.Empty(matches);
    }

    [Fact]
    public async Task FindExistingAsync_UnknownTypeReturnsEmptyWithoutThrowing()
    {
        var scanner = new PlexLibraryScanner(new FakeLibraryCache(new()), NullLogger<PlexLibraryScanner>.Instance);

        var matches = await scanner.FindExistingAsync("Anything", "unknown-type");

        Assert.Empty(matches);
    }

    [Fact]
    public async Task FindExistingAsync_KidsTvChecksBothCandidateFolderNames()
    {
        var cache = new FakeLibraryCache(new()
        {
            ["Kids Tv Shows"] = [("G:\\plex\\Kids Tv Shows\\Bluey", "Bluey")]
        });
        var scanner = new PlexLibraryScanner(cache, NullLogger<PlexLibraryScanner>.Instance);

        var matches = await scanner.FindExistingAsync("Bluey", "kids-tv");

        Assert.Contains(matches, m => m == "G:\\plex\\Kids Tv Shows\\Bluey");
    }
}
