using DownloadBot.Discord;
using DownloadBot.Search;

namespace DownloadBot.Tests.Unit;

public class PickerEmbedTests
{
    [Fact]
    public void BuildPickerEmbed_NumbersResultsWithSeeders()
    {
        var results = new[]
        {
            new SearchResult("The Movie 2020 1080p", "magnet:?a", 120, 1_000_000),
            new SearchResult("The Movie 2020 720p", "magnet:?b", 15, 500_000)
        };

        var embed = DashboardFormatter.BuildPickerEmbed("the movie", "movie", results);

        Assert.Contains("the movie", embed.Title);
        Assert.Contains("**1.** The Movie 2020 1080p — 120 seeders", embed.Description);
        Assert.Contains("**2.** The Movie 2020 720p — 15 seeders", embed.Description);
    }

    // Regression: ten very long release names pushed the description past Discord's 4096 limit, Build()
    // threw, and /download was left stuck on "thinking…".
    [Fact]
    public void BuildPickerEmbed_StaysWithinDiscordLimitsForVeryLongNames()
    {
        var results = Enumerable.Range(0, 10)
            .Select(i => new SearchResult(new string('x', 2000), "magnet:?x", i, 1))
            .ToList();

        var embed = DashboardFormatter.BuildPickerEmbed(new string('t', 400), "kids-movie", results);

        Assert.True(embed.Description.Length <= 4096, $"description is {embed.Description.Length}");
        Assert.True(embed.Title.Length <= 256, $"title is {embed.Title.Length}");
        Assert.True(embed.Length <= 6000, $"embed is {embed.Length}");
    }
}
