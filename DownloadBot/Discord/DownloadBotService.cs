using System.Collections.Concurrent;
using global::Discord;
using global::Discord.WebSocket;
using DownloadBot.LocalLibrary;
using DownloadBot.QBittorrent;
using DownloadBot.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.Discord;

public sealed class DownloadBotService(
    DiscordSocketClient client,
    IJackettClient jackett,
    IQBitApiClient qbit,
    DownloadTrackingStore tracking,
    IPlexLibraryScanner libraryScanner,
    IDriveSpaceChecker driveSpaceChecker,
    IOptions<DiscordOptions> options,
    IOptions<QBittorrentOptions> qbitOptions,
    IHostApplicationLifetime appLifetime,
    ILogger<DownloadBotService> logger) : BackgroundService
{
    // Set once an intentional shutdown (Ctrl+C, service stop) begins, so the natural Disconnected
    // event that follows doesn't also try — and fail — to post its own redundant message once the
    // client/HttpClient are already mid-teardown.
    private volatile bool _isShuttingDown;

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

        // ApplicationStopping fires before hosted services are stopped and the client is disposed —
        // this is the last point where the bot is still fully connected, so it's the only reliable
        // place to send a "going down" message. The host blocks shutdown on this callback (up to its
        // shutdown timeout), which is exactly what's needed to let the send actually complete.
        appLifetime.ApplicationStopping.Register(() =>
        {
            _isShuttingDown = true;
            try
            {
                PostStatusAsync($"🔴 Bot shutting down - {FormatCentral(DateTimeOffset.UtcNow)}").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to post shutdown status message");
            }
        });

        _ = HeartbeatLoopAsync(stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private Task OnDisconnectedAsync(Exception ex)
    {
        logger.LogWarning(ex, "Discord gateway disconnected");
        if (_isShuttingDown)
            return Task.CompletedTask;

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

    // Short bounded backoff for transient failures (a brief DNS/network blip) — enough to ride out a
    // hiccup without meaningfully delaying the ApplicationStopping shutdown path that also calls this.
    private static readonly TimeSpan[] StatusPostRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private async Task PostStatusAsync(string message)
    {
        var channelId = options.Value.StatusChannelId;
        if (channelId is null)
            return;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (client.GetChannel(channelId.Value) is not IMessageChannel channel)
                {
                    logger.LogWarning("Could not resolve status channel {ChannelId}", channelId);
                    return;
                }

                await channel.SendMessageAsync(message);
                return;
            }
            catch (Exception ex) when (attempt < StatusPostRetryDelays.Length)
            {
                logger.LogWarning(ex, "Failed to post status message to channel {ChannelId} (attempt {Attempt}/{Total}) — retrying in {Delay}s",
                    channelId, attempt + 1, StatusPostRetryDelays.Length + 1, StatusPostRetryDelays[attempt].TotalSeconds);
                await Task.Delay(StatusPostRetryDelays[attempt]);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to post status message to channel {ChannelId} after {Attempts} attempt(s)", channelId, StatusPostRetryDelays.Length + 1);
                return;
            }
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
            .WithName("qbot-help")
            .WithDescription("Show how to use the download commands")
            .Build();

        var driveCheckCommand = new SlashCommandBuilder()
            .WithName("drive-check")
            .WithDescription("Show free space on attached drives (excludes C:)")
            .AddOption("drive", ApplicationCommandOptionType.String, "Optional: check only this drive letter (e.g. G)", isRequired: false)
            .Build();

        var activeDownloadsCommand = new SlashCommandBuilder()
            .WithName("active-downloads")
            .WithDescription("Show what qBittorrent is currently downloading")
            .Build();

        try
        {
            if (options.Value.DevGuildId is { } guildId)
            {
                await client.Rest.CreateGuildCommand(downloadCommand, guildId);
                await client.Rest.CreateGuildCommand(downloadManyCommand, guildId);
                await client.Rest.CreateGuildCommand(helpCommand, guildId);
                await client.Rest.CreateGuildCommand(driveCheckCommand, guildId);
                await client.Rest.CreateGuildCommand(activeDownloadsCommand, guildId);
                await RemoveRetiredGuildCommandsAsync(guildId);
            }
            else
            {
                await client.Rest.CreateGlobalCommand(downloadCommand);
                await client.Rest.CreateGlobalCommand(downloadManyCommand);
                await client.Rest.CreateGlobalCommand(helpCommand);
                await client.Rest.CreateGlobalCommand(driveCheckCommand);
                await client.Rest.CreateGlobalCommand(activeDownloadsCommand);
                await RemoveRetiredGlobalCommandsAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register slash command");
        }

        await PostStatusAsync($"🟢 Bot connected - {FormatCentral(DateTimeOffset.UtcNow)}");
    }

    // Command names retired by renames — individual Create*Command calls never remove a stale command
    // Discord still has registered under the old name, so it has to be deleted explicitly or it lingers
    // forever as a dead duplicate.
    private static readonly string[] RetiredCommandNames = ["download-help"];

    private async Task RemoveRetiredGuildCommandsAsync(ulong guildId)
    {
        var existing = await client.Rest.GetGuildApplicationCommands(guildId);
        foreach (var command in existing.Where(c => RetiredCommandNames.Contains(c.Name)))
        {
            await command.DeleteAsync();
            logger.LogInformation("Removed retired guild slash command \"{Name}\"", command.Name);
        }
    }

    private async Task RemoveRetiredGlobalCommandsAsync()
    {
        var existing = await client.Rest.GetGlobalApplicationCommands();
        foreach (var command in existing.Where(c => RetiredCommandNames.Contains(c.Name)))
        {
            await command.DeleteAsync();
            logger.LogInformation("Removed retired global slash command \"{Name}\"", command.Name);
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
            case "qbot-help":
                await HandleHelpAsync(command);
                break;
            case "drive-check":
                await HandleDriveCheckAsync(command);
                break;
            case "active-downloads":
                await HandleActiveDownloadsAsync(command);
                break;
        }
    }

    private async Task HandleActiveDownloadsAsync(SocketSlashCommand command)
    {
        await command.DeferAsync();

        IReadOnlyList<TorrentState> all;
        try
        {
            all = await qbit.GetAllTorrentsAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch active downloads from qBittorrent");
            await command.FollowupAsync($"Failed to reach qBittorrent: {ex.Message}");
            return;
        }

        var active = all.Where(t => t.IsActiveDownload).OrderByDescending(t => t.Progress).ToList();

        logger.LogInformation("/active-downloads invoked by {User} -> {Count} active", command.User.Username, active.Count);

        if (active.Count == 0)
        {
            await command.FollowupAsync("No active downloads right now.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"Active downloads ({active.Count})")
            .WithDescription(string.Join('\n', active.Select(FormatActiveDownload)))
            .Build();

        await command.FollowupAsync(embed: embed);
    }

    private static string FormatActiveDownload(TorrentState t) =>
        $"**{Truncate(t.Name, 80)}** — {t.Progress * 100:F1}% ({t.State}) — " +
        $"{ActiveDownloadFormatter.FormatSpeed(t.DownloadSpeedBytesPerSec)}, {ActiveDownloadFormatter.FormatEta(t.EtaSeconds)}";

    private Task HandleDriveCheckAsync(SocketSlashCommand command)
    {
        var driveOption = command.Data.Options.FirstOrDefault(o => o.Name == "drive")?.Value as string;

        IReadOnlyList<DriveSpace> results;
        try
        {
            results = driveSpaceChecker.GetFreeSpace(driveOption);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check drive space");
            return command.RespondAsync($"Failed to check drive space: {ex.Message}", ephemeral: true);
        }

        if (results.Count == 0)
        {
            var message = driveOption is null
                ? "No attached drives found (other than C:)."
                : $"Drive **{DriveSpaceChecker.NormalizeDriveName(driveOption)}** not found or not ready.";
            return command.RespondAsync(message, ephemeral: true);
        }

        logger.LogInformation("/drive-check invoked by {User}: drive={Drive} -> {Count} result(s)",
            command.User.Username, driveOption ?? "(all)", results.Count);

        var embed = new EmbedBuilder()
            .WithTitle("Drive space")
            .WithDescription(string.Join('\n', results.Select(d => $"**{d.Name}** — {d.FreeGb:F2} GB free of {d.TotalGb:F2} GB")))
            .Build();

        return command.RespondAsync(embed: embed);
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
            .AddField("/drive-check drive",
                "Shows free space on every attached drive except C:. Pass `drive` (e.g. `G`) to check just one.\n" +
                "Example: `/drive-check` or `/drive-check drive:G`")
            .AddField("/active-downloads",
                "Shows what qBittorrent is currently downloading, with progress, speed, and ETA for each.")
            .AddField("What happens after you pick",
                "The chosen release is added directly to qBittorrent — you'll know within a few seconds " +
                "whether it worked. You'll get pinged in this server once it finishes downloading.")
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
            // Acknowledge within Discord's 3-second window immediately — adding to qBittorrent and
            // confirming it took can take several seconds, which would otherwise make the later
            // response miss the window and fail with "Unknown interaction".
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

            var (added, infoHash) = await AddToQBittorrentAsync(picked, pick.Type);

            if (added && infoHash is not null)
            {
                tracking.Track(new TrackedDownload(infoHash, picked.Title, component.Channel.Id, component.User.Id));
            }

            await component.ModifyOriginalResponseAsync(m =>
            {
                m.Content = added
                    ? $"✅ **{picked.Title}** added to qBittorrent."
                    : $"⚠️ **{picked.Title}** could not be confirmed in qBittorrent — check the logs and add it manually if needed.";
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

    // Adds directly via qBittorrent's own API instead of writing to an RSS feed and hoping its RSS
    // Reader polls in time — that indirect handoff was racing qBittorrent's poll interval and its
    // Auto Downloading Rules, with failures only surfacing (or not) minutes later. This adds and
    // confirms within seconds, giving immediate, reliable feedback either way.
    private async Task<(bool Added, string? InfoHash)> AddToQBittorrentAsync(SearchResult picked, string type)
    {
        var marker = CategoryMarkers.GetValueOrDefault(type, "");
        var taggedTitle = string.IsNullOrEmpty(marker) ? picked.Title : $"{marker} {picked.Title}";

        if (!qbitOptions.Value.SavePaths.TryGetValue(type, out var savePath) || string.IsNullOrWhiteSpace(savePath))
        {
            logger.LogError("No QBittorrent:SavePaths entry configured for type \"{Type}\"", type);
            return (false, null);
        }

        try
        {
            await qbit.AddTorrentAsync(picked.MagnetOrTorrentLink, savePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "qBittorrent rejected adding \"{Title}\"", taggedTitle);
            return (false, null);
        }

        var knownHash = MagnetHash.TryExtract(picked.MagnetOrTorrentLink);
        var confirmedHash = await ConfirmAddedAsync(knownHash, picked.Title);

        if (confirmedHash is null)
        {
            logger.LogWarning("qBittorrent never showed \"{Title}\" after adding it — treating as failed", taggedTitle);
            return (false, null);
        }

        logger.LogInformation("Confirmed \"{Title}\" added to qBittorrent (hash {Hash}, save path {SavePath})", taggedTitle, confirmedHash, savePath);
        return (true, confirmedHash);
    }

    // qBittorrent's "Ok." response to /torrents/add is not a reliable success signal on its own, so
    // this polls for the torrent to actually appear before calling the add successful.
    private async Task<string?> ConfirmAddedAsync(string? knownHash, string title)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            try
            {
                if (knownHash is not null)
                {
                    if (await qbit.GetTorrentStateAsync(knownHash) is not null)
                        return knownHash;
                    continue;
                }

                var all = await qbit.GetAllTorrentsAsync();
                var match = all.FirstOrDefault(t => TorrentNameMatcher.LooselyMatch(t.Name, title));
                if (match is not null)
                    return match.Hash;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Confirmation check failed for \"{Title}\" (attempt {Attempt})", title, attempt + 1);
            }
        }

        return null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

file static class StringExtensions
{
    public static string Truncate(this string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}
