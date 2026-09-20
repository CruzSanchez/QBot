namespace DownloadBot.QBittorrent;

public sealed class QBittorrentOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 30;

    // Save path per /download "type" value, replacing what used to live in qBittorrent's Auto
    // Downloading Rules (e.g. "movie" -> "G:\\plex\\Movies"). Required for direct torrent adding.
    public Dictionary<string, string> SavePaths { get; set; } = new();
}
