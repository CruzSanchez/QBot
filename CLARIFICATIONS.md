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

- [ ] **Save paths per drive, per category** — `QBittorrent:SavePaths` in
      `appsettings.json`. This is what the bot passes to qBittorrent when adding a
      torrent directly, so it lands in the right folder without needing an RSS
      Auto Downloading Rule. As of 2026-09-26 it's nested by drive letter first,
      since D/E/F/G all mirror the same folder layout:
      ```json
      "DefaultDrive": "G",
      "SavePaths": {
        "G": {
          "movie": "G:\\plex\\Movies",
          "tv": "G:\\plex\\TV Shows",
          "kids-movie": "G:\\plex\\Kids Movies",
          "kids-tv": "G:\\plex\\Kids TV Shows",
          "music": "G:\\plex\\Music"
        },
        "D": { ... same keys, D: paths ... },
        "E": { ... same keys, E: paths ... },
        "F": { ... same keys, F: paths ... }
      }
      ```
      I only ever confirmed `G`'s paths from a screenshot; **D/E/F are assumed to
      mirror it exactly (just the drive letter swapped) per what you described** —
      please verify that's actually true on the server, especially the folder
      names for `tv`/`kids-movie`/`kids-tv`/`music` on each drive.
- [ ] **`/switch-drive`** — new command, changes `QBittorrentOptions.DefaultDrive`'s
      *runtime* equivalent (persisted to `data/active-drive.json`, gitignored) —
      i.e. which key in `SavePaths` above new `/download` adds route to. Doesn't
      touch anything already downloading, and doesn't move existing files. This is
      manual switching only — not automatic free-space-based routing. Say the word
      if you want it to pick a drive automatically (e.g. whichever has the most
      free space) instead of requiring someone to run `/switch-drive` — that's a
      real feature to build, not a config tweak.

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

## Library duplicate-check now caches, refreshed every 12h (2026-09-26)

- `/download`'s "already have this?" check used to scan the filesystem on every
  single request, which got slow on a large library (Music especially). It now
  reads from an in-memory snapshot (`LibraryCacheService`) that's rebuilt every
  12 hours (and once immediately at startup).
- **Tradeoff**: something added to your Plex library in the last 12 hours won't
  show up in the "already have this?" warning until the next refresh. This only
  affects that soft warning — it never blocks or delays an actual download.
- No config needed. If you want a way to force an immediate refresh (e.g. a
  `/refresh-library` command) instead of waiting up to 12h, say so — not built,
  but straightforward to add.

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

## yt-dlp video downloads via /download-yt (2026-09-26)

- [ ] **Verify the exact `yt-dlp.exe`/`ffmpeg.exe` filenames under
      `C:\Users\johnb\Desktop\repos`** — config currently assumes
      `yt-dlp.exe` sits directly in that folder and points
      `YtDlp:FfmpegLocation` at the folder itself:
      ```json
      "YtDlp": {
        "ExecutablePath": "C:\\Users\\johnb\\Desktop\\repos\\yt-dlp.exe",
        "FfmpegLocation": "C:\\Users\\johnb\\Desktop\\repos"
      }
      ```
      If either binary is actually nested deeper (e.g. inside a
      version-named subfolder, or ffmpeg's own `bin\` folder from how its
      zip extracts), correct these two values. `/download-yt` will report
      "Could not start yt-dlp..." if `ExecutablePath` is wrong; a wrong
      `FfmpegLocation` will more likely show up as a download that
      completes the raw stream(s) but fails to merge into one file.
