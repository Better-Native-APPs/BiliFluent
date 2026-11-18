[CmdletBinding()]
param(
    [string]$SevenZipPath
)
# CI: provisions the untracked x64 native assets required by scripts/Build-Release.ps1 (see scripts/native-assets.json).
# Download metadata is pinned in scripts/native-assets.json; both the archive and the extracted file are SHA-256 verified.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$assets = Join-Path $root 'src/Desktop/BiliCopilot.UI/Assets'
$native = Get-Content (Join-Path $root 'scripts/native-assets.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$work = Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }) 'bilifluent-native'

if (-not $SevenZipPath) {
    $cmd = Get-Command 7z -ErrorAction SilentlyContinue
    if ($cmd) { $SevenZipPath = $cmd.Source } else { $SevenZipPath = Join-Path $env:ProgramFiles '7-Zip/7z.exe' }
}
if (-not (Test-Path -LiteralPath $SevenZipPath)) { throw "7-Zip not found: $SevenZipPath" }

function Get-Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null
foreach ($pin in $native.downloads) {
    $url = "https://github.com/$($pin.repository)/releases/download/$($pin.releaseTag)/$($pin.archiveName)"
    $destination = Join-Path $assets $pin.targetPath
    if ((Test-Path -LiteralPath $destination) -and (Get-Sha $destination) -ceq $pin.FileSha256) {
        Write-Host "Already provisioned: $($pin.targetPath)"
        continue
    }
    $archive = Join-Path $work $pin.archiveName
    Write-Host "Downloading $url"
    for ($attempt = 1; ; $attempt++) {
        try { Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing -MaximumRedirection 10; break }
        catch { if ($attempt -ge 6) { throw }; Write-Warning "Download failed (attempt $attempt): $($_.Exception.Message)"; Start-Sleep -Seconds (15 * $attempt) }
    }
    $actual = Get-Sha $archive
    if ($actual -cne $pin.ArchiveSha256) { throw "Archive SHA-256 mismatch for $url`: expected $($pin.ArchiveSha256), got $actual" }

    $extract = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($archive))
    & $SevenZipPath x $archive ("-o" + $extract) -y -bso0 -bsp0
    if ($LASTEXITCODE) { throw "7-Zip failed to extract $archive ($LASTEXITCODE)" }
    $found = @(Get-ChildItem -LiteralPath $extract -Recurse -File -Filter $pin.fileName)
    if ($found.Count -ne 1) { throw "Expected exactly one $($pin.fileName) in $archive, found $($found.Count)" }
    $actual = Get-Sha $found[0].FullName
    if ($actual -cne $pin.FileSha256) { throw "Extracted $($pin.fileName) SHA-256 mismatch: expected $($pin.FileSha256), got $actual" }

    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath $found[0].FullName -Destination $destination -Force
    Write-Host "Provisioned $($pin.targetPath) ($actual)"
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
foreach ($file in $native.files.PSObject.Properties) {
    if (-not (Test-Path (Join-Path $assets $file.Name))) { throw "Native asset still missing: $($file.Name)" }
}
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Native assets ready.'
