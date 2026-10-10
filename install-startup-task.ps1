# Registers (or re-registers) the "DownloadBot" scheduled task that runs run-bot.bat:
#   - starts at logon of the current user and runs whether or not they're logged in (needed so the
#     deploy workflow's `schtasks /run` works), like the original install-startup-task.bat
#   - RESTARTS ON FAILURE: if the bot exits with a non-zero code (a crash, or the host stopping on an
#     unhandled exception), Task Scheduler relaunches it after 1 minute, up to 999 times. A clean stop
#     (exit code 0 — Ctrl+C, or the deploy workflow's POST /shutdown) is not restarted.
#   - NO TIME LIMIT: tasks created by `schtasks /create` default to "stop after 3 days", which would
#     kill the bot every 72 hours with no restart.
#
# Safe to re-run: it replaces the existing task. Asks for your Windows password (stored by Task
# Scheduler, never written anywhere else) so the task can run while you're logged out.
# Run from an elevated prompt (right-click install-startup-task.bat -> Run as administrator).

$ErrorActionPreference = 'Stop'

$taskName = 'DownloadBot'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$script = Join-Path $root 'run-bot.bat'
$user = "$env:USERDOMAIN\$env:USERNAME"

Write-Host "Registering scheduled task '$taskName' for $user"
Write-Host "  Runs:     $script unattended"
Write-Host '  Restarts: 1 minute after a crash, up to 999 times; no run-time limit'
Write-Host ''

$securePassword = Read-Host "Windows password for $user" -AsSecureString
$password = [System.Net.NetworkCredential]::new('', $securePassword).Password

$action = New-ScheduledTaskAction -Execute $script -Argument 'unattended' -WorkingDirectory $root
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$settings = New-ScheduledTaskSettingsSet `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
    -User $user -Password $password -RunLevel Highest -Force | Out-Null

Write-Host ''
Write-Host "Done. '$taskName' will start at logon and restart itself if it crashes."
Write-Host "Start it now:  schtasks /run /tn $taskName"
Write-Host "Test the restart: end the DownloadBot process in Task Manager; it should relaunch within ~1 minute."
