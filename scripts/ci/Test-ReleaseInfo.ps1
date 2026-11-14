[CmdletBinding()]
param([string]$WorkRoot = [IO.Path]::GetTempPath())
# Offline self-check: all manifests/changelogs are temporary copies, and every scenario injects tags.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$releaseInfo = Join-Path $PSScriptRoot 'Get-ReleaseInfo.ps1'
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
[xml]$sourceManifest = Get-Content (Join-Path $root 'src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Raw -Encoding UTF8
$sourceVersion = ConvertTo-PackageVersion $sourceManifest.Package.Identity.Version
$build = [Math]::Max(1, $sourceVersion.Build)
$previous = [version]::new($sourceVersion.Major, $sourceVersion.Minor, $build - 1, 0)
$baseline = [version]::new($sourceVersion.Major, $sourceVersion.Minor, $build, 0)
$pre = [version]::new($sourceVersion.Major, $sourceVersion.Minor, $build, 1)
$formal = [version]::new($sourceVersion.Major, $sourceVersion.Minor, $build + 1, 0)
$later = [version]::new($sourceVersion.Major, $sourceVersion.Minor, $build + 2, 0)
$history = @("v$previous", "v$previous-pre.17", "v$baseline-pre.19")
$sha = 'a' * 40
$otherSha = 'b' * 40
$formalTags = $history + @("$otherSha`trefs/tags/v$formal", "$sha`trefs/tags/v$formal^{}")
$work = Join-Path $WorkRoot ('bilifluent-release-check-' + [guid]::NewGuid().ToString('N'))
$manifestPath = Join-Path $work 'Package.appxmanifest'
$changelogPath = Join-Path $work 'CHANGELOG.md'
$savedEnvironment = @{}
foreach ($name in @('GITHUB_EVENT_NAME', 'GITHUB_REF', 'GITHUB_REF_NAME', 'GITHUB_SHA', 'GITHUB_RUN_NUMBER', 'GITHUB_OUTPUT')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$script:scenarioCount = 0

function Invoke-Scenario {
    param(
        [string]$Name, [string]$Event, [string]$Ref, [version]$Version, [string[]]$TagLines,
        [hashtable]$Expected, [string]$ExpectedError, [switch]$MissingSection, [switch]$EmptySection
    )
    $sourceManifest.Package.Identity.Version = $Version.ToString()
    $sourceManifest.Save($manifestPath)
    $notes = "## [Unreleased]`n`n- Not part of this release.`n"
    if (-not $MissingSection) {
        $notes += "`n## [$Version] - 2026-11-14 （预发布）`n`n"
        if (-not $EmptySection) { $notes += "- Change for $Version.`n" }
    }
    Set-Content -LiteralPath $changelogPath -Value $notes -Encoding UTF8
    $env:GITHUB_EVENT_NAME = $Event
    $env:GITHUB_REF = $Ref
    $env:GITHUB_REF_NAME = $Ref -replace '^refs/(heads|tags|pull)/', ''
    $env:GITHUB_SHA = $sha
    $env:GITHUB_RUN_NUMBER = '41'
    $env:GITHUB_OUTPUT = $null
    Write-Host "`nSCENARIO: $Name"
    Write-Host "COMMAND: `$env:GITHUB_EVENT_NAME='$Event'; `$env:GITHUB_REF='$Ref'; `$env:GITHUB_REF_NAME='$($env:GITHUB_REF_NAME)'; `$env:GITHUB_SHA='$sha'; `$env:GITHUB_RUN_NUMBER='41'; `$env:GITHUB_OUTPUT=`$null"
    Write-Host 'COMMAND: & ./scripts/ci/Get-ReleaseInfo.ps1 -ManifestPath $manifestPath -ChangelogPath $changelogPath -ExistingTags $tagLines'
    Write-Host "INPUT: manifest=$Version; tags=$($TagLines -join ', '); missing-section=$MissingSection; empty-section=$EmptySection"
    $output = @()
    $failure = $null
    try { $output = @(& $releaseInfo -ManifestPath $manifestPath -ChangelogPath $changelogPath -ExistingTags $TagLines 6>&1) }
    catch { $failure = $_.Exception.Message }
    foreach ($line in $output) { Write-Host $line }
    if ($ExpectedError) {
        if (-not $failure -or $failure -notmatch $ExpectedError) { throw "${Name}: expected error /$ExpectedError/, got '$failure'." }
        Write-Host "ERROR: $failure"
    } else {
        if ($failure) { throw "${Name}: $failure" }
        $text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
        foreach ($key in $Expected.Keys) {
            if ($text -notmatch ('(?m)^' + [regex]::Escape("$key=$($Expected[$key])") + '$')) {
                throw "${Name}: missing expected output $key=$($Expected[$key])."
            }
        }
    }
    $script:scenarioCount++
    Write-Host "PASS: $Name"
}

New-Item -ItemType Directory -Path $work | Out-Null
try {
    $preOutput = @{ version = "$pre"; tag = "v$pre-pre"; prerelease = 'true'; publish = 'true'; title = "BiliFluent $pre 预发布版" }
    $buildOutput = @{ version = "$formal"; tag = "v$formal-build.41"; prerelease = 'false'; publish = 'false' }
    $formalOutput = @{ version = "$formal"; tag = "v$formal"; prerelease = 'false'; publish = 'true' }
    Invoke-Scenario -Name 'main+d>0' -Event push -Ref refs/heads/main -Version $pre -TagLines $history -Expected $preOutput
    Invoke-Scenario -Name 'main+d=0' -Event push -Ref refs/heads/main -Version $formal -TagLines $history -Expected $buildOutput
    Invoke-Scenario -Name 'formal tag matches (annotated)' -Event push -Ref "refs/tags/v$formal" -Version $formal -TagLines $formalTags -Expected $formalOutput
    Invoke-Scenario -Name 'formal tag mismatch' -Event push -Ref "refs/tags/v$later" -Version $formal -TagLines $history -ExpectedError 'does not match the manifest'
    Invoke-Scenario -Name 'formal tag with d>0' -Event push -Ref "refs/tags/v$pre" -Version $pre -TagLines $history -ExpectedError 'fourth manifest version component to be 0'
    Invoke-Scenario -Name 'pre-release tag already exists' -Event push -Ref refs/heads/main -Version $pre -TagLines ($history + "v$pre-pre") -ExpectedError 'already exists'
    Invoke-Scenario -Name 'PR version not increased' -Event pull_request -Ref refs/pull/1/merge -Version $baseline -TagLines $history -ExpectedError 'must be greater'
    Invoke-Scenario -Name 'main version not increased' -Event push -Ref refs/heads/main -Version $baseline -TagLines $history -ExpectedError 'must be greater'
    Invoke-Scenario -Name 'missing changelog section' -Event push -Ref refs/heads/main -Version $pre -TagLines $history -MissingSection -ExpectedError 'section \[.+\] not found'
    Invoke-Scenario -Name 'empty changelog section' -Event push -Ref refs/heads/main -Version $pre -TagLines $history -EmptySection -ExpectedError 'section \[.+\] is empty'
    Invoke-Scenario -Name 'PR build only' -Event pull_request -Ref refs/pull/1/merge -Version $pre -TagLines $history -Expected @{ tag = "v$pre-pr"; publish = 'false'; prerelease = 'false' }
    Invoke-Scenario -Name 'dispatch main+d>0' -Event workflow_dispatch -Ref refs/heads/main -Version $pre -TagLines $history -Expected $preOutput
    Invoke-Scenario -Name 'dispatch main+d=0' -Event workflow_dispatch -Ref refs/heads/main -Version $formal -TagLines $history -Expected $buildOutput
    Invoke-Scenario -Name 'dispatch formal tag matches' -Event workflow_dispatch -Ref "refs/tags/v$formal" -Version $formal -TagLines $formalTags -Expected $formalOutput
    Invoke-Scenario -Name 'dispatch pre-release tag rejected' -Event workflow_dispatch -Ref "refs/tags/v$pre-pre" -Version $pre -TagLines $history -ExpectedError 'select main instead'
    Invoke-Scenario -Name 'formal tag target mismatch' -Event push -Ref "refs/tags/v$formal" -Version $formal -TagLines ($history + "$otherSha`trefs/tags/v$formal") -ExpectedError 'point to GITHUB_SHA'
    Invoke-Scenario -Name 'formal version below another release' -Event push -Ref "refs/tags/v$formal" -Version $formal -TagLines ($formalTags + "v$later-pre") -ExpectedError 'must be greater'

    $parsedTags = @(ConvertFrom-ReleaseTags $history)
    if ($parsedTags.Count -ne 3 -or ($parsedTags | Where-Object { $_.Version -isnot [version] })) { throw 'Release tags were not parsed as package versions.' }
    $ten = [version]::new($sourceVersion.Major, $sourceVersion.Minor, $build, 10)
    $two = [version]::new($sourceVersion.Major, $sourceVersion.Minor, $build, 2)
    Assert-NewPackageVersion -Version $ten -ReleaseTags @(ConvertFrom-ReleaseTags @("v$two-pre"))
    if ((Get-PreviousReleaseTag -Version $ten -ReleaseTags @(ConvertFrom-ReleaseTags ($history + @("v$pre-pre", "v$two-pre"))) -IncludePrereleases) -cne "v$two-pre") { throw 'Pre-release predecessor must be the previous package version.' }
    if ((Get-PreviousReleaseTag -Version $formal -ReleaseTags $parsedTags) -cne "v$previous") { throw 'Formal predecessor must be the previous formal release.' }
    Write-Host "`nPASS: legacy tag parsing, numeric comparison ($ten > $two), and release predecessors"
    Write-Host "PASS: $script:scenarioCount event scenarios; no remote access or source-manifest writes"
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    Remove-Item -LiteralPath $work -Recurse -Force
}
