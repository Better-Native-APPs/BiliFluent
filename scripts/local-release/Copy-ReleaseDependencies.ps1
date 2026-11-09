[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [Parameter(Mandatory = $true)][string]$DestinationDirectory
)

# Only copy the exact, hash-pinned frameworks declared by the built app. No downloads.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ReleaseAppxManifest([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($zip.Entries | Where-Object { $_.FullName -ceq 'AppxManifest.xml' })
        if ($entries.Count -ne 1) { throw 'Expected exactly one AppxManifest.xml in the MSIX.' }
        $settings = New-Object System.Xml.XmlReaderSettings
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $entries[0].Open()
        $reader = [System.Xml.XmlReader]::Create($stream, $settings)
        try {
            $xml = New-Object System.Xml.XmlDocument
            $xml.XmlResolver = $null
            $xml.Load($reader)
            return ,$xml
        } finally { $reader.Dispose(); $stream.Dispose() }
    } finally { $zip.Dispose() }
}

$template = Get-Content (Join-Path $PSScriptRoot 'release-artifacts.json') -Raw | ConvertFrom-Json
$pins = @($template.dependencies)
$app = Get-ReleaseAppxManifest $PackagePath
$ns = New-Object System.Xml.XmlNamespaceManager($app.NameTable)
$ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$declared = @($app.SelectNodes('/f:Package/f:Dependencies/f:PackageDependency', $ns))
if ($declared.Count -ne $pins.Count -or @($pins.name | Select-Object -Unique).Count -ne $pins.Count) {
    throw 'Every declared framework must have exactly one pinned release dependency.'
}

New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
foreach ($pin in $pins) {
    if ($pin.relativePath -cnotmatch '^payload/[A-Za-z0-9][A-Za-z0-9._-]{0,150}[A-Za-z0-9]$' -or $pin.relativePath.Contains('..')) {
        throw 'Dependency relativePath must be a single file under payload/.'
    }
    $dependency = @($declared | Where-Object { $_.GetAttribute('Name') -ceq $pin.name })
    if ($dependency.Count -ne 1 -or $dependency[0].GetAttribute('Publisher') -cne $pin.publisher -or
        $dependency[0].GetAttribute('MinVersion') -cne $pin.declaredMinVersion -or [version]$pin.version -lt [version]$pin.declaredMinVersion) {
        throw "Pinned dependency differs from the built MSIX declaration: $($pin.name)"
    }
    $fileName = $pin.relativePath.Substring('payload/'.Length)
    $source = Join-Path $SourceDirectory $fileName
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing pinned framework MSIX: $fileName" }
    $sha = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sha -cne $pin.sha256) { throw "Framework SHA-256 differs from the release pin: $fileName" }
    $framework = Get-ReleaseAppxManifest $source
    $identity = $framework.SelectSingleNode('/f:Package/f:Identity', $ns)
    $isFramework = $framework.SelectSingleNode('/f:Package/f:Properties/f:Framework', $ns)
    if ($null -eq $identity -or $identity.GetAttribute('Name') -cne $pin.name -or $identity.GetAttribute('Publisher') -cne $pin.publisher -or
        $identity.GetAttribute('Version') -cne $pin.version -or $identity.GetAttribute('ProcessorArchitecture') -cne $pin.architecture -or
        $pin.architecture -cne 'x64' -or $null -eq $isFramework -or $isFramework.InnerText.Trim() -cne 'true') {
        throw "Framework identity differs from the release pin: $fileName"
    }
    if (@($framework.SelectNodes('/f:Package/f:Dependencies/f:PackageDependency', $ns)).Count -ne 0) {
        throw "Framework has additional dependencies that must also be bundled: $fileName"
    }
    if ((Get-AuthenticodeSignature -LiteralPath $source).Status -ne 'Valid') { throw "Framework platform signature is not Valid: $fileName" }
    $destination = Join-Path $DestinationDirectory $fileName
    Copy-Item -LiteralPath $source -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.sha256) {
        throw "Copied framework SHA-256 differs from the release pin: $fileName"
    }
    $pin
}
