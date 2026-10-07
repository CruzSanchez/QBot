using DownloadBot.Discord;

namespace DownloadBot.Tests.Unit;

public class HelpEmbedTests
{
    [Fact]
    public void BuildHelpEmbed_StaysWithinDiscordLimits()
    {
        var embed = DownloadBotService.BuildHelpEmbed(); // Build() itself throws past the limits

        Assert.True(embed.Length <= 6000, $"embed is {embed.Length} chars");
        Assert.All(embed.Fields, f =>
        {
            Assert.True(f.Name.Length <= 256, f.Name);
            Assert.True(f.Value.Length <= 1024, $"{f.Name}: {f.Value.Length}");
        });
    }
}
