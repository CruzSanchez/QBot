using DownloadBot.LocalLibrary;

namespace DownloadBot.Tests.Unit;

public class PlexFolderFinderTests : IDisposable
{
    private readonly string _plex;

    public PlexFolderFinderTests()
    {
        _plex = Path.Combine(Path.GetTempPath(), "downloadbot_plexfinder_" + Guid.NewGuid(), "plex");
        Directory.CreateDirectory(Path.Combine(_plex, "Movies", "Hacksaw Ridge (2016)"));
        Directory.CreateDirectory(Path.Combine(_plex, "Kids Movies", "Hacksaw Kids"));
        Directory.CreateDirectory(Path.Combine(_plex, "TV Shows", "Daredevil"));
        File.WriteAllText(Path.Combine(_plex, "Movies", "hacksaw notes.txt"), "x");
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_plex)!, recursive: true);

    [Fact]
    public void FindMatching_SearchesEveryCategoryCaseInsensitively()
    {
        var result = PlexFolderFinder.FindMatching([_plex], "HACKSAW");

        Assert.Equal(["Hacksaw Kids", "Hacksaw Ridge (2016)"], result.Select(f => f.Name));
        Assert.Equal(["Kids Movies", "Movies"], result.Select(f => f.Category));
    }

    [Theory]
    [InlineData("My name Is earl", "My.Name.Is.Earl")]
    [InlineData("mars attacks 1996", "Mars_Attacks!-(1996)")]
    [InlineData("hills have eyes", "The.Hills.Have.Eyes.2006.1080p.BluRay.x265")]
    public void FindMatching_IgnoresCaseAndPunctuationInFolderNames(string search, string folderName)
    {
        Directory.CreateDirectory(Path.Combine(_plex, "TV Shows", folderName));

        var result = PlexFolderFinder.FindMatching([_plex], search);

        Assert.Contains(folderName, result.Select(f => f.Name));
    }

    [Fact]
    public void FindMatching_PunctuationOnlySearchMatchesNothing()
    {
        Assert.Empty(PlexFolderFinder.FindMatching([_plex], "..."));
    }

    [Fact]
    public void FindMatching_NeverReturnsCategoryFoldersOrFiles()
    {
        Assert.Empty(PlexFolderFinder.FindMatching([_plex], "Movies"));
        Assert.Empty(PlexFolderFinder.FindMatching([_plex], "notes"));
    }

    [Fact]
    public void FindMatching_SpansMultipleRootsAndRespectsMax()
    {
        var secondRoot = Path.Combine(Path.GetDirectoryName(_plex)!, "plex2");
        Directory.CreateDirectory(Path.Combine(secondRoot, "Movies", "Hacksaw Again"));

        Assert.Equal(3, PlexFolderFinder.FindMatching([_plex, secondRoot], "hacksaw").Count);
        Assert.Single(PlexFolderFinder.FindMatching([_plex, secondRoot], "hacksaw", max: 1));
    }

    [Theory]
    [InlineData(@"G:\plex\Movies\Hacksaw Ridge (2016)", true)]
    [InlineData(@"E:\plex\TV Shows\Daredevil", true)]
    [InlineData(@"G:\plex\Movies", false)]
    [InlineData(@"G:\plex", false)]
    [InlineData(@"G:\plex\Movies\Hacksaw\Extras", false)]
    [InlineData(@"G:\other\Movies\Hacksaw", false)]
    [InlineData(@"C:\plex\Movies\Hacksaw", false)]
    [InlineData(@"G:\plex\Movies\..\..", false)]
    public void IsLibraryItemFolder_OnlyAcceptsDriveRootPlexCategoryItem(string path, bool expected)
    {
        Assert.Equal(expected, PlexFolderFinder.IsLibraryItemFolder(path));
    }
}
