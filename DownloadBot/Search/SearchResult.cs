namespace DownloadBot.Search;

public sealed record SearchResult(string Title, string MagnetOrTorrentLink, int Seeders, long SizeBytes);
