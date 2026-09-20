using DownloadBot.LocalLibrary;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownloadBot.Tests.Unit;

public class LibraryFolderScannerTests : IDisposable
{
    private readonly string _root;
    private readonly string _movies;
    private readonly string _tvShows;

    public LibraryFolderScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "downloadbot_test_" + Guid.NewGuid());
        _movies = Path.Combine(_root, "Movies");
        _tvShows = Path.Combine(_root, "TV Shows");

        Directory.CreateDirectory(Path.Combine(_movies, "The Grudge (2004) [1080p]"));
        Directory.CreateDirectory(Path.Combine(_movies, "The Grudge 2 (2006)"));
        File.WriteAllText(Path.Combine(_movies, "The.Ring.2002.1080p.BluRay.mkv"), "x"); // flat file, no per-title folder

        Directory.CreateDirectory(Path.Combine(_tvShows, "Stranger Things (2016)", "Season 01"));
        File.WriteAllText(Path.Combine(_tvShows, "Stranger Things (2016)", "Season 01", "S01E01.mkv"), "x");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private List<string> FindMatches(string categoryPath, string query)
    {
        var (title, year) = TitleYear.Parse(query);
        return [.. LibraryFolderScanner.FindMatches(categoryPath, title, year, NullLogger.Instance)];
    }

    [Fact]
    public void FindMatches_FindsFolderPerMovie()
    {
        var matches = FindMatches(_movies, "The Grudge 2004");

        Assert.Contains(matches, m => Path.GetFileName(m) == "The Grudge (2004) [1080p]");
    }

    [Fact]
    public void FindMatches_FindsBareFileWithNoPerTitleFolder()
    {
        var matches = FindMatches(_movies, "The Ring 2002");

        Assert.Contains(matches, m => Path.GetFileName(m) == "The.Ring.2002.1080p.BluRay.mkv");
    }

    [Fact]
    public void FindMatches_DoesNotConfuseSiblingFranchiseEntry()
    {
        var matches = FindMatches(_movies, "The Grudge 2004");

        Assert.DoesNotContain(matches, m => Path.GetFileName(m) == "The Grudge 2 (2006)");
    }

    [Fact]
    public void FindMatches_FindsNestedTvShowFolder()
    {
        var matches = FindMatches(_tvShows, "Stranger Things 2016");

        Assert.Contains(matches, m => Path.GetFileName(m) == "Stranger Things (2016)");
    }

    [Fact]
    public void FindMatches_ReturnsEmptyForUnrelatedQuery()
    {
        var matches = FindMatches(_movies, "Totally Different Movie 1999");

        Assert.Empty(matches);
    }

    [Fact]
    public void FindMatches_SkipsInaccessibleSubfolderInsteadOfAbortingWholeScan()
    {
        var lockedDir = Path.Combine(_movies, "Some Other Movie (2010) [1080p]");
        Directory.CreateDirectory(lockedDir);

        var acl = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        RunIcacls($"\"{lockedDir}\" /deny \"{acl}\":(OI)(CI)RX");
        try
        {
            var matches = FindMatches(_movies, "The Grudge 2004");

            Assert.Contains(matches, m => Path.GetFileName(m) == "The Grudge (2004) [1080p]");
        }
        finally
        {
            RunIcacls($"\"{lockedDir}\" /remove:d \"{acl}\"");
        }
    }

    private static void RunIcacls(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("icacls", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit();
    }
}
