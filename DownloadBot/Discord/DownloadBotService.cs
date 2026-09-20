using System.Collections.Concurrent;
using global::Discord;
using global::Discord.WebSocket;
using DownloadBot.Feed;
using DownloadBot.QBittorrent;
using DownloadBot.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.Discord;

public sealed class DownloadBotService(
    DiscordSocketClient client,
    IJackettClient jackett,
    PendingItemQueue queue,
    DownloadTrackingStore tracking,
    IOptions<DiscordOptions> options,
    ILogger<DownloadBotService> logger) : BackgroundService
{
    // Search results for an in-flight picker, keyed by the picker message's id.
    private readonly ConcurrentDictionary<ulong, PendingPick> _pendingPicks = new();

    private sealed record PendingPick(string Type, IReadOnlyList<SearchResult> Results);

    // Prefixed onto the RSS item title so qBittorrent's Auto Downloading Rules can match by plain string
    // instead of guessing content type from often-inconsistent torrent titles.
    private static readonly Dictionary<string, string> CategoryMarkers = new()
    {
        ["movie"] = "[DLBOT-MOVIE]",
        ["tv"] = "[DLBOT-TV]",
        ["kids-movie"] = "[DLBOT-KIDS-MOVIE]",
        ["kids-tv"] = "[DLBOT-KIDS-TV]"
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.Log += LogAsync;
        client.Ready += OnReadyAsync;
        client.SlashCommandExecuted += OnSlashCommandExecutedAsync;
        client.SelectMenuExecuted += OnSelectMenuExecutedAsync;

        var token = options.Value.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogError("Discord token is not configured. Set Discord:Token in configuration.");
            return;
        }

        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private Task LogAsync(LogMessage message)
    {
        logger.LogInformation("{Message}", message.ToString());
        return Task.CompletedTask;
    }

    private async Task OnReadyAsync()
    {
        var command = new SlashCommandBuilder()
            .WithName("download")
            .WithDescription("Search indexers and queue a download")
            .AddOption("title", ApplicationCommandOptionType.String, "Title to search for", isRequired: true)
            .AddOption("type", ApplicationCommandOptionType.String, "Content type", isRequired: true, choices:
            [
                new ApplicationCommandOptionChoiceProperties { Name = "Movie", Value = "movie" },
                new ApplicationCommandOptionChoiceProperties { Name = "TV", Value = "tv" },
                new ApplicationCommandOptionChoiceProperties { Name = "Kids Movie", Value = "kids-movie" },
                new ApplicationCommandOptionChoiceProperties { Name = "Kids TV", Value = "kids-tv" }
            ])
            .Build();

        try
        {
            if (options.Value.DevGuildId is { } guildId)
                await client.Rest.CreateGuildCommand(command, guildId);
            else
                await client.Rest.CreateGlobalCommand(command);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register slash command");
        }
    }

    private async Task OnSlashCommandExecutedAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != "download")
            return;

        var title = (string)command.Data.Options.First(o => o.Name == "title").Value;
        var type = (string)command.Data.Options.First(o => o.Name == "type").Value;

        await command.DeferAsync();

        IReadOnlyList<SearchResult> results;
        try
        {
            results = await jackett.SearchAsync(title);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Jackett search failed for query {Query}", title);
            await command.FollowupAsync($"Search failed: {ex.Message}");
            return;
        }

        var top = results.Take(5).ToList();
        if (top.Count == 0)
        {
            await command.FollowupAsync($"No results found for **{title}**.");
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId("download-pick")
            .WithPlaceholder("Choose a result to download");

        foreach (var (result, index) in top.Select((r, i) => (r, i)))
        {
            var sizeMb = result.SizeBytes / 1024 / 1024;
            menu.AddOption(
                Truncate(result.Title, 100),
                index.ToString(),
                $"{result.Seeders} seeders · {sizeMb} MB");
        }

        var componentBuilder = new ComponentBuilder().WithSelectMenu(menu);

        var embed = new EmbedBuilder()
            .WithTitle($"Results for \"{title}\" ({type})")
            .WithDescription(string.Join('\n', top.Select((r, i) => $"**{i + 1}.** {r.Title} — {r.Seeders} seeders")))
            .Build();

        var message = await command.FollowupAsync(embed: embed, components: componentBuilder.Build());
        _pendingPicks[message.Id] = new PendingPick(type, top);
    }

    private async Task OnSelectMenuExecutedAsync(SocketMessageComponent component)
    {
        if (component.Data.CustomId != "download-pick")
            return;

        if (!_pendingPicks.TryRemove(component.Message.Id, out var pick))
        {
            await component.UpdateAsync(m => m.Content = "This selection has expired.");
            return;
        }

        var index = int.Parse(component.Data.Values.First());
        var picked = pick.Results[index];
        var marker = CategoryMarkers.GetValueOrDefault(pick.Type, "");
        var taggedTitle = string.IsNullOrEmpty(marker) ? picked.Title : $"{marker} {picked.Title}";

        queue.Add(new PendingItem
        {
            Id = Guid.NewGuid().ToString(),
            Title = taggedTitle,
            Link = picked.MagnetOrTorrentLink
        });

        var infoHash = MagnetHash.TryExtract(picked.MagnetOrTorrentLink);
        if (infoHash is not null)
        {
            tracking.Track(new TrackedDownload(infoHash, picked.Title, component.Channel.Id, component.User.Id));
        }
        else
        {
            logger.LogInformation("No magnet hash found for {Title}; completion notification will not be tracked", picked.Title);
        }

        await component.UpdateAsync(m =>
        {
            m.Content = $"Queued **{picked.Title}** — it will appear in the RSS feed for qBittorrent to pick up.";
            m.Embed = null;
            m.Components = new ComponentBuilder().Build();
        });
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
