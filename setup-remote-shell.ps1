# One-time setup, run on the SERVER in an elevated PowerShell (right-click PowerShell -> Run as
# administrator), after installing Tailscale on it:
#
#   powershell -ExecutionPolicy Bypass -File .\setup-remote-shell.ps1
#
# It turns on Windows' built-in SSH server so you can open a command line on this machine from your
# other PC (ssh <user>@<tailscale-name>), WITHOUT needing the Windows password: you log in with a key
# instead. It will ask you to paste the PUBLIC key (the single line from id_ed25519.pub on the PC you
# connect from - the public half is safe to paste anywhere; never share the file without ".pub").
#
# Safe to re-run. SSH is only allowed from Tailscale addresses (100.64.0.0/10), not the open internet
# or even your local network.

param([string]$PublicKey)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this from an elevated (Run as administrator) PowerShell.' }

if (-not $PublicKey) { $PublicKey = Read-Host 'Paste the public key line (starts with ssh-ed25519)' }
$PublicKey = $PublicKey.Trim()
if ($PublicKey -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-\S+) \S+') {
    throw "That doesn't look like a public key. It should be one line starting with ssh-ed25519 (the contents of id_ed25519.pub)."
}

Write-Host '1/5 Installing the OpenSSH server (skipped if already there)...'
$cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
if ($cap.State -ne 'Installed') { Add-WindowsCapability -Online -Name $cap.Name | Out-Null }

Write-Host '2/5 Starting it and making it start automatically...'
Set-Service sshd -StartupType Automatic
Start-Service sshd

Write-Host '3/5 Making PowerShell the default shell for SSH logins...'
New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell `
    -Value 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -PropertyType String -Force | Out-Null

Write-Host '4/5 Authorizing your key...'
# Windows OpenSSH reads a different file for members of the Administrators group.
$keyFile = 'C:\ProgramData\ssh\administrators_authorized_keys'
$userIsAdmin = $isAdmin # the account running this script is the one you'll log in as
if ($userIsAdmin) {
    if (-not (Test-Path $keyFile)) { New-Item -ItemType File -Path $keyFile -Force | Out-Null }
    $existing = Get-Content $keyFile -ErrorAction SilentlyContinue
    if ($existing -notcontains $PublicKey) { Add-Content -Path $keyFile -Value $PublicKey }
    # sshd ignores the file unless only Administrators and SYSTEM can touch it.
    icacls $keyFile /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null
}

Write-Host '5/5 Restricting SSH to Tailscale addresses only...'
$ruleName = 'OpenSSH-Server-In-TCP'
if (-not (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name $ruleName -DisplayName 'OpenSSH Server (sshd)' -Enabled True `
        -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 | Out-Null
}
Set-NetFirewallRule -Name $ruleName -RemoteAddress '100.64.0.0/10' -Enabled True

Write-Host ''
Write-Host 'Done. On your other PC, connect with:'
$tailscale = Get-Command tailscale -ErrorAction SilentlyContinue
if ($tailscale) {
    Write-Host "  ssh $env:USERNAME@$(& tailscale ip -4 | Select-Object -First 1)"
}
else {
    Write-Host "  ssh $env:USERNAME@<the server's Tailscale name or 100.x address>   (install Tailscale on the server first)"
}
