[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$CertificateThumbprint,
    [string]$CompileDirectory,
    [string]$OutputDirectory,
    [string]$SignToolPath
)
# Both fresh and reused builds pass through this same signing and final-identity verification path.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CompileArtifact.ps1')
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $CompileDirectory) { $CompileDirectory = Join-Path $root 'artifacts/compiled' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/release' }
$identity = Get-CiSourceIdentity -ManifestPath (Join-Path $root 'src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Version $Version
$metadata = Assert-CiCompileArtifact -Directory $CompileDirectory -SourceSha $env:GITHUB_SHA -Repository $env:GITHUB_REPOSITORY -Identity $identity
$thumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
$cert = Get-Item "Cert:/CurrentUser/My/$thumbprint"
if (-not $cert.HasPrivateKey -or $cert.Subject -cne $identity.Publisher -or (Get-Date) -lt $cert.NotBefore -or (Get-Date) -gt $cert.NotAfter) {
    throw 'Release signing certificate must have a private key, be currently valid, and match the manifest Publisher.'
}
if (-not $SignToolPath) {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { $SignToolPath = $command.Source }
    else {
        $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
        $versions = @(Get-ChildItem -LiteralPath $sdk -Directory | Where-Object { $_.Name -match '^\d+(\.\d+){3}$' } | Sort-Object { [version]$_.Name } -Descending)
        foreach ($directory in $versions) {
            $candidate = Join-Path $directory.FullName 'x64/signtool.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $SignToolPath = $candidate; break }
        }
    }
}
if (-not $SignToolPath -or -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) { throw 'Windows SDK SignTool is required; pass -SignToolPath or install the x64 signing tools.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
foreach ($file in $metadata.files) {
    $destination = Join-Path $OutputDirectory $file.relativePath
    [void][IO.Directory]::CreateDirectory((Split-Path $destination -Parent))
    Copy-Item -LiteralPath (Join-Path $CompileDirectory $file.relativePath) -Destination $destination -Force
}
$packageName = [string]$metadata.package
$packagePath = Join-Path $OutputDirectory $packageName
Push-Location $OutputDirectory
try {
    & $SignToolPath sign /fd SHA256 /s My /sha1 $thumbprint $packageName 1> sign.log 2>&1
    if ($LASTEXITCODE) { throw "SignTool sign failed ($LASTEXITCODE). See artifacts/release/sign.log." }

    # Trust only this public certificate temporarily on the disposable runner, then require platform verification.
    $publicCert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
    $added = $false
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        if ($store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $thumbprint, $false).Count -eq 0) {
            $store.Add($publicCert)
            $added = $true
        }
        & $SignToolPath verify /pa $packageName 1> sign-verify.log 2>&1
        if ($LASTEXITCODE) { throw "SignTool verify failed ($LASTEXITCODE). See artifacts/release/sign-verify.log." }
        $signature = Get-AuthenticodeSignature -LiteralPath $packageName
        if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -cne $thumbprint -or $signature.SignerCertificate.Subject -cne $identity.Publisher) {
            throw 'Signed MSIX signature is not Valid or its signer differs from the release certificate/manifest Publisher.'
        }
    } finally {
        if ($added) { $store.Remove($publicCert) }
        $store.Close(); $publicCert.Dispose()
    }
} finally { Pop-Location }
Assert-CiPackageIdentity -PackagePath $packagePath -Expected $identity
$sha = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{ package = $packageName; version = $Version; sourceSha = $env:GITHUB_SHA; sha256 = $sha; signerThumbprint = $thumbprint; signature = 'Valid' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'signed-package.json') -Encoding UTF8
Write-Host "Signed and verified $packageName (SHA-256 $sha)."
