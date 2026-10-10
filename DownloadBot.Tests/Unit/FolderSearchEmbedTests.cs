using DownloadBot.Discord;
using DownloadBot.LocalLibrary;

namespace DownloadBot.Tests.Unit;

public class FolderSearchEmbedTests
{
    [Fact]
    public void BuildFolderSearchEmbed_ListsEachFolderWithDriveAndCategory()
    {
        var folders = new[]
        {
            new PlexFolder(@"G:\plex\Movies\Hacksaw Ridge (2016)", "Hacksaw Ridge (2016)", "G:", "Movies"),
            new PlexFolder(@"E:\plex\Kids Movies\Hacksaw Kids", "Hacksaw Kids", "E:", "Kids Movies")
        };

        var embed = DashboardFormatter.BuildFolderSearchEmbed("hacksaw", folders, truncated: false);

        Assert.Contains("hacksaw", embed.Title);
        Assert.Contains("**Hacksaw Ridge (2016)** — G: • Movies", embed.Description);
        Assert.Contains("**Hacksaw Kids** — E: • Kids Movies", embed.Description);
        Assert.Null(embed.Footer);
    }

    [Fact]
    public void BuildFolderSearchEmbed_NotesWhenResultsWereCut()
    {
        var folders = new[] { new PlexFolder(@"G:\plex\Movies\A", "A", "G:", "Movies") };

        var embed = DashboardFormatter.BuildFolderSearchEmbed("a", folders, truncated: true);

        Assert.Contains("first 1", embed.Footer!.Value.Text);
    }

    [Fact]
    public void BuildFolderSearchEmbed_StaysWithinDiscordLimitsForWorstCaseResults()
    {
        var longName = new string('x', 300);
        var folders = Enumerable.Range(0, 25)
            .Select(i => new PlexFolder($@"G:\plex\Kids TV Shows\{longName}", longName, "G:", "Kids TV Shows"))
            .ToList();

        var embed = DashboardFormatter.BuildFolderSearchEmbed(new string('s', 500), folders, truncated: true);

        Assert.True(embed.Description.Length <= 4096, $"description is {embed.Description.Length}");
        Assert.True(embed.Title.Length <= 256);
        Assert.True(embed.Length <= 6000, $"embed is {embed.Length}");
    }
}
