[CmdletBinding()]
param([string]$WorkRoot = [IO.Path]::GetTempPath())
# Offline contract tests only: inert ZIP fixtures, no network, signing keys, or app deployment.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CompileArtifact.ps1')
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
[xml]$manifest = Get-Content (Join-Path $root 'src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Raw -Encoding UTF8
$version = [string]$manifest.Package.Identity.Version
$identity = Get-CiSourceIdentity -ManifestPath (Join-Path $root 'src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Version $version
$sha = 'a' * 40
$repository = 'example/project'
$goodRun = [pscustomobject]@{ id = 1001; status = 'completed'; conclusion = 'success'; head_sha = $sha; workflow_id = 9
    repository = @{ id = 42 }; head_repository = @{ id = 42; fork = $false } }
$goodArtifact = [pscustomobject]@{ id = 2001; name = "compiled-msix-$sha-1001-1"; expired = $false; size_in_bytes = 100; digest = 'sha256:' + ('b' * 64)
    workflow_run = @{ id = 1001; repository_id = 42; head_repository_id = 42; head_sha = $sha } }
$work = Join-Path $WorkRoot ('compile-artifact-check-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
$packageName = "BiliFluent_${version}_x64.msix"
$metadataPath = Join-Path $payload 'compiled-artifacts.json'
$script:count = 0

function Assert-That([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Copy-Data([object]$Value) { return ($Value | ConvertTo-Json -Depth 10 | ConvertFrom-Json) }
function Test-Case([string]$Name, [scriptblock]$Check, [string]$ErrorPattern) {
    $failure = $null
    try { & $Check } catch { $failure = $_.Exception.Message }
    if ($ErrorPattern) { Assert-That ($failure -and $failure -match $ErrorPattern) "${Name}: expected /$ErrorPattern/, got '$failure'." }
    elseif ($failure) { throw "${Name}: $failure" }
    $script:count++
    Write-Host "PASS: $Name"
}
function Select-Artifact([object]$Run, [object[]]$Artifacts) {
    return Select-CiCompileArtifact -Run $Run -Artifacts $Artifacts -RepositoryId 42 -WorkflowId 9 -SourceSha $sha
}
function Write-Metadata([object]$Metadata) { $Metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding UTF8 }
function New-Fixture([object]$PackageIdentity = $identity, [switch]$Signed) {
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
    [void][IO.Directory]::CreateDirectory((Join-Path $payload 'dependencies'))
    $xml = [xml]'<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity /></Package>'
    $fixtureIdentity = $xml.DocumentElement.FirstChild
    foreach ($key in @('Name', 'Publisher', 'Version')) { $fixtureIdentity.SetAttribute($key, $PackageIdentity.$key) }
    $fixtureIdentity.SetAttribute('ProcessorArchitecture', $PackageIdentity.Architecture)
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $payload $packageName), [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('AppxManifest.xml')
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($xml.OuterXml) } finally { $writer.Dispose() }
        if ($Signed) {
            $entry = $zip.CreateEntry('AppxSignature.p7x')
            $stream = $entry.Open()
            try { $stream.WriteByte(1) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    [IO.File]::WriteAllText((Join-Path $payload 'dependencies/framework.msix'), 'INERT FRAMEWORK FIXTURE')
    $files = @(foreach ($relative in @($packageName, 'dependencies/framework.msix')) {
        $path = Join-Path $payload $relative
        [pscustomobject]@{ relativePath = $relative; size = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    Write-Metadata ([pscustomobject]@{ schemaVersion = 1; sourceSha = $sha; sourceRepository = $repository; version = $version; package = $packageName; files = $files })
}
function Test-Payload {
    $null = Assert-CiCompileArtifact -Directory $payload -SourceSha $sha -Repository $repository -Identity $identity
}

New-Item -ItemType Directory -Path $work | Out-Null
try {
    Test-Case 'successful same-repository/workflow/SHA artifact accepted' { Assert-That ((Select-Artifact $goodRun @($goodArtifact)).id -eq 2001) 'Expected the matching artifact.' }
    foreach ($variant in @('failed', 'running', 'wrong workflow', 'wrong SHA', 'wrong repository', 'fork', 'missing head repository')) {
        $run = Copy-Data $goodRun
        switch ($variant) {
            'failed' { $run.conclusion = 'failure' }
            'running' { $run.status = 'in_progress' }
            'wrong workflow' { $run.workflow_id = 10 }
            'wrong SHA' { $run.head_sha = 'c' * 40 }
            'wrong repository' { $run.repository.id = 43 }
            'fork' { $run.head_repository.id = 43; $run.head_repository.fork = $true }
            'missing head repository' { $run.head_repository = $null }
        }
        Test-Case "run rejected: $variant" { Assert-That (-not (Select-Artifact $run @($goodArtifact))) 'Untrusted run was accepted.' }
    }
    foreach ($variant in @('expired', 'missing digest', 'wrong name', 'wrong run', 'wrong repository', 'wrong head repository', 'wrong SHA')) {
        $artifact = Copy-Data $goodArtifact
        switch ($variant) {
            'expired' { $artifact.expired = $true }
            'missing digest' { $artifact.digest = $null }
            'wrong name' { $artifact.name = 'delivery-zip' }
            'wrong run' { $artifact.workflow_run.id = 1002 }
            'wrong repository' { $artifact.workflow_run.repository_id = 43 }
            'wrong head repository' { $artifact.workflow_run.head_repository_id = 43 }
            'wrong SHA' { $artifact.workflow_run.head_sha = 'c' * 40 }
        }
        Test-Case "artifact rejected: $variant" { Assert-That (-not (Select-Artifact $goodRun @($artifact))) 'Untrusted artifact was accepted.' }
    }
    $newer = Copy-Data $goodArtifact
    $newer.id = 2002
    $newer.expired = $true
    Test-Case 'ignore newer expired artifact' { Assert-That ((Select-Artifact $goodRun @($goodArtifact, $newer)).id -eq 2001) 'Expired artifact displaced the valid one.' }

    foreach ($case in @(
        @{ Name = 'main release reuse'; Event = 'push'; Ref = 'refs/heads/main'; Publish = $true; Found = $true; Plan = 'reuse' },
        @{ Name = 'main d=0 skip'; Event = 'push'; Ref = 'refs/heads/main'; Publish = $false; Found = $true; Plan = 'skip' },
        @{ Name = 'main cache miss build'; Event = 'push'; Ref = 'refs/heads/main'; Publish = $true; Found = $false; Plan = 'build' },
        @{ Name = 'formal tag reuse'; Event = 'push'; Ref = 'refs/tags/v' + $version; Publish = $true; Found = $true; Plan = 'reuse' },
        @{ Name = 'formal tag cache miss build'; Event = 'push'; Ref = 'refs/tags/v' + $version; Publish = $true; Found = $false; Plan = 'build' },
        @{ Name = 'PR always builds'; Event = 'pull_request'; Ref = 'refs/pull/1/merge'; Publish = $false; Found = $true; Plan = 'build' },
        @{ Name = 'dispatch always builds'; Event = 'workflow_dispatch'; Ref = 'refs/heads/main'; Publish = $true; Found = $true; Plan = 'build' }
    )) {
        Test-Case $case.Name { Assert-That ((Get-CiCompilePlan $case.Event $case.Ref $case.Publish $case.Found) -ceq $case.Plan) 'Incorrect compile plan.' }
    }
    Test-Case 'download disappearance falls back to compilation' { Assert-That (Test-CiCompilationNeeded 'reuse' $false) 'Cache download miss must compile.' }
    Test-Case 'verified restore skips compilation' { Assert-That (-not (Test-CiCompilationNeeded 'reuse' $true)) 'Verified restore must not compile.' }
    Test-Case 'main d=0 skip needs no download or compilation' { Assert-That (-not (Test-CiCompilationNeeded 'skip' $false)) 'Skip mode must not compile.' }
    Test-Case 'PR/dispatch skipped lookup still compiles' { Assert-That (Test-CiCompilationNeeded '' $false) 'Missing lookup outputs must compile.' }

    New-Fixture
    Test-Case 'intact unsigned payload accepted' { Test-Payload }
    $archive = Join-Path $work 'payload.zip'
    # Match Actions artifact ZIPs: .NET Framework CreateFromDirectory emits Windows backslashes.
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    $prefix = (Get-Item -LiteralPath $payload).FullName.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    try {
        foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File) {
            $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative)
        }
    } finally { $zip.Dispose() }
    $archiveSha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Test-Case 'download archive SHA-256 accepted' { Assert-CiFileHash $archive $archiveSha 'archive fixture' }
    Test-Case 'download archive SHA-256 mismatch rejected' { Assert-CiFileHash $archive ('0' * 64) 'archive fixture' } 'SHA-256 mismatch'
    Test-Case 'safe archive extracts and validates' {
        $expanded = Join-Path $work 'expanded'
        Expand-CiCompileArchive $archive $expanded
        $null = Assert-CiCompileArtifact -Directory $expanded -SourceSha $sha -Repository $repository -Identity $identity
    }
    foreach ($field in @('sourceSha', 'sourceRepository', 'version')) {
        New-Fixture
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        $metadata.$field = 'WRONG'
        Write-Metadata $metadata
        Test-Case "payload rejected: wrong $field" { Test-Payload } 'provenance or version differs'
    }
    New-Fixture
    [IO.File]::AppendAllText((Join-Path $payload 'dependencies/framework.msix'), 'TAMPERED')
    Test-Case 'framework file hash mismatch rejected' { Test-Payload } 'SHA-256 mismatch'
    New-Fixture
    Remove-Item -LiteralPath (Join-Path $payload $packageName)
    Test-Case 'missing package rejected' { Test-Payload } 'Missing file'
    New-Fixture
    [IO.File]::WriteAllText((Join-Path $payload 'unexpected.txt'), 'UNLISTED')
    Test-Case 'unlisted payload file rejected' { Test-Payload } 'unlisted files'
    New-Fixture
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $metadata.files[1].relativePath = '../outside.msix'
    Write-Metadata $metadata
    Test-Case 'unsafe inventory path rejected' { Test-Payload } 'unsafe relative path'
    foreach ($field in @('Name', 'Publisher', 'Version', 'Architecture')) {
        $wrongIdentity = Copy-Data $identity
        $wrongIdentity.$field = 'WRONG'
        New-Fixture $wrongIdentity
        Test-Case "MSIX identity rejected: wrong $field" { Test-Payload } "Compiled MSIX $field differs"
    }
    New-Fixture -Signed
    Test-Case 'previously signed MSIX cannot be reused' { Test-Payload } 'unsigned MSIX'
    $unsafeArchive = Join-Path $work 'unsafe.zip'
    $zip = [IO.Compression.ZipFile]::Open($unsafeArchive, [IO.Compression.ZipArchiveMode]::Create)
    try { $null = $zip.CreateEntry('../outside.txt') } finally { $zip.Dispose() }
    Test-Case 'unsafe archive path rejected before extraction' { Expand-CiCompileArchive $unsafeArchive (Join-Path $work 'unsafe') } 'unsafe extraction path'
    Write-Host "PASS: $script:count offline compile-artifact scenarios; no network, signing, or app deployment"
} finally { Remove-Item -LiteralPath $work -Recurse -Force }
