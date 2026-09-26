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
                        posts "finished downloading" / stall / error alerts to Discord
```

Tracking (`hash → {title, channel, user}`) persists to `data/tracked-downloads.json`
so a bot restart doesn't lose the completion ping for anything already added.

Optionally, set `Discord:DashboardChannelId` and the bot keeps one message in that
channel continuously up to date with everything currently downloading — no need to
run a command to check. `/status` gives anyone the same live view on demand,
ephemeral, for about a minute. Completion/stall/error alerts are color-coded embeds;
stall/error ones carry a "Cancel this download" button. See
[CLARIFICATIONS.md](CLARIFICATIONS.md) for setup.

qBittorrent's RSS Reader / Auto Downloading Rules are **not used** — the bot adds
torrents directly and gets an immediate, reliable success/failure signal instead of
waiting on an RSS poll cycle. You can leave your existing rules in place (harmless)
or remove them.

`/download-yt url:<link>` is a separate pipeline: it shells out to
[yt-dlp](https://github.com/yt-dlp/yt-dlp) to download a video or playlist (YouTube
and hundreds of other sites) and saves it to the active drive's Youtube folder —
no Jackett/qBittorrent involved. Each video gets its own folder (named after its
title) by default, since Plex generally won't pick up a flat pile of video files —
pass `addtofolder` (autocompletes against existing folders) or `newfoldername`
(creates one) to instead group several related videos together (e.g. a montage
series acting as one Plex show). `quality` caps the resolution (1080p by default,
or 4K/720p/best available uncapped). Shows live progress with a Cancel button; a
second `/download-yt` queues behind one already running instead of running in
parallel. Folder names only ever get letters, digits, spaces, `-`, or `_` —
anything else is cleaned up automatically instead of erroring. `/rename-folder`
renames an existing folder under the active drive's Youtube folder (autocompletes
the folder to pick). See [CLARIFICATIONS.md](CLARIFICATIONS.md) for yt-dlp/ffmpeg
setup.

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

## Running it without a terminal

For day-to-day use (or if whoever's running the server isn't a developer):

- **`run-bot.bat`** — double-click to start it. Does the same `dotnet run` shown above
  from inside `DownloadBot/`, so it picks up the same secrets/config automatically.
  Requires the .NET SDK on that machine (the same one used to build/test it) — this
  isn't a portable/standalone build, just a launcher.
- **`install-startup-task.bat`** — one-time setup (ideally "Run as administrator")
  that registers a Windows Scheduled Task to run `run-bot.bat` automatically at
  login, so the bot comes back up on its own after a restart. Modifies Task
  Scheduler, so run it yourself rather than expecting it to happen silently.
- **`uninstall-startup-task.bat`** — removes that scheduled task.

## Auto-deploy on push

`.github/workflows/deploy.yml` runs on a self-hosted GitHub Actions runner
installed on the server itself: on every push to `main` it pulls, builds, and
restarts the bot automatically — no manual `git pull` needed. One-time setup
(installing the runner, setting the `SERVER_REPO_PATH` repo variable) is in
[CLARIFICATIONS.md](CLARIFICATIONS.md).

## Running tests

```bash
dotnet test
```

`DownloadBot.Tests` has two kinds of tests:

- **Unit tests** — pure logic (title/year parsing, magnet hash extraction, fuzzy
  name matching, library folder scanning against a real temp directory). No
  external services needed; these always run.
- **Integration tests** — read-only checks against live Jackett, qBittorrent, and
  Discord using whatever credentials are already set up (same `UserSecretsId` as
  `DownloadBot`, so nothing to configure separately). These **skip themselves**
  (not fail) when a service isn't reachable or configured, so `dotnet test` stays
  quiet when the server's off and meaningful when it's on — exactly the "kick off
  when I turn the server on" workflow.
  - Jackett: runs a real search, asserts results come back.
  - qBittorrent: logs in, lists torrents, looks up a hash that doesn't exist.
  - Discord: logs in and waits for the gateway to reach Ready — it does **not**
    wire up the bot's own event handlers, so it never registers commands or posts
    to any channel.
  - **Deliberately not tested**: adding a real torrent, and simulating an actual
    slash-command/button interaction. The first would mean every test run adds
    something to your real qBittorrent; the second isn't realistically possible —
    Discord.Net's interaction objects are sealed types the gateway hands you, not
    something you can construct or mock. If you want either covered anyway (e.g.
    an opt-in add-a-known-safe-torrent test gated behind an env var), say so.

## Project layout

```
DownloadBot/
├── Program.cs                        // wires everything together
├── Discord/
│   ├── DiscordOptions.cs
│   ├── DownloadBotService.cs         // slash commands, picker, direct qBittorrent add
│   ├── DashboardService.cs           // live "active downloads" message, edited in place on a timer
│   ├── DashboardFormatter.cs         // shared embed styling for the dashboard, /status, and alerts
│   └── CentralTime.cs                // shared timestamp formatting
├── Search/
│   ├── SearchResult.cs
│   ├── IJackettClient.cs
│   └── JackettClient.cs              // Torznab query + result parsing
├── LocalLibrary/
│   ├── TitleYear.cs                  // parses "<title> <year>" from a query or folder name
│   ├── LibraryFolderScanner.cs       // title/year matching core, testable against any path
│   ├── LibraryCacheService.cs        // scans \plex\<category> folders across drives every 12h, caches names
│   ├── PlexLibraryScanner.cs         // matches a query against the cache — no disk I/O per request
│   └── DriveSpaceChecker.cs          // free space per attached drive, for /drive-check
└── QBittorrent/
    ├── QBittorrentOptions.cs
    ├── QBitApiClient.cs              // session-cookie auth, add/stop/list/lookup torrents
    ├── DownloadTrackingStore.cs      // hash → {title, channel, user}, persisted to data/tracked-downloads.json
    ├── ActiveDriveStore.cs           // which drive new downloads route to (/switch-drive), persisted to data/active-drive.json
    ├── StallDetector.cs              // pure "no progress for too long" decision logic
    ├── ActiveDownloadFormatter.cs    // speed/ETA formatting for /active-downloads
    ├── TorrentNameMatcher.cs         // fuzzy name matching when a hash isn't known upfront
    ├── MagnetHash.cs                 // pulls the btih hash out of a magnet URI
    └── CompletionPollerService.cs    // polls qBittorrent, posts completion/stall/error alerts to Discord
