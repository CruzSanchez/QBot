# Restarts Plex Media Server. Called by the bot's /restart-plex command, but also fine to run by hand
# (or over SSH):  powershell -ExecutionPolicy Bypass -File .\restart-plex.ps1
#
# Plex runs as a normal app in the logged-in desktop session, not as a service, and the bot/SSH run in a
# different non-interactive session - a Plex started from there would land in the wrong session and die
# with it. So it's stopped here and relaunched through a one-time scheduled task with an Interactive
# principal, which puts it back in the desktop session; `explorer.exe <exe>` is the launcher so the task
# finishes immediately and Plex isn't tied to the task's lifetime.
#
# Exit code 0 = Plex answered on its local port after the restart; 1 = it didn't (message on stdout).
# Leaves Plex's desktop app and PlexUpdateService alone.

param(
    [string]$PlexExe = 'C:\Program Files\Plex\Plex Media Server\Plex Media Server.exe',
    [int]$WaitSeconds = 90
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$taskName = 'PlexRestartOnce'

if (-not (Test-Path $PlexExe)) { Write-Output "Plex not found at $PlexExe"; exit 1 }

$names = 'Plex Media Server', 'PlexScriptHost', 'Plex Tuner Service', 'Plex Transcoder', 'Plex DLNA Server'
Get-Process -Name $names -ErrorAction SilentlyContinue | Stop-Process -Force

# Wait (up to 15s) for them to actually be gone before relaunching.
for ($i = 0; $i -lt 15 -and (Get-Process -Name $names -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Seconds 1 }

try {
    $action = New-ScheduledTaskAction -Execute 'explorer.exe' -Argument ('"' + $PlexExe + '"')
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName

    $up = $false
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while (-not $up -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        try { $up = (Invoke-WebRequest -Uri 'http://localhost:32400/identity' -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { }
    }
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
}

if ($up) { Write-Output 'Plex Media Server restarted and is answering.'; exit 0 }
Write-Output "Plex was relaunched but didn't answer on port 32400 within $WaitSeconds seconds."
exit 1
