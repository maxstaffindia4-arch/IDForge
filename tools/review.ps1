# IDForge phase-review launcher.
# Builds a self-contained Release copy, prints branch/commit, launches the app.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
$hash = (& git rev-parse --short HEAD 2>$null)
Write-Host "Branch: $branch  Commit: $hash" -ForegroundColor Cyan

Write-Host "Publishing..." -ForegroundColor Cyan
& dotnet publish src\IDForge -c Release -r win-x64 --self-contained true -o publish\idforge
if ($LASTEXITCODE -ne 0) {
    Write-Host "PUBLISH FAILED (exit $LASTEXITCODE) - app not launched." -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $root "publish\idforge\IDForge.exe"
Write-Host "Launching $exe" -ForegroundColor Green
Start-Process $exe
