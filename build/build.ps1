# ────────────────────────────────────────────────────────────────
#  Tally BigQuery Agent — build script (run on Windows)
#
#  Prereqs:
#    • .NET 8 SDK          https://dotnet.microsoft.com/download/dotnet/8.0
#    • Inno Setup 6        https://jrsoftware.org/isdl.php
#
#  Usage (from repo root, PowerShell):
#    .\build\build.ps1                 # publish + installer
#    .\build\build.ps1 -SkipInstaller  # publish only
# ────────────────────────────────────────────────────────────────
param(
    [switch]$SkipInstaller,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publish = Join-Path $root "publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "==> Publishing service (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish src/TallyAgent.Service -c $Configuration -r win-x64 --self-contained true `
    -o "$publish/service" /p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw "Service publish failed" }

Write-Host "==> Publishing CLI..." -ForegroundColor Cyan
dotnet publish src/TallyAgent.Cli -c $Configuration -r win-x64 --self-contained true `
    -o "$publish/cli"
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

Write-Host "==> Publishing manager (WPF)..." -ForegroundColor Cyan
dotnet publish src/TallyAgent.Manager -c $Configuration -r win-x64 --self-contained true `
    -o "$publish/manager"
if ($LASTEXITCODE -ne 0) { throw "Manager publish failed" }

if (-not $SkipInstaller) {
    $iscc = @(
        "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) { throw "Inno Setup 6 not found — install from https://jrsoftware.org/isdl.php" }

    Write-Host "==> Compiling installer..." -ForegroundColor Cyan
    & $iscc "installer\TallyBigQueryAgent.iss"
    if ($LASTEXITCODE -ne 0) { throw "Installer compile failed" }

    Write-Host "`nDone: dist\Tally BigQuery Agent Setup.exe" -ForegroundColor Green
} else {
    Write-Host "`nDone (publish only): $publish" -ForegroundColor Green
}
