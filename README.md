# DownloadBot

Single .NET process running a Discord bot. `/download` searches Jackett, lets you
pick a result, checks your Plex library for an existing copy first, and adds the
chosen release directly to qBittorrent via its Web API.

```
Discord /download → local library check ("already have this?") → Jackett search
                                                                          ↓
                                                              pick a result
                                                                          ↓
                                            POST /api/v2/torrents/add (qBittorrent)
                                                                          ↓
                                    confirm it actually appears (polls for ~10s)
                                                                          ↓
                              CompletionPollerService watches qBittorrent's Web API
                                                                          ↓
                                    posts "finished downloading" back to Discord
```

qBittorrent's RSS Reader / Auto Downloading Rules are **not used** — the bot adds
torrents directly and gets an immediate, reliable success/failure signal instead of
waiting on an RSS poll cycle. You can leave your existing rules in place (harmless)
or remove them.

## Setup

See [CLARIFICATIONS.md](CLARIFICATIONS.md) for every credential and config value
that needs to be filled in (Discord bot token, Jackett API key, qBittorrent WebUI
login, and the per-category save paths qBittorrent uses when adding a torrent).

```bash
cd DownloadBot
dotnet user-secrets set "Discord:Token" "..." --project .
dotnet user-secrets set "Jackett:ApiKey" "..." --project .
dotnet user-secrets set "QBittorrent:Username" "..." --project .
dotnet user-secrets set "QBittorrent:Password" "..." --project .
dotnet run
```

The app listens on `http://localhost:5151` (`/` is just a health check — there's no
other public endpoint).

## Project layout

```
DownloadBot/
├── Program.cs                        // wires everything together
├── Discord/
│   ├── DiscordOptions.cs
│   └── DownloadBotService.cs         // slash commands, picker, direct qBittorrent add
├── Search/
│   ├── SearchResult.cs
│   ├── IJackettClient.cs
│   └── JackettClient.cs              // Torznab query + result parsing
├── LocalLibrary/
│   ├── TitleYear.cs                  // parses "<title> <year>" from a query or folder name
│   └── PlexLibraryScanner.cs         // checks \plex\<category> folders across drives for an existing copy
└── QBittorrent/
    ├── QBittorrentOptions.cs
    ├── QBitApiClient.cs              // session-cookie auth, add/list/lookup torrents
    ├── DownloadTrackingStore.cs      // hash → {title, channel, user} awaiting completion
    ├── TorrentNameMatcher.cs         // fuzzy name matching when a hash isn't known upfront
    ├── MagnetHash.cs                 // pulls the btih hash out of a magnet URI
    └── CompletionPollerService.cs    // polls qBittorrent, posts completion/error to Discord
```
