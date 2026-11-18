[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$SourceSha,
    [Parameter(Mandatory)] [string]$SourceRepository,
    [string]$OutputDirectory
)
# Stage only unsigned MSIX/framework bytes and a relative-path inventory, before importing signing keys.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CompileArtifact.ps1')
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$head = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -or $head -cne $SourceSha) { throw 'Checked-out commit differs from the compile artifact source SHA.' }
$identity = Get-CiSourceIdentity -ManifestPath (Join-Path $root 'src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Version $Version
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/compiled' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Compile artifact directory already exists. Use a clean output directory.' }
$releaseDirectory = Join-Path $root 'artifacts/release'
$packageName = "BiliFluent_${Version}_x64.msix"
$packagePath = Join-Path $releaseDirectory $packageName
Assert-CiPackageIdentity -PackagePath $packagePath -Expected $identity -Unsigned
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
Copy-Item -LiteralPath $packagePath -Destination (Join-Path $OutputDirectory $packageName)
$frameworks = Join-Path $releaseDirectory 'dependencies'
if (Test-Path -LiteralPath $frameworks) { Copy-Item -LiteralPath $frameworks -Destination (Join-Path $OutputDirectory 'dependencies') -Recurse }
$prefix = (Get-Item -LiteralPath $OutputDirectory).FullName.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$files = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ relativePath = $_.FullName.Substring($prefix.Length).Replace('\', '/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{ schemaVersion = 1; sourceSha = $SourceSha; sourceRepository = $SourceRepository; version = $Version; package = $packageName; files = $files } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'compiled-artifacts.json') -Encoding UTF8
$null = Assert-CiCompileArtifact -Directory $OutputDirectory -SourceSha $SourceSha -Repository $SourceRepository -Identity $identity
Set-CiOutput 'artifact_name' "compiled-msix-$SourceSha-$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)"
Write-Host "Staged unsigned compile artifact for $Version ($SourceSha)."
