param(
    [Parameter(Mandatory=$true)]
    [string]$InstallerPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $InstallerPath)) {
    throw "Installer not found: $InstallerPath"
}

$signature = Get-AuthenticodeSignature $InstallerPath
$hash = Get-FileHash $InstallerPath -Algorithm SHA256

[PSCustomObject]@{
    File = (Resolve-Path $InstallerPath).Path
    SizeMB = [math]::Round((Get-Item $InstallerPath).Length / 1MB, 2)
    SHA256 = $hash.Hash
    SignatureStatus = $signature.Status
    Signer = $signature.SignerCertificate.Subject
} | Format-List
