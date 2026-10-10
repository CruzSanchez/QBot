using global::Discord;
using DownloadBot.LocalLibrary;
using DownloadBot.QBittorrent;

namespace DownloadBot.Discord;

// Shared embed styling for anything showing download state — the live dashboard (DashboardService),
// /status, and the completion/stall/error alerts (CompletionPollerService) all use the same colors so
// a glance at the left border tells you what kind of message it is.
public static class DashboardFormatter
{
    public static readonly Color BlurpleColor = new(0x58, 0x65, 0xF2);
    public static readonly Color GreenColor = new(0x57, 0xF2, 0x87);
    public static readonly Color YellowColor = new(0xFE, 0xE7, 0x5C);
    public static readonly Color RedColor = new(0xED, 0x42, 0x45);

    // nextRefreshAt, when given, renders as Discord's native "<t:...:R>" relative timestamp — the
    // client itself keeps that counting down live, no repeated message edits required. Omit it (null)
    // when there won't be another refresh (e.g. /status's final tick).
    public static Embed BuildActiveDownloadsEmbed(IReadOnlyList<TorrentState> active, DateTimeOffset updatedAt, DateTimeOffset? nextRefreshAt = null)
    {
        var ordered = active.OrderByDescending(t => t.Progress).ToList();

        var builder = new EmbedBuilder()
            .WithColor(BlurpleColor)
            .WithTitle($"📥 Active downloads ({ordered.Count})")
            .WithDescription(ordered.Count == 0
                ? "Nothing downloading right now."
                : string.Join("\n\n", ordered.Select(FormatLine)))
            .WithFooter($"Updated {CentralTime.Format(updatedAt)}");

        if (nextRefreshAt is { } next)
            builder.AddField("Next update", $"<t:{next.ToUnixTimeSeconds()}:R>");

        return builder.Build();
    }

    // Shared by /drive-check and the scheduled drive-space report so the two always look the same.
    public static Embed BuildDriveSpaceEmbed(IReadOnlyList<DriveSpace> drives, DateTimeOffset? checkedAt = null)
    {
        var builder = new EmbedBuilder()
            .WithTitle("Drive space")
            .WithDescription(string.Join('\n', drives.Select(d => $"**{d.Name}** — {d.FreeGb:F2} GB free of {d.TotalGb:F2} GB")));

        if (checkedAt is { } at)
            builder.WithFooter($"Checked {CentralTime.Format(at)}");

        return builder.Build();
    }

    // /search results: one line per folder with its drive and category. truncated = more matched than shown.
    public static Embed BuildFolderSearchEmbed(string search, IReadOnlyList<PlexFolder> folders, bool truncated)
    {
        var builder = new EmbedBuilder()
            .WithColor(BlurpleColor)
            .WithTitle(Truncate($"Plex folders matching \"{search}\"", 250))
            .WithDescription(string.Join('\n', folders.Select(f => $"📁 **{Truncate(f.Name, 100)}** — {f.Drive} • {f.Category}")));

        if (truncated)
            builder.WithFooter($"Showing the first {folders.Count} — narrow the search to see others");

        return builder.Build();
    }

    private static string FormatLine(TorrentState t) =>
        $"**{Truncate(t.Name, 80)}**\n{ActiveDownloadFormatter.FormatProgressBar(t.Progress)} {t.Progress * 100:F1}% — " +
        $"{ActiveDownloadFormatter.FormatSpeedWithState(t.DownloadSpeedBytesPerSec, t.State)} — {ActiveDownloadFormatter.FormatEta(t.EtaSeconds)}";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
