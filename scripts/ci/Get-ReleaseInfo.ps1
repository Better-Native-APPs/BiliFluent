[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$ChangelogPath,
    [AllowEmptyCollection()] [string[]]$ExistingTags
)
# CI: decides what this run produces, from the event and the manifest Identity Version.
#   push main, fourth part > 0 -> pre-release  v<version>-pre
#   push main, fourth part = 0 -> build only
#   push tag v<version>        -> release      (matching manifest, fourth part = 0)
#   pull_request               -> build only
#   workflow_dispatch          -> like a push of the selected ref
# ExistingTags bypasses remote access for offline checks; it accepts names or git ls-remote lines.
# Outputs (GITHUB_OUTPUT): version, tag, prerelease, publish, title.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
if (-not $ManifestPath) { $ManifestPath = Join-Path $root 'src/Desktop/BiliCopilot.UI/Package.appxmanifest' }
if (-not $ChangelogPath) { $ChangelogPath = Join-Path $root 'CHANGELOG.md' }
[xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8
$version = [string]$manifest.Package.Identity.Version
$packageVersion = ConvertTo-PackageVersion $version

$event = $env:GITHUB_EVENT_NAME
$ref = $env:GITHUB_REF
$refName = $env:GITHUB_REF_NAME
$tag = ''
$prerelease = $false
$publish = $false
$formalTag = $false

if ($event -eq 'pull_request') {
    $tag = "v$version-pr"
} elseif ($ref -like 'refs/tags/v*') {
    if ($refName -like '*-pre*') { throw "Tag $refName is a pre-release tag. Pre-releases are produced from main; select main instead of dispatching a -pre tag." }
    if ($packageVersion.Revision -ne 0) { throw 'Formal release tags require the fourth manifest version component to be 0. Set Identity Version to a.b.(c+1).0, greater than all published package versions, add its CHANGELOG.md section, merge to main, then push the matching v<version> tag.' }
    if ($refName -cne "v$version") { throw "Tag $refName does not match the manifest Identity Version (expected v$version). Push the matching formal tag, or update Package.appxmanifest and its CHANGELOG.md section before tagging the new commit." }
    $tag = $refName
    $publish = $true
    $formalTag = $true
} elseif ($ref -eq 'refs/heads/main' -and $packageVersion.Revision -gt 0) {
    $tag = "v$version-pre"
    $prerelease = $true
    $publish = $true
} else {
    $tag = "v$version-build.$($env:GITHUB_RUN_NUMBER)"
}

$releaseTags = if ($PSBoundParameters.ContainsKey('ExistingTags')) {
    @(ConvertFrom-ReleaseTags $ExistingTags)
} else {
    @(Get-RemoteReleaseTags $root)
}
$matchingTag = @($releaseTags | Where-Object { $_.Tag -ceq $tag })
$excludedTag = ''
if ($formalTag) {
    # A pushed formal tag already exists. Exclude only that verified trigger from the version ceiling.
    if ($matchingTag.Count -ne 1 -or -not $env:GITHUB_SHA -or $matchingTag[0].Commit -cne $env:GITHUB_SHA) {
        throw "Formal tag $tag must exist in origin and point to GITHUB_SHA. Push the matching tag for this commit; do not move an existing release tag. Offline checks must supply its commit in an ls-remote-style ExistingTags entry."
    }
    $excludedTag = $tag
} elseif ($publish -and $matchingTag.Count -gt 0) {
    throw "Release tag $tag already exists. Increase the manifest Identity Version (the fourth component for a pre-release), add the corresponding CHANGELOG.md section, and merge a new commit; do not reuse or overwrite the tag."
}
Assert-NewPackageVersion -Version $packageVersion -ReleaseTags $releaseTags -ExcludedTag $excludedTag
$null = & (Join-Path $PSScriptRoot 'Get-ChangelogNotes.ps1') -Path $ChangelogPath -Version $version

$title = if ($prerelease) { "BiliFluent $version 预发布版" } else { "BiliFluent $version" }
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
