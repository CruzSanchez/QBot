# Reads the bot's log on the server over SSH (Tailscale), from this PC. Read-only.
#
#   .\server-logs.ps1                  last 60 meaningful lines of today's log
#   .\server-logs.ps1 -Errors          only warnings / errors / fatals
#   .\server-logs.ps1 -Search jackett  only lines containing that text (case-insensitive)
#   .\server-logs.ps1 -Lines 200       how many lines
#   .\server-logs.ps1 -Date 20261009   another day (yyyyMMdd)
#   .\server-logs.ps1 -Follow          keep streaming new lines (Ctrl+C to stop)
#   .\server-logs.ps1 -Raw             everything: no filtering, no stack-trace trimming, no truncation
#
# By default it hides Discord gateway chatter and stack-trace continuation lines, and truncates very
# long lines, so the output stays readable. Needs the SSH key set up by setup-remote-shell.ps1.

param(
    [int]$Lines = 60,
    [switch]$Errors,
    [string]$Search = '',
    [string]$Date = '',
    [switch]$Follow,
    [switch]$Raw,
    [string]$Server = '100.87.4.91',
    [string]$User = 'johnb',
    [string]$LogDir = 'C:\Users\johnb\Desktop\QBot\DownloadBot\logs'
)

$ErrorActionPreference = 'Stop'

function Quote([string]$s) { "'" + $s.Replace("'", "''") + "'" }
function Bool([bool]$b) { if ($b) { '$true' } else { '$false' } }

# The remote script is a header of values (safely quoted) plus a fixed body, sent as an encoded command
# so nothing gets mangled by quoting on the way through ssh.
$header = @"
`$ProgressPreference = 'SilentlyContinue'
`$logDir = $(Quote $LogDir)
`$date = $(Quote $Date)
`$search = $(Quote $Search)
`$lines = $Lines
`$errorsOnly = $(Bool $Errors.IsPresent)
`$raw = $(Bool $Raw.IsPresent)
`$follow = $(Bool $Follow.IsPresent)
"@

$body = @'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not $date) { $date = Get-Date -Format yyyyMMdd }
$file = Join-Path $logDir ("downloadbot-" + $date + ".log")
if (-not (Test-Path $file)) { Write-Output "No log file: $file"; exit 1 }

$level = if ($errorsOnly) { '\[(WRN|ERR|FTL)\]' } else { '\[(INF|WRN|ERR|FTL)\]' }

function Keep([string]$line) {
    if ($raw) { return $true }
    if ($line -notmatch ('^\d\d:\d\d:\d\d\.\d+ ' + $level)) { return $false }
    if ($line -match 'Gateway     (You.re using|Connecting|Disconnect|Resumed|Connected)') { return $false }
    if ($search -and -not $line.ToLower().Contains($search.ToLower())) { return $false }
    return $true
}
function Show([string]$line) {
    if (-not $raw -and $line.Length -gt 220) { $line.Substring(0, 220) + '...' } else { $line }
}

if ($follow) {
    Get-Content $file -Tail 20 -Wait | Where-Object { Keep $_ } | ForEach-Object { Show $_ }
}
else {
    Get-Content $file | Where-Object { Keep $_ } | Select-Object -Last $lines | ForEach-Object { Show $_ }
}
'@

$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($header + "`n" + $body))

[Console]::OutputEncoding = [Text.Encoding]::UTF8
& ssh -o BatchMode=yes -o ServerAliveInterval=30 "$User@$Server" "powershell -NoProfile -OutputFormat Text -EncodedCommand $encoded"
exit $LASTEXITCODE