└── YtDlp/
    ├── YtDlpOptions.cs                // executable/ffmpeg path overrides, default max height, timeout, max-downloads cap
    ├── YtDlpProgressParser.cs         // pure "[download] NN.N%" / "video X of Y" line parsing
    ├── YtDlpFormatSelector.cs         // pure quality-option -> -f format selector string
    ├── FolderNameSanitizer.cs         // pure "letters/digits/spaces/-/_ only" folder-name cleanup
    ├── YoutubeFolderResolver.cs       // pure path-traversal-safe lookup of an existing Youtube subfolder
    ├── YtDlpResult.cs                 // success/output-paths/error result
    ├── IYtDlpRunner.cs
    └── YtDlpRunner.cs                 // shells out to yt-dlp via ArgumentList, streams progress, kills tree on timeout

DownloadBot.Tests/
├── Unit/                             // TitleYear, MagnetHash, TorrentNameMatcher, LibraryFolderScanner, PlexLibraryScanner, StallDetector, DownloadTrackingStore, ActiveDriveStore, DashboardFormatter, YtDlpProgressParser, YtDlpFormatSelector, FolderNameSanitizer, YoutubeFolderResolver
└── Integration/                      // live Jackett/qBittorrent/Discord/yt-dlp checks, self-skipping
```
