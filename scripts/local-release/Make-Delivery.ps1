[CmdletBinding()]
param(
    [string]$Version,
    [string]$CertificateThumbprint = '6727300457DD924DAD363FDD27C5166E0E62CF32',
    [string]$OutputDirectory
)

# Packs a built MSIX into the self-contained local-release delivery folder + ZIP.
# Run after scripts/Build-Release.ps1. The tracked release-artifacts.json is used as a template only.
# The delivery root contains the user-facing one-click installer (<"An Zhuang" BiliFluent>.cmd -> Install-BiliFluent.ps1),
# which runs Install.ps1 -Install with the manifest SHA-256 embedded below.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/release' }
if (-not $Version) {
    [xml]$manifest = Get-Content (Join-Path $root 'src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Raw
    $Version = $manifest.Package.Identity.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Invalid package version: $Version" }

$utf8Bom = New-Object System.Text.UTF8Encoding($true)
function Get-Sha([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Set-PinnedVersion([string]$Path, [string]$Value) {
    # Install.ps1 / Trust-Certificate.ps1 pin the package version in $script:ReleaseFixed; stamp the delivered copy.
    $text = [IO.File]::ReadAllText($Path)
    $pattern = "(?m)^(\s*Version\s*=\s*')[0-9.]+(')"
    if ([regex]::Matches($text, $pattern).Count -ne 1) { throw "Expected exactly one pinned Version line in $Path" }
    [IO.File]::WriteAllText($Path, [regex]::Replace($text, $pattern, ('${1}' + $Value + '${2}')), $utf8Bom)
}

$dl = Join-Path $OutputDirectory "BiliFluent-$Version"
if (Test-Path $dl) { Remove-Item $dl -Recurse -Force }
$payload = Join-Path $dl 'payload'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
$msixName = "BiliFluent_${Version}_x64.msix"
$msixSource = Join-Path $OutputDirectory $msixName
if (-not (Test-Path -LiteralPath $msixSource)) { throw "MSIX not found: $msixSource (run scripts/Build-Release.ps1 first)" }
Copy-Item -LiteralPath $msixSource (Join-Path $payload $msixName)
$cert = Get-Item "Cert:/CurrentUser/My/$CertificateThumbprint"
if ($cert.Subject -cne 'CN=BiliFluent') { throw 'Delivery certificate must have subject CN=BiliFluent.' }
$cerPath = Join-Path $payload 'BiliFluent.cer'
[IO.File]::WriteAllBytes($cerPath, $cert.RawData)
foreach ($s in 'Install.ps1', 'Trust-Certificate.ps1') {
    Copy-Item (Join-Path $PSScriptRoot $s) (Join-Path $dl $s)
    Set-PinnedVersion (Join-Path $dl $s) $Version
}

$m = Get-Content (Join-Path $PSScriptRoot 'release-artifacts.json') -Raw | ConvertFrom-Json
$m.package.relativePath = "payload/$msixName"
$m.package.sha256 = Get-Sha (Join-Path $payload $msixName)
$m.package.version = $Version
$m.package.packageFullName = "BiliFluent_${Version}_x64__7rrwb2e69v8er"
$cerHash = Get-Sha $cerPath
$m.certificate.sha256 = $cerHash
$m.certificate.derSha256 = $cerHash
$m.certificate.thumbprint = $cert.Thumbprint
$m.certificate.subject = $cert.Subject
$m.certificate.notBeforeUtc = $cert.NotBefore.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
$m.certificate.notAfterUtc = $cert.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
$m.scripts.installSha256 = Get-Sha (Join-Path $dl 'Install.ps1')
$m.scripts.trustSha256 = Get-Sha (Join-Path $dl 'Trust-Certificate.ps1')
$m | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $dl 'release-artifacts.json') -Encoding UTF8
$manifestSha = Get-Sha (Join-Path $dl 'release-artifacts.json')

# One-click installer: wrapper gets the manifest digest embedded; .cmd is copied byte-for-byte (ASCII).
$wrapperText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Install-BiliFluent.ps1'))
if (-not $wrapperText.Contains('__MANIFEST_SHA256__')) { throw 'Install-BiliFluent.ps1 template lacks the __MANIFEST_SHA256__ placeholder.' }
[IO.File]::WriteAllText((Join-Path $dl 'Install-BiliFluent.ps1'), $wrapperText.Replace('__MANIFEST_SHA256__', $manifestSha), $utf8Bom)
$cmdName = [string]::Concat([char]0x5B89, [char]0x88C5, ' BiliFluent.cmd')  # Chinese "install" + " BiliFluent.cmd"; built from code points to keep this file ASCII
Copy-Item -LiteralPath (Join-Path $PSScriptRoot $cmdName) -Destination (Join-Path $dl $cmdName)

$zip = "$dl.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Build the ZIP directly: Windows PowerShell 5.1 Compress-Archive writes '\' entry separators, which other unzip tools mishandle.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipStream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create, $false, [Text.Encoding]::UTF8)
    try {
        $dlFull = (Resolve-Path -LiteralPath $dl).ProviderPath.TrimEnd('\') + '\'
        foreach ($file in Get-ChildItem -LiteralPath $dl -Recurse -File | Sort-Object FullName) {
            $entryName = $file.FullName.Substring($dlFull.Length).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
} finally { $zipStream.Dispose() }
Write-Host "DELIVERY $dl"
Write-Host "ZIP $zip"
Write-Host ("MANIFEST_SHA256 " + $manifestSha)
