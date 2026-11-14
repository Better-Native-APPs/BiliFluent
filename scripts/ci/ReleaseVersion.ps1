# Shared package-version parsing and release-history checks. This file is dot-sourced by CI scripts.
function ConvertTo-PackageVersion([string]$Value) {
    $parsed = $null
    if ($Value -notmatch '^\d+\.\d+\.\d+\.\d+$' -or -not [version]::TryParse($Value, [ref]$parsed)) {
        throw "Invalid package version '$Value'. Set src/Desktop/BiliCopilot.UI/Package.appxmanifest Identity Version to four numeric components a.b.c.d."
    }
    return $parsed
}

function ConvertFrom-ReleaseTag([string]$Tag) {
    if ($Tag -cnotmatch '^v(?<version>\d+(?:\.\d+){3})(?<pre>-pre(?:\.(?<build>\d+))?)?$') { return }
    return [pscustomobject]@{
        Tag         = $Tag
        Version     = ConvertTo-PackageVersion $Matches.version
        Prerelease  = [bool]$Matches.pre
        LegacyBuild = if ($Matches.build) { [long]$Matches.build } else { 0 }
        Commit      = $null
    }
}

function ConvertFrom-ReleaseTags([string[]]$TagLines) {
    # Accept tag names for offline checks, or ls-remote lines with peeled annotated-tag commits.
    $records = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($line in $TagLines) {
        $tagName = $line.Trim()
        $commit = $null
        $peeled = $false
        if ($line -match '^(?<commit>[0-9a-fA-F]{40,64})\s+refs/tags/(?<tag>.+?)(?<peeled>\^\{\})?$') {
            $tagName = $Matches.tag
            $commit = $Matches.commit
            $peeled = [bool]$Matches.peeled
        }
        $record = ConvertFrom-ReleaseTag $tagName
        if (-not $record) { continue }
        if (-not $records.ContainsKey($tagName) -or $peeled) {
            $record.Commit = $commit
            $records[$tagName] = $record
        }
    }
    return $records.Values
}

function Get-RemoteReleaseTags([string]$RepositoryRoot) {
    $tagLines = @(& git -C $RepositoryRoot ls-remote --tags origin)
    if ($LASTEXITCODE) { throw 'Unable to read origin release tags. Verify checkout credentials and network access, then retry.' }
    return ConvertFrom-ReleaseTags $tagLines
}

function Assert-NewPackageVersion([version]$Version, [object[]]$ReleaseTags, [string]$ExcludedTag) {
    $previous = @($ReleaseTags | Where-Object { $_.Tag -cne $ExcludedTag } | Sort-Object Version -Descending)
    if ($previous.Count -gt 0 -and $Version -le $previous[0].Version) {
        throw "Manifest version $Version must be greater than every existing release package version (latest: $($previous[0].Version), tag $($previous[0].Tag)). Increase Identity Version in src/Desktop/BiliCopilot.UI/Package.appxmanifest and add a dated, non-empty CHANGELOG.md section for that new version."
    }
}

function Get-PreviousReleaseTag([version]$Version, [object[]]$ReleaseTags, [switch]$IncludePrereleases) {
    $previous = @($ReleaseTags | Where-Object { $_.Version -lt $Version -and ($IncludePrereleases -or -not $_.Prerelease) } |
        Sort-Object @{ Expression = { $_.Version }; Descending = $true },
                    @{ Expression = { $_.Prerelease }; Ascending = $true },
                    @{ Expression = { $_.LegacyBuild }; Descending = $true })
    if ($previous.Count -gt 0) { return $previous[0].Tag }
}
