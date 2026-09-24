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

## Live dashboard (2026-09-21)

- **`Discord:DashboardChannelId`** (optional, `null` by default) — set this to a
  channel ID and the bot posts one message there listing everything qBittorrent is
  currently downloading (progress bar, speed, ETA), then edits that same message in
  place every `QBittorrent:PollIntervalSeconds`. Leave it `null` and this feature is
  just off — nothing posts.
  ```
  dotnet user-secrets set "Discord:DashboardChannelId" "123456789012345678" --project DownloadBot
  ```
  Pin the message yourself after the first post if you want it easy to find — the
  bot doesn't pin it automatically. The message id persists to
  `DownloadBot/data/dashboard-message.json` (gitignored) so a restart edits the same
  message instead of posting a new one.
- **Completion/stall/error alerts are now embeds**, color-coded (green/yellow/red),
  and the stall/error ones carry a "Cancel this download" button that jumps straight
  into the same remove/remove+delete/nevermind flow as `/cancel`.
- **`/status`** is a lighter-weight alternative to the dashboard channel — anyone can
  run it to get the same embed, self-refreshing every 5s for about a minute,
  ephemeral (only they see it). No config needed.

## Auto-deploy on push (2026-09-23)

- [ ] **Set up the self-hosted runner** — one-time, done on the server itself
      (not this dev machine), needs Administrator:
      1. On GitHub: repo -> **Settings -> Actions -> Runners -> New self-hosted
         runner**, pick Windows x64. Follow the PowerShell commands GitHub shows
         you there exactly (they include a registration token generated just
         for that request, so copy them fresh rather than reusing old ones).
      2. When it asks how to run the runner, install it **as a service** (the
         setup script offers this) rather than leaving `run.cmd` open in a
         terminal, so it survives reboots the same way the bot's own scheduled
         task does.
      3. Confirm it shows "Idle" under Settings -> Actions -> Runners once done.
- [ ] **Set the `SERVER_REPO_PATH` repository variable** — Settings -> Secrets
      and variables -> Actions -> **Variables** tab -> New repository variable:
      - Name: `SERVER_REPO_PATH`
      - Value: the absolute path to this repo's clone **on the server**
        (e.g. `C:\Users\<you>\Desktop\QbitBotStack`) — whatever `run-bot.bat`
        already sits in there.
- [ ] **Verify** by pushing any small change to `main` and checking the repo's
      **Actions** tab — you should see a "Deploy to server" run pick it up,
      pull, build, and restart the bot automatically (`.github/workflows/deploy.yml`).

- [ ] **DownloadBot scheduled task must be "Run whether user is logged on or
      not"**, not "Run only when user is logged on" (its default when created
      via Task Scheduler's UI or an older `install-startup-task.bat`). With
      the "only when logged on" setting, `schtasks /run` (what the deploy
      workflow uses to restart the bot) reports success but silently does
      nothing if nobody's got an active interactive session at that moment —
      which is exactly when an automated restart is likely to happen. Fix:
      task Properties -> General -> select "Run whether user is logged on or
      not" -> OK -> enter your Windows password when prompted. (Re-running
      `install-startup-task.bat` now sets this correctly for a fresh install.)

**Heads up**: the deploy step runs `git reset --hard origin/main` on the server
so it always exactly matches what's pushed — any local uncommitted changes made
directly on the server get discarded on the next push. If you ever edit files
directly on the server for a quick test, commit/push them (or stash them) before
pushing something else, or they'll be silently wiped on the next deploy.
