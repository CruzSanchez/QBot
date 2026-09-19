# DownloadBot

Single .NET process that hosts a Discord bot and a local RSS feed. A `/download`
slash command searches Jackett, lets you pick a result in Discord, and queues it
into an in-memory feed that qBittorrent's RSS Downloader polls and auto-adds.

```
Discord /download → Jackett search → pick a result → item added to /feed (RSS)
                                                              ↓
                                    qBittorrent RSS Reader polls http://localhost:5151/feed
                                                              ↓
                                    Auto Downloading Rule matches → torrent added
                                                              ↓
                              CompletionPollerService watches qBittorrent's Web API
                                                              ↓
                                    posts "finished downloading" back to Discord
```

## Setup

See [CLARIFICATIONS.md](CLARIFICATIONS.md) for every credential and config value
that needs to be filled in (Discord bot token, Jackett API key, qBittorrent WebUI
login) plus the one-time manual qBittorrent RSS/rule setup.

```bash
cd DownloadBot
dotnet user-secrets set "Discord:Token" "..." --project .
dotnet user-secrets set "Jackett:ApiKey" "..." --project .
dotnet user-secrets set "QBittorrent:Username" "..." --project .
dotnet user-secrets set "QBittorrent:Password" "..." --project .
dotnet run
```

The app listens on `http://localhost:5151` (`/feed` serves the RSS feed; `/` is a
health check).

## Project layout

```
DownloadBot/
├── Program.cs                        // wires everything together
├── Discord/
│   ├── DiscordOptions.cs
│   └── DownloadBotService.cs         // /download slash command + result picker
├── Search/
│   ├── SearchResult.cs
│   ├── IJackettClient.cs
│   └── JackettClient.cs              // Torznab query + result parsing
├── Feed/
│   ├── PendingItem.cs
│   ├── PendingItemQueue.cs
│   └── FeedEndpoint.cs               // GET /feed → RSS 2.0
└── QBittorrent/
    ├── QBittorrentOptions.cs
    ├── QBitApiClient.cs              // session-cookie auth + torrent state lookup
    ├── DownloadTrackingStore.cs      // hash → {title, channel, user} awaiting completion
    ├── MagnetHash.cs                 // pulls the btih hash out of a magnet URI
    └── CompletionPollerService.cs    // polls qBittorrent, posts completion to Discord
```
