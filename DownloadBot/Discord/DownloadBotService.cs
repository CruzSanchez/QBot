using System.Collections.Concurrent;
using global::Discord;
using global::Discord.WebSocket;
using DownloadBot.Feed;
using DownloadBot.LocalLibrary;
using DownloadBot.QBittorrent;
using DownloadBot.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.Discord;

public sealed class DownloadBotService(
    DiscordSocketClient client,
    IJackettClient jackett,
    PendingItemQueue queue,
    DownloadTrackingStore tracking,
    IPlexLibraryScanner libraryScanner,
    IHttpClientFactory httpClientFactory,
    IOptions<DiscordOptions> options,
    ILogger<DownloadBotService> logger) : BackgroundService
{
    // Search results for an in-flight picker, keyed by the picker message's id.
    private readonly ConcurrentDictionary<ulong, PendingPick> _pendingPicks = new();

    // "Already in the library, search anyway?" confirmations, keyed by that message's id.
    private readonly ConcurrentDictionary<ulong, PendingDuplicateConfirmation> _pendingDuplicateConfirmations = new();

    private sealed record PendingPick(string Type, IReadOnlyList<SearchResult> Results);

    private sealed record PendingDuplicateConfirmation(SocketSlashCommand Command, string Title, string Type);

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
        client.Disconnected += OnDisconnectedAsync;
        client.SlashCommandExecuted += OnSlashCommandExecutedAsync;
        client.SelectMenuExecuted += OnSelectMenuExecutedAsync;
        client.ButtonExecuted += OnButtonExecutedAsync;

        var token = options.Value.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogError("Discord token is not configured. Set Discord:Token in configuration.");
            return;
        }

        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();

        _ = HeartbeatLoopAsync(stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private Task OnDisconnectedAsync(Exception ex)
    {
        logger.LogWarning(ex, "Discord gateway disconnected");
        return PostStatusAsync($"🔴 Bot disconnected: {ex.Message}");
    }

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PostStatusAsync($"Bot Status: LIVE - {FormatCentral(DateTimeOffset.UtcNow)}");
        }
    }

    private static readonly TimeZoneInfo CentralTimeZone = ResolveCentralTimeZone();

    private static TimeZoneInfo ResolveCentralTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"); }
    }

    private static string FormatCentral(DateTimeOffset utc) =>
        $"{TimeZoneInfo.ConvertTime(utc, CentralTimeZone):yyyy-MM-dd HH:mm:ss} CST";

    private async Task PostStatusAsync(string message)
    {
        var channelId = options.Value.StatusChannelId;
        if (channelId is null)
            return;

        try
        {
            if (client.GetChannel(channelId.Value) is not IMessageChannel channel)
            {
                logger.LogWarning("Could not resolve status channel {ChannelId}", channelId);
                return;
            }

            await channel.SendMessageAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to post status message to channel {ChannelId}", channelId);
        }
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
            .WithDescription("Search several titles at once, with a picker posted for each")
            .AddOption("titles", ApplicationCommandOptionType.String, "Titles separated by commas", isRequired: true)
            .AddOption("type", ApplicationCommandOptionType.String, "Content type applied to all titles", isRequired: true, choices: TypeChoices)
            .Build();

        var helpCommand = new SlashCommandBuilder()
            .WithName("download-help")
            .WithDescription("Show how to use the download commands")
            .Build();

        try
        {
            if (options.Value.DevGuildId is { } guildId)
            {
                await client.Rest.CreateGuildCommand(downloadCommand, guildId);
                await client.Rest.CreateGuildCommand(downloadManyCommand, guildId);
                await client.Rest.CreateGuildCommand(helpCommand, guildId);
            }
            else
            {
                await client.Rest.CreateGlobalCommand(downloadCommand);
                await client.Rest.CreateGlobalCommand(downloadManyCommand);
                await client.Rest.CreateGlobalCommand(helpCommand);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register slash command");
        }

        await PostStatusAsync($"🟢 Bot connected - {FormatCentral(DateTimeOffset.UtcNow)}");
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
            case "download-help":
                await HandleHelpAsync(command);
                break;
        }
    }

    private static Task HandleHelpAsync(SocketSlashCommand command)
    {
        var embed = new EmbedBuilder()
            .WithTitle("Download bot — how to use it")
            .WithDescription("Search torrent indexers from Discord and queue a download for qBittorrent to pick up automatically.")
            .AddField("/download title type",
                "Search for one title. Pick your `type` (Movie, TV, Kids Movie, Kids TV), " +
                "then choose the exact release from the dropdown of top results.\n" +
                "Example: `/download title:Dune Part Two type:movie`")
            .AddField("/download-many titles type",
                "Search for several titles at once, separated by commas (or newlines). " +
                "You get a separate picker for each title, so you still choose the exact release for every one.\n" +
                "Example: `/download-many titles:Bluey, Paw Patrol type:kids-tv`\n" +
                "Limit: 20 titles per command.")
            .AddField("Already-in-library check",
                "Before searching, the bot checks the Plex library folders for a matching title/year. " +
                "If found, it asks you to confirm before searching anyway instead of blocking you outright.")
            .AddField("What happens after you pick",
                "The chosen release is added to the download queue and shows up in qBittorrent automatically " +
                "within a few minutes. You'll get pinged in this server once it finishes downloading.")
            .WithFooter("Ask whoever runs the bot if a search comes back empty — it may need more indexers configured.")
            .Build();

        return command.RespondAsync(embed: embed, ephemeral: true);
    }

    private async Task HandleDownloadAsync(SocketSlashCommand command)
    {
        var title = (string)command.Data.Options.First(o => o.Name == "title").Value;
        var type = (string)command.Data.Options.First(o => o.Name == "type").Value;

        logger.LogInformation("/download invoked by {User}: title={Title} type={Type}", command.User.Username, title, type);

        await command.DeferAsync();

        if (await PostDuplicateConfirmationIfFoundAsync(command, title, type))
            return;

        await SearchAndPostPickerAsync(command, title, type);
    }

    private async Task SearchAndPostPickerAsync(SocketSlashCommand command, string title, string type)
    {
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

        logger.LogInformation("Jackett returned {Count} result(s) for {Query}", results.Count, title);

        if (results.Count == 0)
        {
            await command.FollowupAsync($"No results found for **{title}**.");
            return;
        }

        await PostPickerAsync(command, title, type, results);
    }

    // Checks the local Plex library for an existing match before spending a Jackett search on it.
    // Returns true (and posts a "search anyway?" confirmation instead) if something was found — this
    // is a soft warning, not a hard stop, since a duplicate title/year could still be a different cut,
    // a damaged/incomplete copy, etc.
    private async Task<bool> PostDuplicateConfirmationIfFoundAsync(SocketSlashCommand command, string title, string type)
    {
        IReadOnlyList<string> matches;
        try
        {
            matches = await libraryScanner.FindExistingAsync(title);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Library duplicate check failed for \"{Title}\"; proceeding with search anyway", title);
            return false;
        }

        if (matches.Count == 0)
            return false;

        logger.LogInformation("Found {Count} existing match(es) for \"{Title}\" already in the library", matches.Count, title);

        var embed = new EmbedBuilder()
            .WithTitle($"Already in the library: \"{title}\"")
            .WithDescription("A search resolved these item(s) already in the server:\n" +
                string.Join('\n', matches.Select(m => $"📁 `{m}`")).Truncate(3800))
            .WithFooter("Do you still want to search and download it anyway?")
            .Build();

        var buttons = new ComponentBuilder()
            .WithButton("Search anyway", "dup-confirm-yes", ButtonStyle.Primary)
            .WithButton("Cancel", "dup-confirm-no", ButtonStyle.Secondary);

        var message = await command.FollowupAsync(embed: embed, components: buttons.Build());
        _pendingDuplicateConfirmations[message.Id] = new PendingDuplicateConfirmation(command, title, type);
        return true;
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (component.Data.CustomId is not ("dup-confirm-yes" or "dup-confirm-no"))
            return;

        try
        {
            if (!_pendingDuplicateConfirmations.TryRemove(component.Message.Id, out var pending))
            {
                await component.UpdateAsync(m => m.Content = "This confirmation has expired.");
                return;
            }

            if (component.Data.CustomId == "dup-confirm-no")
            {
                await component.UpdateAsync(m =>
                {
                    m.Content = $"Skipped searching for **{pending.Title}** — already in the library.";
                    m.Embed = null;
                    m.Components = new ComponentBuilder().Build();
                });
                return;
            }

            await component.UpdateAsync(m =>
            {
                m.Content = $"Searching anyway for **{pending.Title}**...";
                m.Embed = null;
                m.Components = new ComponentBuilder().Build();
            });

            await SearchAndPostPickerAsync(pending.Command, pending.Title, pending.Type);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle duplicate confirmation on message {MessageId}", component.Message.Id);
        }
    }

    private async Task PostPickerAsync(SocketSlashCommand command, string title, string type, IReadOnlyList<SearchResult> results)
    {
        var top = results.Take(5).ToList();

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
        logger.LogInformation("Posted picker message {MessageId} for \"{Title}\" with {Count} option(s)", message.Id, title, top.Count);
    }

    private async Task OnSelectMenuExecutedAsync(SocketMessageComponent component)
    {
        if (component.Data.CustomId != "download-pick")
            return;

        try
        {
            // Acknowledge within Discord's 3-second window immediately — resolving the info hash below
            // can take several seconds (downloading a .torrent file, following redirects), which would
            // otherwise make the later response miss the window and fail with "Unknown interaction".
            await component.DeferAsync();

            if (!_pendingPicks.TryRemove(component.Message.Id, out var pick))
            {
                logger.LogWarning("Selection on message {MessageId} had no matching pending pick (expired or already consumed)", component.Message.Id);
                await component.ModifyOriginalResponseAsync(m => m.Content = "This selection has expired.");
                return;
            }

            var index = int.Parse(component.Data.Values.First());
            var picked = pick.Results[index];
            logger.LogInformation("User {User} picked option {Index}: \"{Title}\"", component.User.Username, index, picked.Title);

            await QueuePickedAsync(picked, pick.Type, component.Channel.Id, component.User.Id);

            await component.ModifyOriginalResponseAsync(m =>
            {
                m.Content = $"Queued **{picked.Title}** — it will appear in the RSS feed for qBittorrent to pick up.";
                m.Embed = null;
                m.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle selection on message {MessageId}", component.Message.Id);
            try
            {
                await component.ModifyOriginalResponseAsync(m => m.Content = $"Something went wrong queuing that: {ex.Message}");
            }
            catch (Exception updateEx)
            {
                logger.LogError(updateEx, "Also failed to report the error back to Discord");
            }
        }
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

        var notFound = new List<string>();
        var failed = new List<string>();
        var needsConfirmation = new List<string>();
        var pickersPosted = 0;

        foreach (var title in titles)
        {
            if (await PostDuplicateConfirmationIfFoundAsync(command, title, type))
            {
                needsConfirmation.Add(title);
                continue;
            }

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

            if (results.Count == 0)
            {
                notFound.Add(title);
                continue;
            }

            // One picker message per title, posted independently, so each can be picked at its own pace/order.
            await PostPickerAsync(command, title, type, results);
            pickersPosted++;
        }

        if (notFound.Count > 0 || failed.Count > 0 || needsConfirmation.Count > 0)
        {
            var summary = new EmbedBuilder().WithTitle($"Posted {pickersPosted} picker(s) — some titles need attention");
            if (needsConfirmation.Count > 0)
                summary.AddField("Already in library — confirm above", string.Join('\n', needsConfirmation.Select(t => $"📁 {t}")).Truncate(1024));
            if (notFound.Count > 0)
                summary.AddField("No results", string.Join('\n', notFound.Select(t => $"❌ {t}")).Truncate(1024));
            if (failed.Count > 0)
                summary.AddField("Search failed", string.Join('\n', failed.Select(t => $"⚠️ {t}")).Truncate(1024));

            await command.FollowupAsync(embed: summary.Build());
        }
    }

    private async Task QueuePickedAsync(SearchResult picked, string type, ulong channelId, ulong userId)
    {
        var marker = CategoryMarkers.GetValueOrDefault(type, "");
        var taggedTitle = string.IsNullOrEmpty(marker) ? picked.Title : $"{marker} {picked.Title}";
        var infoHash = await ResolveInfoHashAsync(picked.MagnetOrTorrentLink);

        queue.Add(new PendingItem
        {
            Id = Guid.NewGuid().ToString(),
            Title = taggedTitle,
            Link = picked.MagnetOrTorrentLink,
            InfoHash = infoHash,
            ChannelId = channelId,
            UserId = userId
        });
        logger.LogInformation("Queued \"{Title}\" for the RSS feed; queue now has {Count} item(s)", taggedTitle, queue.GetAll().Count);

        if (infoHash is not null)
        {
            tracking.Track(new TrackedDownload(infoHash, picked.Title, channelId, userId));
        }
        else
        {
            logger.LogInformation("Could not resolve an info hash for {Title}; completion notification will not be tracked", picked.Title);
        }
    }

    private async Task<string?> ResolveInfoHashAsync(string link)
    {
        var magnetHash = MagnetHash.TryExtract(link);
        if (magnetHash is not null)
            return magnetHash;

        // Not a magnet link — Jackett gave back a .torrent file URL instead, which doesn't carry the
        // hash inline. Download it and compute the hash from its bencoded "info" dict. The redirect
        // chain can also land on a magnet URI directly (some trackers skip serving an actual .torrent
        // file) — that's handled inside the loop below, since it can't be fetched over HTTP.
        try
        {
            return await ResolveInfoHashFollowingRedirectsAsync(link);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download/parse .torrent file at {Link} to compute its info hash", link);
            return null;
        }
    }

    // Some indexer redirects (e.g. Jackett's /dl/ proxy landing on the tracker's own file host) carry
    // unencoded characters in the Location header that .NET's built-in redirect handling can't parse
    // into a valid connection authority. Following manually lets us sanitize each hop's Location first.
    private async Task<string?> ResolveInfoHashFollowingRedirectsAsync(string link)
    {
        var httpClient = httpClientFactory.CreateClient("TorrentFileDownloader");
        var currentUri = new Uri(link);

        for (var hop = 0; hop < 5; hop++)
        {
            if (currentUri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
                return MagnetHash.TryExtract(currentUri.OriginalString);

            using var response = await httpClient.GetAsync(currentUri, HttpCompletionOption.ResponseHeadersRead);

            if (!IsRedirect(response.StatusCode))
            {
                response.EnsureSuccessStatusCode();
                var torrentBytes = await response.Content.ReadAsByteArrayAsync();
                return TorrentInfoHash.TryCompute(torrentBytes);
            }

            var rawLocation = response.Headers.Location?.OriginalString;
            if (string.IsNullOrEmpty(rawLocation))
            {
                logger.LogWarning("Redirect from {Uri} had no usable Location header", currentUri);
                return null;
            }

            currentUri = ResolveRedirectUri(currentUri, rawLocation);
        }

        logger.LogWarning("Too many redirects while downloading {Link}", link);
        return null;
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.SeeOther
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;

    private static Uri ResolveRedirectUri(Uri baseUri, string rawLocation)
    {
        // Raw spaces are the most common offender in the wild; encode them before attempting to parse.
        var sanitized = rawLocation.Replace(" ", "%20");

        if (Uri.TryCreate(sanitized, UriKind.Absolute, out var absolute))
            return absolute;
        if (Uri.TryCreate(baseUri, sanitized, out var combined))
            return combined;

        throw new UriFormatException($"Could not resolve redirect location \"{rawLocation}\" against {baseUri}");
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

file static class StringExtensions
{
    public static string Truncate(this string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}
