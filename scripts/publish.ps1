param([string]$Output = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $Output) { $Output = Join-Path $projectRoot 'dist' }
dotnet publish (Join-Path $projectRoot 'ScanArchive') -c Release -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
dotnet publish (Join-Path $projectRoot 'ScanArchive.Server') -c Release -r win-x64 --self-contained true -o (Join-Path $Output 'server')
if ($LASTEXITCODE -ne 0) { throw 'Secretary server publish failed' }
Write-Output "Published desktop and web service to $Output"
