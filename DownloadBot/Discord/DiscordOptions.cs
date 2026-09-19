namespace DownloadBot.Discord;

public sealed class DiscordOptions
{
    public string Token { get; set; } = "";

    // Optional: when set, slash commands register instantly to this guild instead of globally (global registration can take ~1hr to propagate).
    public ulong? DevGuildId { get; set; }
}
