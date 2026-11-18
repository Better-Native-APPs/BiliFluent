[CmdletBinding()]
param(
    [Parameter(Mandatory)] [long]$ArtifactId,
    [Parameter(Mandatory)] [string]$ArchiveSha256,
    [Parameter(Mandatory)] [string]$Version,
    [string]$OutputDirectory
)
# Missing/deleted artifacts are cache misses; integrity/provenance failures stop the release.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CompileArtifact.ps1')
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/compiled' }
$identity = Get-CiSourceIdentity -ManifestPath (Join-Path $root 'src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Version $Version
$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$work = Join-Path $temp ('compile-download-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$archive = Join-Path $work 'artifact.zip'

function Receive-Archive([string]$Path) {
    # Follow GitHub's signed storage redirect with a separate anonymous client: never forward GH_TOKEN.
    Add-Type -AssemblyName System.Net.Http
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $storage = [System.Net.Http.HttpClient]::new()
    $response = $null
    try {
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('BiliFluent-CI')
        foreach ($header in (Get-CiApiHeaders).GetEnumerator()) { [void]$client.DefaultRequestHeaders.TryAddWithoutValidation($header.Key, $header.Value) }
        $api = if ($env:GITHUB_API_URL) { $env:GITHUB_API_URL.TrimEnd('/') } else { 'https://api.github.com' }
        $uri = "$api/repos/$($env:GITHUB_REPOSITORY)/actions/artifacts/$ArtifactId/zip"
        $response = $client.GetAsync($uri, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400) {
            $location = $response.Headers.Location
            if (-not $location -or -not $location.IsAbsoluteUri -or $location.Scheme -cne 'https') { throw 'Artifact download redirect must be an absolute HTTPS URL.' }
            $response.Dispose()
            $response = $storage.GetAsync($location, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        }
        $null = $response.EnsureSuccessStatusCode()
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $file = [IO.File]::Create($Path)
        try { $stream.CopyTo($file) } finally { $file.Dispose(); $stream.Dispose() }
    } finally {
        if ($response) { $response.Dispose() }
        $storage.Dispose(); $client.Dispose(); $handler.Dispose()
    }
}

try {
    try { Receive-Archive $archive }
    catch {
        Write-Warning 'Compile artifact download is unavailable; selecting a full compilation.'
        Set-CiOutput 'reused' 'false'
        return
    }
    Assert-CiFileHash -Path $archive -Sha256 $ArchiveSha256 -Label 'downloaded compile archive'
    $payload = Join-Path $work 'payload'
    Expand-CiCompileArchive -ArchivePath $archive -Directory $payload
    $null = Assert-CiCompileArtifact -Directory $payload -SourceSha $env:GITHUB_SHA -Repository $env:GITHUB_REPOSITORY -Identity $identity
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Compile artifact destination already exists. Use a clean output directory.' }
    [void][IO.Directory]::CreateDirectory((Split-Path $OutputDirectory -Parent))
    Move-Item -LiteralPath $payload -Destination $OutputDirectory
    Set-CiOutput 'reused' 'true'
    Write-Host "Verified compile artifact $ArtifactId for $Version ($($env:GITHUB_SHA))."
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
