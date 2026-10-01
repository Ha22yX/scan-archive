param([string]$ServerDirectory = '')
$ErrorActionPreference = 'Stop'
if (-not $ServerDirectory) { $ServerDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\server' }
Add-Type -AssemblyName System.Net.Http
$health = $null
$healthHandler = New-Object System.Net.Http.HttpClientHandler
$healthHandler.UseProxy = $false
$healthClient = New-Object System.Net.Http.HttpClient($healthHandler)
$healthClient.Timeout = [TimeSpan]::FromSeconds(2)
try { $health = $healthClient.GetStringAsync('http://127.0.0.1:5278/health').GetAwaiter().GetResult() | ConvertFrom-Json }
catch { }
finally { $healthClient.Dispose() }
if ($health) {
    if ($health.service -ne 'ScanArchive.Secretary' -or $health.dataLayout -ne 'user-profile-v1') {
        throw 'An older or different service is using port 5278. Stop the old ScanArchive.Server process and start the updated application. Existing data will be preserved.'
    }
    exit 0
}
$exe = Join-Path $ServerDirectory 'ScanArchive.Server.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Publish the service first with scripts/publish.ps1' }
Start-Process -FilePath $exe -WorkingDirectory $ServerDirectory -WindowStyle Hidden
