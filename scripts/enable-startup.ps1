param([switch]$Disable)
$ErrorActionPreference = 'Stop'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($Disable) { Remove-ItemProperty -LiteralPath $key -Name 'ScanArchiveSecretary' -ErrorAction SilentlyContinue; exit 0 }
$script = Join-Path $PSScriptRoot 'start-secretary.ps1'
$command = 'powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $script + '"'
New-ItemProperty -LiteralPath $key -Name 'ScanArchiveSecretary' -Value $command -PropertyType String -Force | Out-Null
Write-Output 'The secretary will run in the background at Windows sign-in.'
