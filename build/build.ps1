param(
    [string]$Version = "1.0.0",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

Write-Host "Building Tally BigQuery Agent version $Version" -ForegroundColor Cyan

function Assert-ExitCode([string]$Step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE"
    }
}

if (-not $SkipTests) {
    dotnet restore .\TallyBigQueryAgent.sln
    Assert-ExitCode "dotnet restore"
    dotnet test .\TallyBigQueryAgent.sln -c Release --no-restore `
        --logger "trx;LogFileName=tests.trx" `
        --results-directory .\TestResults
    Assert-ExitCode "dotnet test"
}

Remove-Item .\publish -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item .\dist -Recurse -Force -ErrorAction SilentlyContinue

$projects = @(
    @{ Name = "service"; Path = ".\src\TallyAgent.Service\TallyAgent.Service.csproj" },
    @{ Name = "cli";     Path = ".\src\TallyAgent.Cli\TallyAgent.Cli.csproj" },
    @{ Name = "manager"; Path = ".\src\TallyAgent.Manager\TallyAgent.Manager.csproj" }
)

foreach ($project in $projects) {
    Write-Host "Publishing $($project.Name)..." -ForegroundColor Yellow
    dotnet publish $project.Path `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        -o ".\publish\$($project.Name)"
    Assert-ExitCode "dotnet publish ($($project.Name))"
}

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)

$ISCC = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $ISCC) {
    throw "Inno Setup 6 was not found. Install it or run: choco install innosetup -y"
}

Write-Host "Compiling installer..." -ForegroundColor Yellow
& $ISCC "/DMyAppVersion=$Version" ".\installer\TallyBigQueryAgent.iss"
Assert-ExitCode "ISCC installer compile"

$Installer = ".\dist\Tally BigQuery Agent Setup.exe"
if (-not (Test-Path $Installer)) {
    throw "Installer was not generated at: $Installer"
}

$hash = (Get-FileHash $Installer -Algorithm SHA256).Hash
$hash | Set-Content ".\dist\Tally BigQuery Agent Setup.exe.sha256"

Write-Host "Build complete" -ForegroundColor Green
Write-Host "Installer: $Installer"
Write-Host "SHA256: $hash"
