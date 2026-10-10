# Sets up the "DownloadBot" scheduled task that runs run-bot.bat:
#   - starts at logon of the current user, like the original install-startup-task.bat
#   - RESTARTS ON FAILURE: if the bot exits with a non-zero code (a crash, or the host stopping on an
#     unhandled exception), Task Scheduler relaunches it after 1 minute, up to 999 times. A clean stop
#     (exit code 0 - Ctrl+C, or the deploy workflow's POST /shutdown) is not restarted.
#   - NO TIME LIMIT: tasks created by `schtasks /create` default to "stop after 3 days", which would
#     kill the bot every 72 hours with no restart.
#
# No Windows password is needed in the normal case:
#   * If the task already exists, it's updated IN PLACE - the credentials Task Scheduler already stored
#     for it are left alone.
#   * If it doesn't exist, it's created with logon type S4U: runs whether or not you're logged in,
#     without a stored password (limit: no access to network shares as that user - the bot only talks
#     to localhost and the internet, so that doesn't matter).
#   * -WithPassword: prompts for the password and registers it the old way ("run whether logged on or
#     not" with a stored password) - only if you need that.
#
# Run from an elevated prompt (right-click install-startup-task.bat -> Run as administrator).

param([switch]$WithPassword)

$ErrorActionPreference = 'Stop'

$taskName = 'DownloadBot'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$script = Join-Path $root 'run-bot.bat'
$user = "$env:USERDOMAIN\$env:USERNAME"

$action = New-ScheduledTaskAction -Execute $script -Argument 'unattended' -WorkingDirectory $root
$settings = New-ScheduledTaskSettingsSet `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue

if ($existing -and -not $WithPassword) {
    $logonBefore = $existing.Principal.LogonType
    Set-ScheduledTask -TaskName $taskName -Action $action -Settings $settings | Out-Null

    $logonAfter = (Get-ScheduledTask -TaskName $taskName).Principal.LogonType
    if ($logonAfter -ne $logonBefore) {
        Write-Warning "The task's logon type changed ($logonBefore -> $logonAfter) during the update. Re-run with -WithPassword if it no longer starts when you're logged out."
    }
    Write-Host "Updated existing '$taskName' task in place (credentials untouched)."
}
else {
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user

    if ($WithPassword) {
        $securePassword = Read-Host "Windows password for $user" -AsSecureString
        $password = [System.Net.NetworkCredential]::new('', $securePassword).Password
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
            -User $user -Password $password -RunLevel Highest -Force | Out-Null
        Write-Host "Registered '$taskName' for $user with a stored password."
    }
    else {
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType S4U -RunLevel Highest
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
            -Principal $principal -Force | Out-Null
        Write-Host "Registered '$taskName' for $user (S4U logon: runs when logged out, no password stored)."
    }
}

Write-Host ''
Write-Host "Done. It restarts itself 1 minute after a crash, up to 999 times, with no run-time limit."
Write-Host "Start it now:     schtasks /run /tn $taskName"
Write-Host 'Test the restart: end the DownloadBot process in Task Manager; it should relaunch within ~1 minute.'
