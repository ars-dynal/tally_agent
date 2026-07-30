# ────────────────────────────────────────────────────────────────
#  Authenticode signing — run AFTER build.ps1 when a code-signing
#  certificate is available. Signs the three EXEs and the installer.
#
#  Usage:
#    .\build\sign.ps1 -PfxPath cert.pfx -PfxPassword (Read-Host -AsSecureString)
#    .\build\sign.ps1 -CertThumbprint <thumbprint>     # cert store
# ────────────────────────────────────────────────────────────────
param(
    [string]$PfxPath,
    [SecureString]$PfxPassword,
    [string]$CertThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found — install the Windows 10/11 SDK" }

$targets = @(
    "$root\publish\service\TallyAgent.Service.exe",
    "$root\publish\cli\TallyAgent.Cli.exe",
    "$root\publish\manager\TallyAgent.Manager.exe",
    "$root\dist\Tally BigQuery Agent Setup.exe"
) | Where-Object { Test-Path $_ }

foreach ($t in $targets) {
    Write-Host "Signing $t"
    if ($CertThumbprint) {
        & $signtool.FullName sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 "$t"
    } else {
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword))
        & $signtool.FullName sign /f $PfxPath /p $plain /fd SHA256 /tr $TimestampUrl /td SHA256 "$t"
    }
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $t" }
}
Write-Host "All binaries signed." -ForegroundColor Green
