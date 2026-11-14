# Shared, offline-testable contracts for unsigned compile artifacts and their provenance.
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')

function Set-CiOutput([string]$Name, [string]$Value) {
    if ($env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "$Name=$Value" -Encoding utf8 }
    Write-Host "$Name=$Value"
}

function Test-CiReusableRun([object]$Run, [long]$RepositoryId, [long]$WorkflowId, [string]$SourceSha) {
    return $Run.status -ceq 'completed' -and $Run.conclusion -ceq 'success' -and
        $Run.head_sha -ceq $SourceSha -and $Run.workflow_id -eq $WorkflowId -and
        $Run.repository.id -eq $RepositoryId -and $Run.head_repository.id -eq $RepositoryId -and
        $Run.head_repository.fork -eq $false
}

function Select-CiCompileArtifact {
    param([object]$Run, [object[]]$Artifacts, [long]$RepositoryId, [long]$WorkflowId, [string]$SourceSha)
    if (-not (Test-CiReusableRun $Run $RepositoryId $WorkflowId $SourceSha)) { return }
    $namePattern = '^compiled-msix-' + [regex]::Escape($SourceSha) + '-' + $Run.id + '-\d+$'
    $eligible = @($Artifacts | Where-Object {
        $_.name -cmatch $namePattern -and $_.expired -eq $false -and $_.size_in_bytes -gt 0 -and
        $_.digest -cmatch '^sha256:[a-f0-9]{64}$' -and $_.workflow_run.id -eq $Run.id -and
        $_.workflow_run.repository_id -eq $RepositoryId -and $_.workflow_run.head_repository_id -eq $RepositoryId -and
        $_.workflow_run.head_sha -ceq $SourceSha
    } | Sort-Object id -Descending)
    if ($eligible.Count -gt 0) { return $eligible[0] }
}

function Get-CiCompilePlan([string]$Event, [string]$Ref, [bool]$Publish, [bool]$HasArtifact) {
    if ($Event -cne 'push' -or ($Ref -cne 'refs/heads/main' -and $Ref -notlike 'refs/tags/v*') -or -not $HasArtifact) { return 'build' }
    if (-not $Publish) { return 'skip' }
    return 'reuse'
}

function Test-CiCompilationNeeded([string]$Plan, [bool]$Restored) {
    return $Plan -cne 'skip' -and -not $Restored
}

