[CmdletBinding()]
param(
    # Allow an ephemeral self-signed CN=BiliFluent certificate when signing secrets are absent (pull request builds only).
    [switch]$AllowEphemeral
)
# CI: imports the release signing certificate into Cert:\CurrentUser\My and emits its thumbprint.
# Inputs (environment): SIGNING_PFX_BASE64, SIGNING_PFX_PASSWORD, SIGNING_CERT_THUMBPRINT.
# Outputs (GITHUB_OUTPUT): thumbprint=<SHA-1>, ephemeral=true|false.
$ErrorActionPreference = 'Stop'
$subject = 'CN=BiliFluent'

function Set-StepOutput([string]$Name, [string]$Value) {
    if ($env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "$Name=$Value" -Encoding utf8 }
    Write-Host "$Name=$Value"
}

$hasSecrets = -not [string]::IsNullOrWhiteSpace($env:SIGNING_PFX_BASE64)
if (-not $hasSecrets) {
    if (-not $AllowEphemeral) { throw 'SIGNING_PFX_BASE64 is not set; signed builds require the signing secrets.' }
    Write-Warning 'Signing secrets are unavailable; creating an ephemeral self-signed CN=BiliFluent certificate (NOT for distribution).'
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddDays(30)
    Set-StepOutput 'thumbprint' $cert.Thumbprint
    Set-StepOutput 'ephemeral' 'true'
    return
}

if ([string]::IsNullOrWhiteSpace($env:SIGNING_CERT_THUMBPRINT)) { throw 'SIGNING_CERT_THUMBPRINT is not set.' }
$expected = ($env:SIGNING_CERT_THUMBPRINT -replace '\s', '').ToUpperInvariant()
$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$pfx = Join-Path $temp ('signing-' + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String(($env:SIGNING_PFX_BASE64 -replace '\s', '')))
    $password = ConvertTo-SecureString -String ([string]$env:SIGNING_PFX_PASSWORD) -AsPlainText -Force
    $imported = @(Import-PfxCertificate -FilePath $pfx -CertStoreLocation 'Cert:\CurrentUser\My' -Password $password)
    $cert = @($imported | Where-Object { $_.HasPrivateKey }) | Select-Object -First 1
    if (-not $cert) { throw 'The PFX did not contain a certificate with a private key.' }
    if ($cert.Thumbprint.ToUpperInvariant() -cne $expected) { throw "Imported certificate thumbprint $($cert.Thumbprint) does not match SIGNING_CERT_THUMBPRINT." }
    if ($cert.Subject -cne $subject) { throw "Signing certificate subject must be $subject (got $($cert.Subject))." }
    if ((Get-Date) -gt $cert.NotAfter) { throw "Signing certificate expired on $($cert.NotAfter)." }
    Set-StepOutput 'thumbprint' $cert.Thumbprint
    Set-StepOutput 'ephemeral' 'false'
} finally {
    if (Test-Path -LiteralPath $pfx) { Remove-Item -LiteralPath $pfx -Force }
}
