[CmdletBinding()]
param()
# CI: decides what this run produces, from the event and the manifest Identity Version.
#   push main                  -> pre-release  v<version>-pre.<run_number>
#   push tag v<version>        -> release      (tag must equal v + Package.appxmanifest Identity Version)
#   pull_request               -> build only
#   workflow_dispatch          -> like a push of the selected ref (main -> pre-release, v* tag -> release, else build only)
# Outputs (GITHUB_OUTPUT): version, tag, prerelease, publish, title.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
[xml]$manifest = Get-Content (Join-Path $root 'src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Raw
$version = [string]$manifest.Package.Identity.Version
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Invalid manifest Identity Version: $version" }

$event = $env:GITHUB_EVENT_NAME
$ref = $env:GITHUB_REF
$refName = $env:GITHUB_REF_NAME
$tag = ''
$prerelease = $false
$publish = $false

if ($event -eq 'pull_request') {
    $tag = "v$version-pr"
} elseif ($ref -like 'refs/tags/v*') {
    if ($refName -like '*-pre*') { throw "Tag $refName looks like a pre-release tag; pre-releases are produced automatically from main." }
    if ($refName -cne "v$version") { throw "Tag $refName does not match the manifest Identity Version (expected v$version). Bump Package.appxmanifest or re-tag." }
    $tag = $refName
    $publish = $true
} elseif ($ref -eq 'refs/heads/main') {
    $tag = "v$version-pre.$($env:GITHUB_RUN_NUMBER)"
    $prerelease = $true
    $publish = $true
} else {
    $tag = "v$version-build.$($env:GITHUB_RUN_NUMBER)"
}

$title = if ($prerelease) { "BiliFluent $version 预发布版 (build $($env:GITHUB_RUN_NUMBER))" } else { "BiliFluent $version" }
$values = [ordered]@{
    version    = $version
    tag        = $tag
    prerelease = $prerelease.ToString().ToLowerInvariant()
    publish    = $publish.ToString().ToLowerInvariant()
    title      = $title
}
foreach ($key in $values.Keys) {
    if ($env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "$key=$($values[$key])" -Encoding utf8 }
    Write-Host "$key=$($values[$key])"
}