function Get-CiApiHeaders {
    if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is required to read workflow artifacts.' }
    return @{ Authorization = "Bearer $($env:GH_TOKEN)"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
}

function Invoke-CiGitHubApi([string]$Path) {
    $api = if ($env:GITHUB_API_URL) { $env:GITHUB_API_URL.TrimEnd('/') } else { 'https://api.github.com' }
    return Invoke-RestMethod -Uri ($api + $Path) -Headers (Get-CiApiHeaders) -UserAgent 'BiliFluent-CI' -UseBasicParsing
}

function Assert-CiFileHash([string]$Path, [string]$Sha256, [string]$Label) {
    if ($Sha256 -cnotmatch '^[a-f0-9]{64}$' -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing file or invalid SHA-256: $Label" }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Sha256) { throw "SHA-256 mismatch: $Label" }
}

function Get-CiSourceIdentity([string]$ManifestPath, [string]$Version) {
    [xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8
    $identity = $manifest.Package.Identity
    $null = ConvertTo-PackageVersion $Version
    if ([string]$identity.Version -cne $Version) { throw 'Resolved release version differs from Package.appxmanifest Identity Version.' }
    return [pscustomobject]@{ Name = [string]$identity.Name; Publisher = [string]$identity.Publisher; Version = $Version; Architecture = 'x64' }
}

function Get-CiPackageIdentity([string]$PackagePath, [switch]$Unsigned) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        if ($Unsigned -and @($zip.Entries | Where-Object { $_.FullName -ceq 'AppxSignature.p7x' }).Count -gt 0) { throw 'Reusable compile artifacts must contain an unsigned MSIX, not a previously signed package.' }
        $entries = @($zip.Entries | Where-Object { $_.FullName -ceq 'AppxManifest.xml' })
        if ($entries.Count -ne 1) { throw 'Expected exactly one AppxManifest.xml in the compiled MSIX.' }
        $settings = [System.Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $entries[0].Open()
        $reader = [System.Xml.XmlReader]::Create($stream, $settings)
        try {
            $xml = [System.Xml.XmlDocument]::new()
            $xml.XmlResolver = $null
            $xml.Load($reader)
            $ns = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
            $ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
            $identity = $xml.SelectSingleNode('/f:Package/f:Identity', $ns)
            if (-not $identity) { throw 'Compiled MSIX Identity is missing.' }
            return [pscustomobject]@{
                Name = $identity.GetAttribute('Name'); Publisher = $identity.GetAttribute('Publisher')
                Version = $identity.GetAttribute('Version'); Architecture = $identity.GetAttribute('ProcessorArchitecture')
            }
        } finally { $reader.Dispose(); $stream.Dispose() }
    } finally { $zip.Dispose() }
}

function Assert-CiPackageIdentity([string]$PackagePath, [object]$Expected, [switch]$Unsigned) {
    $actual = Get-CiPackageIdentity -PackagePath $PackagePath -Unsigned:$Unsigned
    foreach ($key in @('Name', 'Publisher', 'Version', 'Architecture')) {
        if ($actual.$key -cne $Expected.$key) { throw "Compiled MSIX $key differs from the resolved manifest identity." }
    }
}

function Assert-CiCompileArtifact {
    param([string]$Directory, [string]$SourceSha, [string]$Repository, [object]$Identity)
    $metadataPath = Join-Path $Directory 'compiled-artifacts.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw 'compiled-artifacts.json is missing; this is not a reusable compile artifact.' }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($metadata.schemaVersion -ne 1 -or $metadata.sourceSha -cne $SourceSha -or $metadata.sourceRepository -cne $Repository -or
        $metadata.version -cne $Identity.Version) { throw 'Compile artifact provenance or version differs from the current source commit.' }
    $packageName = "BiliFluent_$($Identity.Version)_x64.msix"
    if ($metadata.package -cne $packageName) { throw 'Compile artifact package name differs from the manifest version.' }
    $files = @($metadata.files)
    if ($files.Count -eq 0 -or @($files.relativePath | Select-Object -Unique).Count -ne $files.Count -or
        @($files | Where-Object { $_.relativePath -ceq $packageName }).Count -ne 1) { throw 'Compile artifact file inventory is empty, duplicated, or missing the MSIX.' }
    foreach ($file in $files) {
        $relative = [string]$file.relativePath
        if ($relative.Contains('..') -or ($relative -cne $packageName -and $relative -cnotmatch '^dependencies/[A-Za-z0-9][A-Za-z0-9._-]*\.(?:msix|appx)$')) {
            throw 'Compile artifact contains an unexpected or unsafe relative path.'
        }
        $path = Join-Path $Directory $relative
        Assert-CiFileHash -Path $path -Sha256 $file.sha256 -Label $relative
        if ((Get-Item -LiteralPath $path).Length -ne $file.size) { throw "Compile artifact file size differs: $relative" }
    }
    $metadataFullName = (Get-Item -LiteralPath $metadataPath).FullName
    $actualFiles = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object { $_.FullName -cne $metadataFullName })
    if ($actualFiles.Count -ne $files.Count) { throw 'Compile artifact contains unlisted files.' }
    Assert-CiPackageIdentity -PackagePath (Join-Path $Directory $packageName) -Expected $Identity -Unsigned
    return $metadata
}

function Expand-CiCompileArchive([string]$ArchivePath, [string]$Directory) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.Contains('..') -or $entry.FullName -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*(?:/[A-Za-z0-9][A-Za-z0-9._-]*)*/?$') {
                throw 'Compile archive contains an unsafe extraction path.'
            }
        }
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $Directory)
}
