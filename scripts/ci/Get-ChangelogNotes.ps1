[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Path,
    [Parameter(Mandatory)] [string]$Version
)
# Returns a Keep a Changelog section without its heading or trailing link references.
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'CHANGELOG.md is missing. Add a dated, non-empty section for the manifest version before merging to main.' }
$lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
$contentEnd = $lines.Count
while ($contentEnd -gt 0) {
    $line = $lines[$contentEnd - 1]
    if ([string]::IsNullOrWhiteSpace($line) -or $line -match '^ {0,3}\[[^\]]+\]:\s*\S') {
        $contentEnd--
    } else {
        break
    }
}

$headingPattern = '^## \[' + [regex]::Escape($Version) + '\] - (?<date>\d{4}-\d{2}-\d{2})(?:[ \t].*)?$'
$start = -1
for ($i = 0; $i -lt $contentEnd; $i++) {
    if ($lines[$i] -match $headingPattern) {
        $sectionDate = [datetime]::MinValue
        if (-not [datetime]::TryParseExact($Matches.date, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None, [ref]$sectionDate)) {
            throw "CHANGELOG.md section [$Version] has an invalid date. Use ## [$Version] - YYYY-MM-DD with a valid calendar date."
        }
        $start = $i + 1
        break
    }
}
if ($start -eq -1) {
    throw [System.Collections.Generic.KeyNotFoundException]::new("CHANGELOG.md section [$Version] not found. Rename [Unreleased] to ## [$Version] - YYYY-MM-DD and include this release's changes before merging to main.")
}

$end = $contentEnd
for ($i = $start; $i -lt $contentEnd; $i++) {
    if ($lines[$i] -match '^##(?:\s|$)') {
        $end = $i
        break
    }
}
$notes = if ($start -lt $end) { ($lines[$start..($end - 1)] -join "`n").Trim() } else { '' }
if ([string]::IsNullOrWhiteSpace($notes)) { throw "CHANGELOG.md section [$Version] is empty. Record the changes for this version before merging to main." }
return $notes
