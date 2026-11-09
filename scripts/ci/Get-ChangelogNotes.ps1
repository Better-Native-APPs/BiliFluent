[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Path,
    [Parameter(Mandatory)] [string]$Version,
    [switch]$Unreleased
)
# Returns a Keep a Changelog section without its heading or trailing link references.
$ErrorActionPreference = 'Stop'
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

$section = if ($Unreleased) { 'Unreleased' } else { $Version }
$headingPattern = '^## \[' + [regex]::Escape($section) + '\](?:[ \t].*)?$'
$start = -1
for ($i = 0; $i -lt $contentEnd; $i++) {
    if ($lines[$i] -match $headingPattern) {
        $start = $i + 1
        break
    }
}
if ($start -eq -1) {
    if ($Unreleased) { return '' }
    throw [System.Collections.Generic.KeyNotFoundException]::new("Changelog section [$Version] not found in '$Path'.")
}

$end = $contentEnd
for ($i = $start; $i -lt $contentEnd; $i++) {
    if ($lines[$i] -match '^##(?:\s|$)') {
        $end = $i
        break
    }
}
if ($start -ge $end) { return '' }
return ($lines[$start..($end - 1)] -join "`n").Trim()
