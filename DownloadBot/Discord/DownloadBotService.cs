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

    private static readonly ApplicationCommandOptionChoiceProperties[] TypeChoices =
    [
        new ApplicationCommandOptionChoiceProperties { Name = "Movie", Value = "movie" },
        new ApplicationCommandOptionChoiceProperties { Name = "TV", Value = "tv" },
        new ApplicationCommandOptionChoiceProperties { Name = "Kids Movie", Value = "kids-movie" },
        new ApplicationCommandOptionChoiceProperties { Name = "Kids TV", Value = "kids-tv" }
    ];

    private async Task OnReadyAsync()
    {
        var downloadCommand = new SlashCommandBuilder()
            .WithName("download")
            .WithDescription("Search indexers and queue a download")
            .AddOption("title", ApplicationCommandOptionType.String, "Title to search for", isRequired: true)
            .AddOption("type", ApplicationCommandOptionType.String, "Content type", isRequired: true, choices: TypeChoices)
            .Build();

        var downloadManyCommand = new SlashCommandBuilder()
            .WithName("download-many")
            .WithDescription("Queue several titles at once (auto-picks the top-seeded result for each)")
            .AddOption("titles", ApplicationCommandOptionType.String, "Titles separated by commas", isRequired: true)
            .AddOption("type", ApplicationCommandOptionType.String, "Content type applied to all titles", isRequired: true, choices: TypeChoices)
            .Build();

        try
        {
            if (options.Value.DevGuildId is { } guildId)
            {
                await client.Rest.CreateGuildCommand(downloadCommand, guildId);
                await client.Rest.CreateGuildCommand(downloadManyCommand, guildId);
            }
            else
            {
                await client.Rest.CreateGlobalCommand(downloadCommand);
                await client.Rest.CreateGlobalCommand(downloadManyCommand);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register slash command");
        }
    }

    private async Task OnSlashCommandExecutedAsync(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case "download":
                await HandleDownloadAsync(command);
                break;
            case "download-many":
                await HandleDownloadManyAsync(command);
                break;
        }
    }

    private async Task HandleDownloadAsync(SocketSlashCommand command)
    {
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
        QueuePicked(picked, pick.Type, component.Channel.Id, component.User.Id);

        await component.UpdateAsync(m =>
        {
            m.Content = $"Queued **{picked.Title}** — it will appear in the RSS feed for qBittorrent to pick up.";
            m.Embed = null;
            m.Components = new ComponentBuilder().Build();
        });
    }

    private async Task HandleDownloadManyAsync(SocketSlashCommand command)
    {
        var titlesRaw = (string)command.Data.Options.First(o => o.Name == "titles").Value;
        var type = (string)command.Data.Options.First(o => o.Name == "type").Value;

        var titles = titlesRaw
            .Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20) // guard against pasting an enormous list into one command
            .ToList();

        if (titles.Count == 0)
        {
            await command.RespondAsync("No titles found — separate them with commas or newlines.");
            return;
        }

        await command.DeferAsync();

        var queued = new List<string>();
        var notFound = new List<string>();
        var failed = new List<string>();

        foreach (var title in titles)
        {
            IReadOnlyList<SearchResult> results;
            try
            {
                results = await jackett.SearchAsync(title);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Jackett search failed for query {Query}", title);
                failed.Add(title);
                continue;
            }

            // No interactive picker here — with many titles in one command, auto-take the top-seeded result.
            var best = results.FirstOrDefault();
            if (best is null)
            {
                notFound.Add(title);
                continue;
            }

            QueuePicked(best, type, command.Channel.Id, command.User.Id);
            queued.Add($"{title} → {best.Title}");
        }

        var summary = new EmbedBuilder().WithTitle($"Queued {queued.Count}/{titles.Count} titles ({type})");
        if (queued.Count > 0)
            summary.AddField("Queued", string.Join('\n', queued.Select(q => $"✅ {q}")).Truncate(1024));
        if (notFound.Count > 0)
            summary.AddField("No results", string.Join('\n', notFound.Select(t => $"❌ {t}")).Truncate(1024));
        if (failed.Count > 0)
            summary.AddField("Search failed", string.Join('\n', failed.Select(t => $"⚠️ {t}")).Truncate(1024));

        await command.FollowupAsync(embed: summary.Build());
    }

    private void QueuePicked(SearchResult picked, string type, ulong channelId, ulong userId)
    {
        var marker = CategoryMarkers.GetValueOrDefault(type, "");
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
            tracking.Track(new TrackedDownload(infoHash, picked.Title, channelId, userId));
        }
        else
        {
            logger.LogInformation("No magnet hash found for {Title}; completion notification will not be tracked", picked.Title);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

file static class StringExtensions
{
    public static string Truncate(this string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}
