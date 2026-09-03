# KAN-11: ask a running Mahjong Helper to write a capture sidecar.
# Telesto URL ACL is HTTP://LOCALHOST:45678/ — 127.0.0.1 returns HTTP.sys 400 Invalid Hostname.
$ErrorActionPreference = 'Stop'
$uri = 'http://localhost:45678/'
$body = '{"version":1,"id":1,"type":"ExecuteCommand","payload":{"command":"/mj snap"}}'

Write-Host "POST $uri ExecuteCommand /mj snap (Host localhost)"
try {
    Invoke-RestMethod -Method Post -Uri $uri -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 5 | Out-Null
    Write-Host 'Telesto accepted ExecuteCommand.'
}
catch {
    Write-Warning "Telesto POST failed (plugin/game may be wedged): $($_.Exception.Message)"
    Write-Host 'Fallback: write %APPDATA%\MahjongHelper\captures\request_snap and wait for the plugin file-watch.'
    $captures = Join-Path $env:APPDATA 'MahjongHelper\captures'
    New-Item -ItemType Directory -Force -Path $captures | Out-Null
    Set-Content -Path (Join-Path $captures 'request_snap') -Value 'snap' -Encoding ascii
}

$capturesDir = Join-Path $env:APPDATA 'MahjongHelper\captures'
Write-Host "Sidecar JSON (if the plugin is healthy) lands in $capturesDir"
Write-Host 'Print Screen / monitor PNG is separate; never commit PNGs. Plugin keeps the last 10 capture files.'
