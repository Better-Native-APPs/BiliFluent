[CmdletBinding()]
param(
    [string]$Version,
    [string]$CertificateThumbprint = '0460A4224C301235C30B6D89287AFA7F2ACE2A21',
    [string]$OutputDirectory
)

# Packs a built MSIX into the self-contained local-release delivery folder + ZIP.
# Run after scripts/Build-Release.ps1. The tracked release-artifacts.json is used as a template only.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/release' }
if (-not $Version) {
    [xml]$manifest = Get-Content (Join-Path $root 'src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Raw
    $Version = $manifest.Package.Identity.Version
}

$dl = Join-Path $OutputDirectory "BiliCopilot-LocalRelease-$Version"
if (Test-Path $dl) { Remove-Item $dl -Recurse -Force }
$payload = Join-Path $dl 'payload'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
$msixName = "BiliCopilot_${Version}_x64.msix"
Copy-Item (Join-Path $OutputDirectory $msixName) (Join-Path $payload $msixName)
$cert = Get-Item "Cert:/CurrentUser/My/$CertificateThumbprint"
$cerPath = Join-Path $payload 'BiliCopilot.LocalRelease.cer'
[IO.File]::WriteAllBytes($cerPath, $cert.RawData)
foreach ($s in 'Install.ps1', 'Trust-Certificate.ps1') { Copy-Item (Join-Path $PSScriptRoot $s) (Join-Path $dl $s) }

function Get-Sha([string]$p) { (Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant() }
$m = Get-Content (Join-Path $PSScriptRoot 'release-artifacts.json') -Raw | ConvertFrom-Json
$m.package.relativePath = "payload/$msixName"
$m.package.sha256 = Get-Sha (Join-Path $payload $msixName)
$m.package.version = $Version
$m.package.packageFullName = "BiliCopilot.LocalRelease_${Version}_x64__p8tmrechhd89c"
$cerHash = Get-Sha $cerPath
$m.certificate.sha256 = $cerHash
$m.certificate.derSha256 = $cerHash
$m.scripts.installSha256 = Get-Sha (Join-Path $dl 'Install.ps1')
$m.scripts.trustSha256 = Get-Sha (Join-Path $dl 'Trust-Certificate.ps1')
$m | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $dl 'release-artifacts.json') -Encoding UTF8

$zip = "$dl.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dl '*') -DestinationPath $zip
Write-Host "DELIVERY $dl"
Write-Host "ZIP $zip"
Write-Host ("MANIFEST_SHA256 " + (Get-Sha (Join-Path $dl 'release-artifacts.json')))
