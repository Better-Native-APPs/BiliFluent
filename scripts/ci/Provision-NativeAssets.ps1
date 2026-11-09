[CmdletBinding()]
param(
    [string]$SevenZipPath
)
# CI: provisions the untracked x64 native assets required by scripts/Build-Release.ps1 (see scripts/native-assets.json).
# Downloads are pinned to shinchiro/mpv-winbuild-cmake release 20261006; both the archive and the extracted file are SHA-256 verified.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$assets = Join-Path $root 'src/Desktop/BiliCopilot.UI/Assets'
$work = Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }) 'bilifluent-native'

if (-not $SevenZipPath) {
    $cmd = Get-Command 7z -ErrorAction SilentlyContinue
    if ($cmd) { $SevenZipPath = $cmd.Source } else { $SevenZipPath = Join-Path $env:ProgramFiles '7-Zip/7z.exe' }
}
if (-not (Test-Path -LiteralPath $SevenZipPath)) { throw "7-Zip not found: $SevenZipPath" }

function Get-Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$base = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261006'
$pins = @(
    [pscustomobject]@{
        Url = "$base/mpv-dev-x86_64-20261006-git-6c092d978b.7z"; ArchiveSha256 = 'bc0e016bdf6573bada824873d4746ab58d9bfac0ddd59c046f160312f81f9548'
        File = 'libmpv-2.dll'; FileSha256 = 'd10d0994bd1398813da87fdb299961b378c71561bae1d135cb2b641b5864d7eb'; Target = 'libmpv/x64'
    },
    [pscustomobject]@{
        Url = "$base/ffmpeg-x86_64-git-47313ad3f.7z"; ArchiveSha256 = '32f3ab53954901c6fd7a0fd811b0724ded3239306009af41b99b10094d91a25a'
        File = 'ffmpeg.exe'; FileSha256 = '97cba10ab56e0ed7b3fc09004f1caedd8468924fd5fb39db126383a053da9c5e'; Target = 'ffmpeg/x64'
    }
)

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null
foreach ($pin in $pins) {
    $destination = Join-Path (Join-Path $assets $pin.Target) $pin.File
    if ((Test-Path -LiteralPath $destination) -and (Get-Sha $destination) -ceq $pin.FileSha256) {
        Write-Host "Already provisioned: $($pin.Target)/$($pin.File)"
        continue
    }
    $archive = Join-Path $work ([IO.Path]::GetFileName($pin.Url))
    Write-Host "Downloading $($pin.Url)"
    for ($attempt = 1; ; $attempt++) {
        try { Invoke-WebRequest -Uri $pin.Url -OutFile $archive -UseBasicParsing -MaximumRedirection 10; break }
        catch { if ($attempt -ge 6) { throw }; Write-Warning "Download failed (attempt $attempt): $($_.Exception.Message)"; Start-Sleep -Seconds (15 * $attempt) }
    }
    $actual = Get-Sha $archive
    if ($actual -cne $pin.ArchiveSha256) { throw "Archive SHA-256 mismatch for $($pin.Url): expected $($pin.ArchiveSha256), got $actual" }

    $extract = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($archive))
    & $SevenZipPath x $archive ("-o" + $extract) -y -bso0 -bsp0
    if ($LASTEXITCODE) { throw "7-Zip failed to extract $archive ($LASTEXITCODE)" }
    $found = @(Get-ChildItem -LiteralPath $extract -Recurse -File -Filter $pin.File)
    if ($found.Count -ne 1) { throw "Expected exactly one $($pin.File) in $archive, found $($found.Count)" }
    $actual = Get-Sha $found[0].FullName
    if ($actual -cne $pin.FileSha256) { throw "Extracted $($pin.File) SHA-256 mismatch: expected $($pin.FileSha256), got $actual" }

    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath $found[0].FullName -Destination $destination -Force
    Write-Host "Provisioned $($pin.Target)/$($pin.File) ($actual)"
}

# Visor (AOT helper). Prefer the copy tracked in git; otherwise build it with Native AOT.
$visorRelative = 'src/Desktop/BiliCopilot.UI/Assets/visor/x64/BiliCopilot.Visor.exe'
$visor = Join-Path $root $visorRelative
Push-Location $root
try { $tracked = [bool](& git ls-files -- $visorRelative) } finally { Pop-Location }
if ($tracked -and (Test-Path -LiteralPath $visor)) {
    Write-Host "Using tracked Visor: $visorRelative ($(Get-Sha $visor))"
} else {
    $publishDir = Join-Path $work 'visor-publish'
    & dotnet publish (Join-Path $root 'src/Desktop/BiliCopilot.Visor/BiliCopilot.Visor.csproj') -c Release -r win-x64 -p:PublishAot=true -o $publishDir -nologo
    if ($LASTEXITCODE) { throw "Visor publish failed ($LASTEXITCODE)" }
    $built = Join-Path $publishDir 'BiliCopilot.Visor.exe'
    if (-not (Test-Path -LiteralPath $built)) { throw "Visor publish produced no BiliCopilot.Visor.exe in $publishDir" }
    New-Item -ItemType Directory -Force -Path (Split-Path $visor -Parent) | Out-Null
    Copy-Item -LiteralPath $built -Destination $visor -Force
    Write-Host "Built Visor with Native AOT ($(Get-Sha $visor))"
}

# Same check Build-Release.ps1 performs, so failures surface here with a clear message.
$native = Get-Content (Join-Path $root 'scripts/native-assets.json') -Raw | ConvertFrom-Json
foreach ($file in $native.files.PSObject.Properties) {
    if (-not (Test-Path (Join-Path $assets $file.Name))) { throw "Native asset still missing: $($file.Name)" }
}
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Native assets ready.'
