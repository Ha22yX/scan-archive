# Run in an elevated PowerShell only if LAN access is needed.
$ErrorActionPreference = 'Stop'
if (-not (Get-NetFirewallRule -Name 'ScanArchiveSecretary-LAN' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name 'ScanArchiveSecretary-LAN' -DisplayName 'Scan Archive secretary (private LAN)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5278 -Profile Private -RemoteAddress LocalSubnet | Out-Null
}
Write-Output 'Private local subnet access is enabled on TCP 5278.'
