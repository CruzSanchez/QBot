namespace DownloadBot.Search;

// InfoHash (the torrent's unique id, when the indexer reports it) lets the same release found on several
// sites be recognised as one result.
public sealed record SearchResult(string Title, string MagnetOrTorrentLink, int Seeders, long SizeBytes, string? InfoHash = null);