- The `"youtube"` key lives under the existing `QBittorrent:SavePaths`
  structure (one added per drive: `G`/`D`/`E`/`F`, all `\plex\Youtube`) so
  `/switch-drive` applies to it automatically with zero extra code — this is
  a minor naming mismatch (it's not a qBittorrent path) I accepted rather
  than building a second, parallel save-path config section for one content
  type. Say so if you'd rather it live elsewhere.
- **Playlists are supported**, capped at `YtDlp:MaxDownloadsPerInvocation`
  (default 100, raised 2026-09-27 from 25 after a real 80-video playlist
  hit the old cap) via yt-dlp's own `--max-downloads`, so a huge/accidental
  playlist link can't run unbounded — anything past the cap is just not
  downloaded, no error. `/download-yt`'s `maxdownloads` option (1–500)
  overrides this per-download without changing the global default.
  Combined with the download archive above: re-running the same playlist
  URL after hitting the cap skips what's already downloaded and continues
  with the next batch, rather than starting over.
- **A private/deleted/age-restricted video partway through a playlist no
  longer fails the whole download** (2026-09-27) — `--ignore-errors` skips
  it and keeps going; the result is reported as a success listing whatever
  did download, plus a note on how many were skipped. Only actually fails
  if *nothing* in the playlist could be downloaded.
- **`YtDlp:TimeoutMinutes`** (default 30) is an *inactivity* limit
  (changed 2026-10-03): yt-dlp is only killed if it prints nothing for that
  long, so a big playlist that keeps making progress never times out. It
  used to be a hard total-time cap, which killed a healthy 100-video
  playlist run at 30 minutes. If it does go silent, the failure message says
  how many files had finished; re-running the same URL continues from there.
- **A second `/download-yt` queues** behind one already in progress (shown
  as "⏳ Queued" in its embed) rather than running in parallel or being
  rejected — this server also runs qBittorrent and Plex, and two
  unthrottled yt-dlp/ffmpeg processes at once would compete for the same
  disk/NIC/CPU.
- Downloads are **disk-only** — saved to the active drive's Youtube folder,
  never posted back as a Discord attachment (Discord's upload size limits
  make that impractical for anything beyond a very short clip).
- **`quality`** (2026-09-27) picks a max resolution per download — 1080p
  (default), 4K, 720p, or best available uncapped. Defaults to
  `YtDlp:DefaultMaxHeight` (1080) when left blank. Always merges into mp4
  (`YtDlp:MergeOutputFormat`) — no audio-only option on the command itself.
- **Folder names are restricted to letters, digits, spaces, `-`, and `_`**
  (2026-09-27) — anything else (a video title with a colon/comma/etc., or a
  user typing one into `createnewfolder`/`/rename-folder`) gets automatically
  replaced with a space rather than rejected. This applies to: the
  default per-video folder name (via yt-dlp's own `--replace-in-metadata`
  on the title), `addtoexistingfolder`/`createnewfolder`, and
  `/rename-folder`'s new name. `/rename-folder` tells you when it had to
  clean up what you typed; `/download-yt` doesn't currently surface that
  same notice for `createnewfolder` — say so if you want it to.
- **`/rename-folder`** — renames a folder under the *active drive's*
  Youtube folder only (not other drives). `folder` autocompletes against
  real subfolders there; anything that doesn't resolve to an actual
  existing folder (including a `..\` traversal attempt) is rejected.

## yt-dlp: cookies, download archive, SponsorBlock, retries (2026-09-27)

- [ ] **Cookies (`YtDlp:CookiesFilePath`, blank by default)** — needed for
      age-restricted, members-only, or private videos (the "Sign in to
      confirm your age" errors from an earlier `/download-yt` run). This is
      a manual, one-time setup, not something the bot can do itself:
      1. Log into YouTube in a real browser (ideally a throwaway/dedicated
         account, not your main one — this file is effectively a login
         credential and should be treated that way).
      2. Export cookies in Netscape format using a browser extension (e.g.
         "Get cookies.txt LOCALLY") to a file on the server, e.g.
         `C:\Users\johnb\Desktop\repos\cookies.txt`.
      3. Set `YtDlp:CookiesFilePath` to that path.
      4. Keep this file out of the repo — it already lives outside
         `DownloadBot/` here, but double-check wherever you actually put
         it isn't tracked by git.
      Cookies expire/rotate periodically — if age-restricted downloads
      start failing again later, re-export a fresh cookies.txt.
- **Download archive (`YtDlp:DownloadArchivePath`, default
  `data/yt-dlp-archive.txt`)** — every downloaded video ID gets logged
  here permanently; re-running the same channel/playlist URL later only
  grabs videos not already in the archive instead of re-downloading
  everything. Gitignored along with the rest of `data/`. Delete the file
  (or blank the setting) to reset it.
- **SponsorBlock (`YtDlp:SponsorBlockRemoveCategories`, default
  `"sponsor"`)** — segments in that category are cut from the downloaded
  file using SponsorBlock's community-maintained database. Comma-separate
  more categories (e.g. `"sponsor,selfpromo,interaction"`) or blank it to
  disable entirely. If SponsorBlock's API is unreachable for a given
  video, that video just downloads without any segments removed — it
  doesn't fail the download.
- **Retries (`YtDlp:Retries`, default `1`)** — applied to both
  `--retries`/`--fragment-retries`, i.e. one extra attempt on a flaky
  connection before it's reported as a real failure. Deliberately not
  yt-dlp's own default of 10 (or "infinite") — raise it if downloads are
  failing on transient network issues more than expected.

## /download-yt now posts a separate status message (2026-09-27)

Root cause found for progress appearing "stuck," and for a failure that
never made it back to Discord ("Invalid Webhook Token"/"Interaction token
no longer valid" in the logs): Discord invalidates an interaction's own
webhook token after a while, so every `ModifyOriginalResponseAsync` call
used for the live progress updates could silently fail once that happened
— previously logged at Debug level, so it was invisible, and the embed
just stayed on whatever its last successfully-applied state was (often
still 0%) until the final update either landed in time or also failed.

Fix: `/download-yt` now gives a quick **private** acknowledgment on the
command itself, then posts a normal, bot-owned **public** message for all
progress updates and the final result — that message has no expiration and
is edited via the regular REST API, not the interaction's webhook. Any
future update failure is now logged at Warning, not Debug, so it won't go
unnoticed again.

## Error-triggered log upload (2026-09-27)

Whenever anything anywhere in the app logs an Error (or worse), the bot
waits 2 minutes — a debounce window, so a burst of related errors (e.g. one
root failure causing several follow-on error logs) results in a single
upload, not one per error — then uploads that day's rolling log file
(`logs/downloadbot-YYYYMMDD.log`) to **`Discord:StatusChannelId`** (the same
channel connect/disconnect notices already go to — no new config needed).
- [ ] If you'd rather this go to a *different* channel than
      `StatusChannelId`, say so — currently it deliberately reuses that
      config value rather than adding a duplicate one, since you gave the
      same channel ID for this that's already configured there.
- Only one upload timer runs at a time; more errors arriving while one's
  already pending don't restart or stack additional timers/uploads.
- If the channel can't be resolved, or the log file isn't found for some
  reason, it logs a warning and gives up silently rather than retrying
  indefinitely — the next Error anywhere will start a fresh attempt anyway.


## /delete — Mods-only folder deletion (2026-10-03)

`/delete search:<text>` searches every Plex category folder on every drive
(`<drive>\plex\<category>\<folder>`, all ready drives except C:, live — not the
12h library cache) for folders whose name contains the text (case-insensitive,
up to 25), shown in a picker with the drive and category beside each name.
Picking one asks for a final "Delete forever" confirmation; that **permanently
deletes the folder and everything in it**. A Cancel button is on the picker and
on the confirmation.
- Restricted to the role in `Discord:ModsRoleId` (the Mods role,
  `1551025784460681278`), re-checked on the command, the pick, and the confirm.
  If `ModsRoleId` is unset, nobody can use it.
- Only ever matches folders exactly one level inside a category — never a
  category folder itself (e.g. all of `Movies`), never files, never anything
  deeper. A final shape check (`<drive>\plex\<category>\<item>`, not on C:)
  runs again right before the delete.
- Every delete (or failed attempt) is also announced publicly in the channel with who did it and the full path; the picker and confirm stay private to the mod. It is logged at Warning too.
  Deleting a folder that qBittorrent, yt-dlp, or Plex has open can fail; the
  error is shown instead of the folder being half-removed silently.
- The torrent itself isn't touched: if the folder came from a download still
  listed in qBittorrent, remove that with `/cancel` too or it may recreate files.

## /move — Mods-only folder moves (2026-10-06)

`/move search:<text>` works like `/delete` (same Mods role check, same live,
punctuation-tolerant search across every `<drive>\plex\<category>\<folder>`),
with one extra step: after picking the folder you pick its **destination** —
any other `<drive>\plex\<category>` — then confirm with "Move". Cancel is
available on every step; the private picker/confirm stay with the mod and the
result (or failure) is announced publicly with who did it and both paths.
- Destinations never include the folder's current category, nor any category
  that already has a folder with the same name — a move never merges into or
  overwrites something. (If every other category has a clash, it says so.)
- Same drive = an instant rename. Different drive = `robocopy /MOVE` (built
  into Windows), which can take a long time for a big folder. The private
  message may stop updating after Discord's 15-minute interaction window, but
  the public notice still posts (falls back to a plain channel message).
- If a cross-drive move fails partway, files already copied are already gone
  from the source (that's how `/MOVE` works), so check both locations.
- Same safety rails as `/delete`: only exactly `<drive>\plex\<category>\<item>`
  can be moved, only into exactly `<drive>\plex\<category>`, never on C:.
- Like `/delete`, it doesn't touch qBittorrent — moving a folder that's still
  an active torrent will make qBittorrent report missing files.

## Scheduled drive-space report (2026-10-09)

At **8:00 AM, 12:00 PM and 10:00 PM Central** the bot posts the same embed
`/drive-check` shows (all drives except C:, with a "Checked …" footer) to
`Discord:DriveCheckChannelId` (set to `1547823750517489696`). Leave it unset
to turn the report off.
- Times are Central wall-clock, so they follow daylight saving (the same
  "CST" label the bot's other timestamps use) rather than a fixed UTC-6.
- If the bot is down or disconnected at a slot, that report is just skipped —
  it doesn't catch up later. A failed post logs a Warning, not an Error, so it
  doesn't trigger the error-log upload.
- The next run time is logged at startup and after each post.

## A network blip no longer takes the whole bot down (2026-10-10)

On 2026-10-10 at ~06:12 the server briefly couldn't resolve discord.com. A Discord
send failed inside the dashboard refresh and then inside a completion alert;
neither was caught, and .NET stops the *entire host* when any background
service throws — so the bot shut down at 06:13 and stayed down (nothing
restarts it; the scheduled drive reports and everything else stopped too).
- The dashboard refresh and the download poller now catch per-tick failures,
  log a Warning, and try again on the next tick.
- A download that finishes or fails stays tracked until its Discord alert has
  actually been delivered, so an outage delays the ping instead of losing it.
- The dashboard only reposts itself when its message was really deleted — not
  when a lookup merely failed — so an outage can't create duplicate dashboards.
- Still not covered: if the bot process dies for some other reason, nothing
  relaunches it until the next deploy or manual start. Say so if you want the
  startup scheduled task set to restart on failure.

## Restart-on-crash: a loop in run-bot.bat (2026-10-10)

Task Scheduler's own "restart on failure" setting did **not** fire when the bot
process was killed in Task Manager on the server, so the restart now lives in
`run-bot.bat` itself and doesn't depend on that setting at all:
- If `dotnet run` exits with a **non-zero** code (a crash, the host stopping on
  an unhandled exception, the process being killed), the script waits 15
  seconds and starts it again, up to 50 restarts per launch (so a bot that can't
  start at all — bad config — doesn't loop forever).
- A **clean stop** (exit code 0 — Ctrl+C, or the deploy's `POST /shutdown`) is
  *not* restarted, so deploys behave exactly as before. If a deploy ever has to
  force-kill a stuck bot, the workflow's `schtasks /end` removes the loop
  before its 15-second delay is up, so it can't respawn mid-build.
- Applies however it's started (double-click, scheduled task). Only the final
  `pause` is skipped when run with `unattended`, as the scheduled task does.
- Tested here with a stand-in `dotnet.exe`: two crashes -> two restarts -> clean
  stop ends the script; an immediate clean stop is not restarted.
- To test on the server: kill the **`DownloadBot.exe`** process in Task Manager
  (not the parent `dotnet.exe`) — it should be back in ~15-20 seconds. A new
  loop only applies to a *new* run of the task, so after this deploys, the
  deploy's own restart picks it up automatically.
- `install-startup-task.bat` is still worth running once: it removes Task
  Scheduler's default 3-day run limit and passes `unattended`. Its
  restart-on-failure setting is harmless but redundant now.
