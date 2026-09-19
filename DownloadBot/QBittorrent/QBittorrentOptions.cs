namespace DownloadBot.QBittorrent;

public sealed class QBittorrentOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 30;
}
