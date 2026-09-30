param([string]$ServerDirectory = '')
$ErrorActionPreference = 'Stop'
if (-not $ServerDirectory) { $ServerDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\server' }
try { $health = Invoke-RestMethod 'http://localhost:5278/health' -TimeoutSec 2; if ($health.service -eq 'ScanArchive.Secretary') { exit 0 } } catch { }
$exe = Join-Path $ServerDirectory 'ScanArchive.Server.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Publish the service first with scripts/publish.ps1' }
Start-Process -FilePath $exe -WorkingDirectory $ServerDirectory -WindowStyle Hidden
