# Clarifications needed

Things I couldn't decide on my own. Everything below has a placeholder in
place so the app builds and runs; fill in real values and re-check the box.

## Credentials (required to actually run anything)

- [ ] **Discord bot token** — from the Developer Portal, then:
      ```
      dotnet user-secrets set "Discord:Token" "your-token" --project DownloadBot
      ```

- [ ] **Discord dev guild ID** (recommended — instant command registration instead
      of up to an hour globally):
      ```
      dotnet user-secrets set "Discord:DevGuildId" "123456789012345678" --project DownloadBot
      ```

- [ ] **Jackett API key**:
      ```
      dotnet user-secrets set "Jackett:ApiKey" "your-key" --project DownloadBot
      ```
      Also confirm `Jackett:BaseUrl` in `appsettings.json` matches where Jackett runs.

- [ ] **qBittorrent WebUI credentials** (Tools → Options → Web UI to enable it):
      ```
      dotnet user-secrets set "QBittorrent:Username" "your-username" --project DownloadBot
      dotnet user-secrets set "QBittorrent:Password" "your-password" --project DownloadBot
      ```
      Confirm `QBittorrent:BaseUrl` matches the WebUI port.

- [ ] **Save paths per category** — `QBittorrent:SavePaths` in `appsettings.json`.
      This is what the bot passes to qBittorrent when adding a torrent directly, so
      it lands in the right folder without needing an RSS Auto Downloading Rule.
      I only confirmed one of these from a screenshot (`movie` → `G:\plex\Movies`);
      **please verify/correct the other three** (`tv`, `kids-movie`, `kids-tv`) —
      I guessed at their folder names from the pattern, not confirmed:
      ```json
      "SavePaths": {
        "movie": "G:\\plex\\Movies",
        "tv": "G:\\plex\\TV Shows",
        "kids-movie": "G:\\plex\\Kids Movies",
        "kids-tv": "G:\\plex\\Kids TV Shows"
      }
      ```

## Architecture change (2026-09-20)

The bot used to write picks into an in-memory RSS feed and rely on qBittorrent's
RSS Reader polling it, matched by an Auto Downloading Rule keyed on a title marker.
That indirection turned out to race qBittorrent's poll interval and its rules in
ways that silently dropped items and produced false "never picked up" alerts.

The bot now calls qBittorrent's `/api/v2/torrents/add` directly and polls for
confirmation within ~10 seconds. Consequences:
- **The RSS feed, `/feed` endpoint, and the whole `Feed/` folder are gone.**
- **qBittorrent's RSS subscription and Auto Downloading Rules are no longer used**
  by the bot. You can leave them in place (harmless — the bot's own picks never go
  through them) or remove them; that's your call, not required either way.
- Category routing (movie vs. TV vs. kids) now happens via `QBittorrent:SavePaths`
  above instead of the rules' "Must Contain" title-marker matching.

## Design decisions I made without checking — flag if you want them different

- **Completion detection** treats these qBittorrent states as "done":
  `uploading`, `stalledUP`, `pausedUP`, `queuedUP`, `forcedUP`, `checkingUP`,
  or progress `>= 1.0`.
- **Add confirmation polls for up to 10 seconds** (1s intervals) after calling
  qBittorrent's add API before declaring it failed. If your qBittorrent instance
  is unusually slow to register a new torrent, this could still false-negative —
  say so if 10s isn't enough headroom and I'll raise it.
- **Completion tracking now persists** to `DownloadBot/data/tracked-downloads.json`
  (gitignored, machine-local) — a bot restart no longer drops anything mid-download
  from being tracked for a completion ping. JSON, not SQLite, per your preference
  (zero extra setup: no package, no schema).
- **Stall alert threshold**: `QBittorrent:StallAlertMinutes` (default 20) — if a
  tracked download shows no forward progress for this long, you get pinged once
  (dead tracker / no seeders). It only alerts once per stall episode; if progress
  resumes and then stalls again later, it can alert again.
