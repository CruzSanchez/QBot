using global::Discord;
using global::Discord.WebSocket;
using DownloadBot.Discord;
using Microsoft.Extensions.Configuration;

namespace DownloadBot.Tests.Integration;

// Full slash-command/button interaction testing isn't realistically automatable — Discord.Net's
// interaction objects (SocketSlashCommand, SocketMessageComponent, etc.) are sealed types the gateway
// hands you, not something you can construct or mock, and there's no official way to simulate a real
// Discord interaction without an actual second Discord client clicking things.
//
// What IS safely testable: whether the configured token is valid and the gateway connection actually
// works. This test does exactly that and nothing else — it logs in, waits for Ready, then disconnects.
// It never wires the bot's own event handlers, so it never registers commands or posts anything to
// any channel (in particular, it does NOT trigger the "Bot connected" status-channel message).
public class DiscordConnectivityIntegrationTests
{
    private static DiscordOptions LoadOptions()
    {
        var options = new DiscordOptions();
        TestConfiguration.Root.GetSection("Discord").Bind(options);
        return options;
    }

    [SkippableFact]
    public async Task Client_CanLoginAndReachReadyState()
    {
        var options = LoadOptions();
        Skip.If(string.IsNullOrWhiteSpace(options.Token), "Discord:Token is not configured — skipping live Discord test.");

        using var client = new DiscordSocketClient();
        var ready = new TaskCompletionSource();
        client.Ready += () => { ready.TrySetResult(); return Task.CompletedTask; };

        try
        {
            await client.LoginAsync(TokenType.Bot, options.Token);
            await client.StartAsync();

            var completed = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.True(completed == ready.Task, "Timed out waiting for the Discord gateway to reach Ready — check the token and network access.");
        }
        finally
        {
            await client.StopAsync();
            await client.LogoutAsync();
        }
    }
}
