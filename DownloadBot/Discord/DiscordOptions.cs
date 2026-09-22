namespace DownloadBot.Discord;

public sealed class DiscordOptions
{
    public string Token { get; set; } = "";

    // Optional: when set, slash commands register instantly to this guild instead of globally (global registration can take ~1hr to propagate).
    public ulong? DevGuildId { get; set; }

    // Optional: channel for connect/disconnect notices and the periodic "still alive" heartbeat.
    public ulong? StatusChannelId { get; set; }

    // Optional: channel for the live, auto-refreshing "active downloads" dashboard message.
    // Leave unset to disable it (DashboardService just no-ops).
    public ulong? DashboardChannelId { get; set; }
}
