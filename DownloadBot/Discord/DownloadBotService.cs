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

    // "Not enough free space, add anyway?" confirmations, keyed by that message's id.
    private readonly ConcurrentDictionary<ulong, PendingSpaceConfirmation> _pendingSpaceConfirmations = new();

    // /cancel's "which torrent?" picker options (sourced from qBittorrent directly, not tied to
    // whoever added it), keyed by that message's id.
    private readonly ConcurrentDictionary<ulong, IReadOnlyList<TorrentState>> _pendingCancelPicks = new();

    // /cancel's "remove, remove+delete, or nevermind?" confirmations, keyed by that message's id.
    private readonly ConcurrentDictionary<ulong, TorrentState> _pendingCancelConfirmations = new();

    private sealed record PendingPick(string Type, IReadOnlyList<SearchResult> Results);

    private sealed record PendingDuplicateConfirmation(SocketSlashCommand Command, string Title, string Type);

    private sealed record PendingSpaceConfirmation(SearchResult Picked, string Type);

    // Prefixed onto the title tracked in Discord/logs so content type is obvious at a glance instead
    // of guessing from often-inconsistent torrent titles.
    private static readonly Dictionary<string, string> CategoryMarkers = new()
    {
        ["movie"] = "[DLBOT-MOVIE]",
        ["tv"] = "[DLBOT-TV]",
        ["kids-movie"] = "[DLBOT-KIDS-MOVIE]",
        ["kids-tv"] = "[DLBOT-KIDS-TV]",
        ["music"] = "[DLBOT-MUSIC]"
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Discord.Net dispatches events by awaiting the handler inline on its own gateway processing
        // loop — an event handler doing real work (REST calls, retries) blocks that loop for as long
        // as it takes, which can delay heartbeats and other incoming events. Firing the actual work
        // off as a detached task (the standard Discord.Net pattern: return an already-completed Task
        // immediately, let the real handler run independently) keeps the gateway loop free regardless
        // of how long any individual handler takes. This matters a lot more now that PostStatusAsync
        // (called from Connected) has a retry budget of up to ~30 seconds.
        client.Log += LogAsync;
        client.Connected += () => FireAndForget(OnConnectedAsync, "Connected");
        client.Ready += () => FireAndForget(OnReadyAsync, "Ready");
        client.Disconnected += ex => FireAndForget(() => OnDisconnectedAsync(ex), "Disconnected");
        client.SlashCommandExecuted += command => FireAndForget(() => OnSlashCommandExecutedAsync(command), "SlashCommandExecuted");
        client.SelectMenuExecuted += component => FireAndForget(() => OnSelectMenuExecutedAsync(component), "SelectMenuExecuted");
        client.ButtonExecuted += component => FireAndForget(() => OnButtonExecutedAsync(component), "ButtonExecuted");

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
                PostStatusAsync($"🔴 Bot shutting down - {CentralTime.Format(DateTimeOffset.UtcNow)}").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to post shutdown status message");
            }
        });

        _ = HeartbeatLoopAsync(stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    // Returns immediately with an already-completed Task (so the gateway dispatch loop isn't blocked),
    // while the actual handler runs independently. Any exception that escapes the handler is caught
    // and logged here instead of becoming an unobserved task exception.
    private Task FireAndForget(Func<Task> handler, string name)
    {
        _ = RunSafelyAsync(handler, name);
        return Task.CompletedTask;
    }

    private async Task RunSafelyAsync(Func<Task> handler, string name)
    {
        try
        {
            await handler();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in {Handler} event handler", name);
        }
    }

    private Task OnDisconnectedAsync(Exception ex)
    {
        logger.LogWarning(ex, "Discord gateway disconnected");
        if (_isShuttingDown)
            return Task.CompletedTask;

        return PostStatusAsync($"🔴 Bot disconnected: {ex.Message}");
    }

    // Connected fires on every successful (re)connection — a fresh identify at startup AND a resumed
    // session after a transient drop ("Server requested a reconnect"). Ready, by contrast, only fires
    // on a fresh identify; Discord.Net doesn't re-fire it after a resume, since the client already has
    // its guild/session data cached. Posting the connect notice here (instead of from Ready) is what
    // actually closes the gap between every disconnect notice and its matching reconnect notice.
    private Task OnConnectedAsync() =>
        PostStatusAsync($"🟢 Bot connected - {CentralTime.Format(DateTimeOffset.UtcNow)}");

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PostStatusAsync($"Bot Status: LIVE - {CentralTime.Format(DateTimeOffset.UtcNow)}");
        }
    }

    // Covers two different transient conditions with one retry schedule: a brief network/DNS blip on
    // the send itself, and — the more common one in practice — the status channel not being resolvable
    // yet because Discord.Net's guild/channel cache isn't fully populated immediately when Connected
    // fires (observed gap between Connected and Ready: 20+ seconds). Without retrying the "not found"
    // case too, a fresh connect's notice would silently never send. ~30s total budget.
    private static readonly TimeSpan[] StatusPostRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)];

    private async Task PostStatusAsync(string message)
    {
        var channelId = options.Value.StatusChannelId;
        if (channelId is null)
            return;

        for (var attempt = 0; ; attempt++)
        {
            var isLastAttempt = attempt >= StatusPostRetryDelays.Length;

            if (client.GetChannel(channelId.Value) is not IMessageChannel channel)
            {
                if (isLastAttempt)
                {
                    logger.LogWarning("Could not resolve status channel {ChannelId} after {Attempts} attempt(s)", channelId, attempt + 1);
                    return;
                }

                logger.LogDebug("Status channel {ChannelId} not resolvable yet (attempt {Attempt}) — retrying in {Delay}s",
                    channelId, attempt + 1, StatusPostRetryDelays[attempt].TotalSeconds);
                await Task.Delay(StatusPostRetryDelays[attempt]);
                continue;
            }

            try
            {
                await channel.SendMessageAsync(message);
                return;
            }
            catch (Exception ex) when (!isLastAttempt)
            {
                logger.LogWarning(ex, "Failed to post status message to channel {ChannelId} (attempt {Attempt}/{Total}) — retrying in {Delay}s",
                    channelId, attempt + 1, StatusPostRetryDelays.Length + 1, StatusPostRetryDelays[attempt].TotalSeconds);
                await Task.Delay(StatusPostRetryDelays[attempt]);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to post status message to channel {ChannelId} after {Attempts} attempt(s)", channelId, attempt + 1);
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
        new ApplicationCommandOptionChoiceProperties { Name = "TV Shows", Value = "tv" },
        new ApplicationCommandOptionChoiceProperties { Name = "Kids Movie", Value = "kids-movie" },
        new ApplicationCommandOptionChoiceProperties { Name = "Kids TV Shows", Value = "kids-tv" },
        new ApplicationCommandOptionChoiceProperties { Name = "Music", Value = "music" }
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

        var cancelCommand = new SlashCommandBuilder()
            .WithName("cancel")
            .WithDescription("Cancel any active or stuck torrent in qBittorrent")
            .Build();

        var statusCommand = new SlashCommandBuilder()
            .WithName("status")
            .WithDescription("Live-updating view of active downloads for about a minute")
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
                await client.Rest.CreateGuildCommand(cancelCommand, guildId);
                await client.Rest.CreateGuildCommand(statusCommand, guildId);
                await RemoveRetiredGuildCommandsAsync(guildId);
            }
            else
            {
                await client.Rest.CreateGlobalCommand(downloadCommand);
                await client.Rest.CreateGlobalCommand(downloadManyCommand);
                await client.Rest.CreateGlobalCommand(helpCommand);
                await client.Rest.CreateGlobalCommand(driveCheckCommand);
                await client.Rest.CreateGlobalCommand(activeDownloadsCommand);
                await client.Rest.CreateGlobalCommand(cancelCommand);
                await client.Rest.CreateGlobalCommand(statusCommand);
                await RemoveRetiredGlobalCommandsAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register slash command");
        }
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
            case "cancel":
                await HandleCancelAsync(command);
                break;
            case "status":
                await HandleStatusAsync(command);
                break;
        }
    }

    // Posts the same embed the live dashboard shows and self-edits it every few seconds for about a
    // minute — a cheap "live view" for anyone who wants one without waiting on DashboardChannelId.
    private async Task HandleStatusAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        logger.LogInformation("/status invoked by {User}", command.User.Username);

        const int ticks = 12;
        var tickInterval = TimeSpan.FromSeconds(5);

        for (var i = 0; i < ticks; i++)
        {
            IReadOnlyList<TorrentState> all;
            try
            {
                all = await qbit.GetAllTorrentsAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "/status failed to reach qBittorrent");
                await command.FollowupAsync($"Failed to reach qBittorrent: {ex.Message}", ephemeral: true);
                return;
            }

            var active = all.Where(t => t.IsActiveDownload).ToList();
            var isLast = i == ticks - 1;
            var now = DateTimeOffset.UtcNow;
            var embed = DashboardFormatter.BuildActiveDownloadsEmbed(active, now, isLast ? null : now + tickInterval);

            await command.ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embed;
                m.Content = isLast ? "Live view stopped — run /status again to refresh." : null;
            });

            if (isLast)
                break;

            await Task.Delay(tickInterval);
        }
    }

    private async Task HandleCancelAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        // Sourced directly from qBittorrent — same as /active-downloads — rather than from our own
        // DownloadTrackingStore, which only knows about adds the bot itself confirmed and attributed
        // to whoever ran /download. That missed anything added by someone else, added outside the
        // bot entirely, or where the post-add confirmation step happened to false-negative. Not tied
        // to any particular user: anyone can cancel anything currently active or stuck.
        IReadOnlyList<TorrentState> all;
        try
        {
            all = await qbit.GetAllTorrentsAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch torrents from qBittorrent for /cancel");
            await command.FollowupAsync($"Failed to reach qBittorrent: {ex.Message}", ephemeral: true);
            return;
        }

        var candidates = all.Where(t => t.IsActiveDownload || t.IsError).Take(25).ToList(); // Discord select menus cap at 25 options

        if (candidates.Count == 0)
        {
            await command.FollowupAsync("Nothing active or stuck in qBittorrent to cancel.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId("cancel-pick")
            .WithPlaceholder("Choose a torrent to cancel");

        foreach (var (torrent, index) in candidates.Select((t, i) => (t, i)))
            menu.AddOption(Truncate(torrent.Name, 100), index.ToString(), Truncate(torrent.State, 100));

        var componentBuilder = new ComponentBuilder().WithSelectMenu(menu);

        var message = await command.FollowupAsync("Which torrent do you want to cancel?", components: componentBuilder.Build(), ephemeral: true);
        _pendingCancelPicks[message.Id] = candidates;

        logger.LogInformation("/cancel invoked by {User} -> {Count} candidate(s) to choose from", command.User.Username, candidates.Count);
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
                "Search for one title. Pick your `type` (Movie, TV Shows, Kids Movie, Kids TV Shows, Music), " +
                "then choose the exact release from the dropdown of top results (or hit Cancel to back out).\n" +
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
            .AddField("/status",
                "Same as /active-downloads, but keeps refreshing itself every few seconds for about a minute — " +
                "a quick live view without leaving Discord open on a channel.")
            .AddField("/cancel",
                "Cancel any active or stuck torrent in qBittorrent — not just ones you added. Pick " +
                "which one, then choose to remove it (keeping any partially-downloaded files) or " +
                "remove and delete the files too.")
            .AddField("What happens after you pick",
                "The chosen release is added directly to qBittorrent — you'll know within a few seconds " +
                "whether it worked. If the destination drive doesn't have enough free space, you'll be " +
                "asked to confirm before it adds anyway. You'll get pinged in this server once it finishes " +
                "downloading, if it stalls with no progress, or if it fails — stall/error alerts carry a " +
                "\"Cancel this download\" button so you can act on them right away.")
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
            matches = await libraryScanner.FindExistingAsync(title, type);
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

    private Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (component.Data.CustomId.StartsWith("poller-cancel:", StringComparison.Ordinal))
            return HandlePollerCancelButtonAsync(component);

        return component.Data.CustomId switch
        {
            "dup-confirm-yes" or "dup-confirm-no" => HandleDuplicateConfirmAsync(component),
            "download-pick-cancel" => HandlePickerCancelAsync(component),
            "space-confirm-yes" or "space-confirm-no" => HandleSpaceConfirmAsync(component),
            "cancel-confirm-remove" or "cancel-confirm-remove-delete" or "cancel-confirm-no" => HandleCancelConfirmAsync(component),
            _ => Task.CompletedTask
        };
    }

    // Lets someone jump straight from a completion/stall/error alert into the same remove/remove+delete/
    // nevermind flow /cancel uses, without re-finding the torrent themselves. Ephemeral, like /cancel,
    // and leaves the original public alert message untouched.
    private async Task HandlePollerCancelButtonAsync(SocketMessageComponent component)
    {
        var hash = component.Data.CustomId["poller-cancel:".Length..];

        try
        {
            await component.DeferAsync(ephemeral: true);

            var state = await qbit.GetTorrentStateAsync(hash);
            if (state is null)
            {
                await component.FollowupAsync("That torrent isn't in qBittorrent anymore.", ephemeral: true);
                return;
            }

            var buttons = new ComponentBuilder()
                .WithButton("Remove (keep files)", "cancel-confirm-remove", ButtonStyle.Primary)
                .WithButton("Remove + delete files", "cancel-confirm-remove-delete", ButtonStyle.Danger)
                .WithButton("Nevermind", "cancel-confirm-no", ButtonStyle.Secondary);

            var message = await component.FollowupAsync(
                $"Cancel **{state.Name}**? Files already downloaded are kept unless you choose to delete them.",
                components: buttons.Build(), ephemeral: true);

            _pendingCancelConfirmations[message.Id] = state;
            logger.LogInformation("User {User} started a cancel from an alert button for \"{Title}\" (hash {Hash})",
                component.User.Username, state.Name, hash);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle poller-cancel button for hash {Hash}", hash);
        }
    }

    private async Task HandleDuplicateConfirmAsync(SocketMessageComponent component)
    {
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

    private Task HandlePickerCancelAsync(SocketMessageComponent component)
    {
        _pendingPicks.TryRemove(component.Message.Id, out _);
        logger.LogInformation("User {User} cancelled the picker on message {MessageId}", component.User.Username, component.Message.Id);

        return component.UpdateAsync(m =>
        {
            m.Content = "Cancelled.";
            m.Embed = null;
            m.Components = new ComponentBuilder().Build();
        });
    }

    private async Task HandleSpaceConfirmAsync(SocketMessageComponent component)
    {
        try
        {
            if (!_pendingSpaceConfirmations.TryRemove(component.Message.Id, out var pending))
            {
                await component.UpdateAsync(m => m.Content = "This confirmation has expired.");
                return;
            }

            if (component.Data.CustomId == "space-confirm-no")
            {
                logger.LogInformation("User {User} declined to add \"{Title}\" due to low free space", component.User.Username, pending.Picked.Title);
                await component.UpdateAsync(m =>
                {
                    m.Content = $"Skipped **{pending.Picked.Title}** — not enough free space.";
                    m.Embed = null;
                    m.Components = new ComponentBuilder().Build();
                });
                return;
            }

            logger.LogInformation("User {User} chose to add \"{Title}\" anyway despite low free space", component.User.Username, pending.Picked.Title);
            await component.DeferAsync();
            await CompleteAddAsync(component, pending.Picked, pending.Type);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle space confirmation on message {MessageId}", component.Message.Id);
        }
    }

    private async Task HandleCancelConfirmAsync(SocketMessageComponent component)
    {
        try
        {
            if (!_pendingCancelConfirmations.TryRemove(component.Message.Id, out var chosen))
            {
                await component.UpdateAsync(m => m.Content = "This confirmation has expired.");
                return;
            }

            if (component.Data.CustomId == "cancel-confirm-no")
            {
                logger.LogInformation("User {User} decided not to cancel \"{Title}\" after all", component.User.Username, chosen.Name);
                await component.UpdateAsync(m =>
                {
                    m.Content = $"Left **{chosen.Name}** as is.";
                    m.Components = new ComponentBuilder().Build();
                });
                return;
            }

            var deleteFiles = component.Data.CustomId == "cancel-confirm-remove-delete";
            await component.DeferAsync();

            try
            {
                await qbit.RemoveTorrentAsync(chosen.Hash, deleteFiles);
                tracking.Untrack(chosen.Hash);
                logger.LogInformation("User {User} cancelled \"{Title}\" (hash {Hash}), deleteFiles={DeleteFiles}",
                    component.User.Username, chosen.Name, chosen.Hash, deleteFiles);

                await component.ModifyOriginalResponseAsync(m =>
                {
                    m.Content = deleteFiles
                        ? $"🗑️ Removed **{chosen.Name}** and deleted its files."
                        : $"🛑 Removed **{chosen.Name}** (files kept).";
                    m.Components = new ComponentBuilder().Build();
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to cancel \"{Title}\"", chosen.Name);
                await component.ModifyOriginalResponseAsync(m => m.Content = $"Failed to cancel **{chosen.Name}**: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle cancel confirmation on message {MessageId}", component.Message.Id);
        }
    }

    // TV shows tend to have far more scattered/duplicate releases (per-episode, per-season, remux vs
    // web-dl, etc.) than movies, so a picker capped at 5 more often misses the release you actually
    // want — 10 gives more room without approaching the select menu's 25-option limit.
    private static readonly HashSet<string> TypesWithExpandedResults = new(StringComparer.OrdinalIgnoreCase) { "tv", "kids-tv" };

    private async Task PostPickerAsync(SocketSlashCommand command, string title, string type, IReadOnlyList<SearchResult> results)
    {
        var resultLimit = TypesWithExpandedResults.Contains(type) ? 10 : 5;
        var top = results.Take(resultLimit).ToList();

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

        var componentBuilder = new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("Cancel", "download-pick-cancel", ButtonStyle.Secondary, row: 1);

        var embed = new EmbedBuilder()
            .WithTitle($"Results for \"{title}\" ({type})")
            .WithDescription(string.Join('\n', top.Select((r, i) => $"**{i + 1}.** {r.Title} — {r.Seeders} seeders")))
            .Build();

        var message = await command.FollowupAsync(embed: embed, components: componentBuilder.Build());
        _pendingPicks[message.Id] = new PendingPick(type, top);
        logger.LogInformation("Posted picker message {MessageId} for \"{Title}\" with {Count} option(s)", message.Id, title, top.Count);
    }

    private Task OnSelectMenuExecutedAsync(SocketMessageComponent component) => component.Data.CustomId switch
    {
        "download-pick" => HandleDownloadPickAsync(component),
        "cancel-pick" => HandleCancelPickAsync(component),
        _ => Task.CompletedTask
    };

    private async Task HandleDownloadPickAsync(SocketMessageComponent component)
    {
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

            if (await PostSpaceWarningIfInsufficientAsync(component, picked, pick.Type))
                return;

            await CompleteAddAsync(component, picked, pick.Type);
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

    // Checks the destination drive's free space against the release's reported size before adding —
    // same "soft warning, not a hard stop" pattern as the library duplicate-check. Returns true (and
    // posts an "add anyway?" confirmation instead) if space looks insufficient; false if there's
    // enough room, or if the check itself couldn't be done (never block on our own check failing).
    private async Task<bool> PostSpaceWarningIfInsufficientAsync(SocketMessageComponent component, SearchResult picked, string type)
    {
        if (!qbitOptions.Value.SavePaths.TryGetValue(type, out var savePath) || string.IsNullOrWhiteSpace(savePath))
            return false; // AddToQBittorrentAsync will surface this same configuration problem itself

        try
        {
            var driveRoot = Path.GetPathRoot(savePath);
            if (string.IsNullOrEmpty(driveRoot))
                return false;

            var drive = driveSpaceChecker.GetFreeSpace(driveRoot).FirstOrDefault();
            if (drive is null)
                return false;

            var requiredGb = picked.SizeBytes / 1024.0 / 1024.0 / 1024.0;
            if (drive.FreeGb >= requiredGb)
                return false;

            logger.LogWarning("Low space warning for \"{Title}\": needs {RequiredGb:F2} GB, {Drive} has {FreeGb:F2} GB free",
                picked.Title, requiredGb, drive.Name, drive.FreeGb);

            var embed = new EmbedBuilder()
                .WithTitle("⚠️ Not enough free space?")
                .WithDescription(
                    $"**{picked.Title}** needs about **{requiredGb:F2} GB**, but **{drive.Name}** only has " +
                    $"**{drive.FreeGb:F2} GB** free.")
                .WithFooter("Add it anyway?")
                .Build();

            var buttons = new ComponentBuilder()
                .WithButton("Add anyway", "space-confirm-yes", ButtonStyle.Primary)
                .WithButton("Cancel", "space-confirm-no", ButtonStyle.Secondary);

            await component.ModifyOriginalResponseAsync(m =>
            {
                m.Content = null;
                m.Embed = embed;
                m.Components = buttons.Build();
            });

            _pendingSpaceConfirmations[component.Message.Id] = new PendingSpaceConfirmation(picked, type);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Low-space check failed for \"{Title}\"; proceeding without it", picked.Title);
            return false;
        }
    }

    private async Task CompleteAddAsync(SocketMessageComponent component, SearchResult picked, string type)
    {
        var (added, infoHash) = await AddToQBittorrentAsync(picked, type);

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

    private async Task HandleCancelPickAsync(SocketMessageComponent component)
    {
        if (!_pendingCancelPicks.TryRemove(component.Message.Id, out var candidates))
        {
            await component.UpdateAsync(m => m.Content = "This selection has expired.");
            return;
        }

        var index = int.Parse(component.Data.Values.First());
        var chosen = candidates[index];
        logger.LogInformation("User {User} selected \"{Title}\" (hash {Hash}) to cancel", component.User.Username, chosen.Name, chosen.Hash);

        var buttons = new ComponentBuilder()
            .WithButton("Remove (keep files)", "cancel-confirm-remove", ButtonStyle.Primary)
            .WithButton("Remove + delete files", "cancel-confirm-remove-delete", ButtonStyle.Danger)
            .WithButton("Nevermind", "cancel-confirm-no", ButtonStyle.Secondary);

        await component.UpdateAsync(m =>
        {
            m.Content = $"Cancel **{chosen.Name}**? Files already downloaded are kept unless you choose to delete them.";
            m.Components = buttons.Build();
        });

        _pendingCancelConfirmations[component.Message.Id] = chosen;
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
