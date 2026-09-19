# Clarifications needed

Things I couldn't decide on my own. Everything below has a placeholder in
place so the app builds and runs; fill in real values and re-check the box.

## Credentials (required to actually run anything)

- [ ] **Discord bot token** — create an app at https://discord.com/developers/applications,
      add a Bot, enable it, copy the token, then:
      ```
      dotnet user-secrets set "Discord:Token" "your-token" --project DownloadBot
      ```
      Also invite the bot to your server with the `applications.commands` and `bot`
      scopes (Send Messages, Use Slash Commands permissions).

- [ ] **Discord dev guild ID** (optional but recommended while developing — global
      slash command registration can take up to an hour to propagate; a guild-scoped
      one is instant). Right-click your test server in Discord (Developer Mode on) →
      Copy Server ID, then:
      ```
      dotnet user-secrets set "Discord:DevGuildId" "123456789012345678" --project DownloadBot
      ```

- [ ] **Jackett API key** — install/run Jackett locally, add your indexers, copy the
      API key from the Jackett dashboard, then:
      ```
      dotnet user-secrets set "Jackett:ApiKey" "your-key" --project DownloadBot
      ```
      Also confirm `Jackett:BaseUrl` in `appsettings.json` (default `http://localhost:9117`)
      matches where Jackett is actually running.

- [ ] **qBittorrent WebUI credentials** — enable the WebUI in qBittorrent
      (Tools → Options → Web UI), then:
      ```
      dotnet user-secrets set "QBittorrent:Username" "your-username" --project DownloadBot
      dotnet user-secrets set "QBittorrent:Password" "your-password" --project DownloadBot
      ```
      Confirm `QBittorrent:BaseUrl` matches the WebUI port (default `http://localhost:8080`).

## Design decisions I made without checking — flag if you want them different

- **Completion detection** treats these qBittorrent states as "done":
  `uploading`, `stalledUP`, `pausedUP`, `queuedUP`, `forcedUP`, `checkingUP`,
  or progress `>= 1.0`. This means "finished downloading, now seeding" — not
  "fully seeded per your seeding goal." If you want a different definition
  (e.g. only notify once it drops out of the qBittorrent UI, or only on a
  specific category), say so.
- **Completion notification only works for magnet links.** The info hash is
  parsed directly out of the magnet URI (`btih:...`). If Jackett returns a
  `.torrent` file URL instead of a magnet for some indexer, that item still
  gets added to the RSS feed and downloaded fine, but won't be tracked for a
  completion ping (no hash available without downloading and parsing the
  .torrent file first — not implemented).
- **Feed item expiry is 10 minutes** ([Program.cs](DownloadBot/Program.cs)).
  If qBittorrent's RSS Reader polls less often than that, items could
  disappear before being picked up. Adjust if your RSS Reader's refresh
  interval is longer.
- **qBittorrent RSS Reader subscription URL and Auto Downloading Rule are
  manual, one-time setup** (per the original plan) — I haven't automated
  this since it's a one-time click-through in the qBittorrent UI:
  1. Tools → RSS → Downloader (or RSS panel) → add feed URL `http://localhost:5151/feed`
  2. RSS → Auto Downloading Rules → new rule, match-all (or match a marker
     you embed in titles), set save path/category per movie vs. TV.
- **No persistence** — the pending-item queue and completion-tracking store
  are in-memory (`ConcurrentDictionary`/`ConcurrentQueue`), so a bot restart
  drops anything mid-flight. The original plan flagged SQLite as an optional
  upgrade; I left it as MVP in-memory since nothing in the request specified
  durability requirements. Say the word if you want SQLite added.
