namespace DownloadBot.QBittorrent;

public sealed class QBittorrentOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 30;

    // Alert once if a tracked download shows no forward progress for this long — a dead tracker or
    // no seeders, distinct from qBittorrent's own error states.
    public int StallAlertMinutes { get; set; } = 20;

    // Save path per drive letter, then per /download "type" value — e.g. SavePaths["G"]["movie"] ->
    // "G:\\plex\\Movies". Every drive listed here is expected to mirror the same folder layout under
    // a different letter; ActiveDriveStore/"/switch-drive" pick which one new downloads route to.
    public Dictionary<string, Dictionary<string, string>> SavePaths { get; set; } = new();

    // Which key in SavePaths new downloads route to until /switch-drive changes it (persisted to
    // data/active-drive.json, so a restart doesn't reset back to this).
    public string DefaultDrive { get; set; } = "G";
}
