[CmdletBinding()]
param(
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent),
    [Parameter(Mandatory = $true)][string]$WorkRoot
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
Import-Module (Join-Path $SourceRoot 'ReleaseMetadata.psm1') -Force
$repo = [IO.Path]::GetFullPath((Join-Path $SourceRoot '../..'))
$version = Get-ReleasePackageVersion
$runtimeVersion = Get-ReleaseRuntimeVersion
$overrideVersion = '{0}.{1}.{2}.{3}' -f ([version]$version).Major, ([version]$version).Minor, ([version]$version).Build, (([version]$version).Revision + 1)
if (Test-Path -LiteralPath $WorkRoot) { throw 'Use a new, empty fixture work directory.' }
[void][IO.Directory]::CreateDirectory($WorkRoot)
$fixture = Join-Path $WorkRoot 'repository'
$utf8 = New-Object Text.UTF8Encoding($false)
$script:Passes = 0
function Assert-That([bool]$Condition, [string]$Message) { if (-not $Condition) { throw ('ASSERT: ' + $Message) } }
function Get-Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Save-Json([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), $utf8) }
function Copy-FixtureFile([string]$Relative) {
    $destination = Join-Path $fixture $Relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    [IO.File]::Copy((Join-Path $repo $Relative), $destination)
}
function New-Zip([string]$Path, [hashtable]$Entries) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            $stream = $zip.CreateEntry($name).Open()
            try { $bytes = $utf8.GetBytes([string]$Entries[$name]); $stream.Write($bytes, 0, $bytes.Length) }
            finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
}
function Pass([string]$Message) { $script:Passes++; Write-Output ('PASS: ' + $Message) }

foreach ($relative in @(
    'src/Desktop/BiliCopilot.UI/Package.appxmanifest', 'src/Directory.Packages.props', 'scripts/native-assets.json',
    'scripts/ci/Provision-NativeAssets.ps1', 'scripts/local-test/Prepare-Package.ps1', 'scripts/local-test/Seal-Package.ps1',
    'scripts/local-test/LocalTest.psm1', 'scripts/local-test/channel.json', 'scripts/local-test/Install.ps1',
    'scripts/local-release/Make-Delivery.ps1', 'scripts/local-release/Copy-ReleaseDependencies.ps1',
    'scripts/local-release/ReleaseMetadata.psm1', 'scripts/local-release/Install.ps1', 'scripts/local-release/Trust-Certificate.ps1',
    'scripts/local-release/Install-BiliFluent.ps1', 'scripts/local-release/release-artifacts.json'
)) { Copy-FixtureFile $relative }
$cmdName = [string]::Concat([char]0x5B89, [char]0x88C5, ' BiliFluent.cmd')
Copy-FixtureFile ('scripts/local-release/' + $cmdName)

# Exercise real version, inventory, manifest and hashing code. Native PE and platform signatures are fixture seams only.
$lt = Join-Path $fixture 'scripts/local-test'
$modulePath = Join-Path $lt 'LocalTest.psm1'
$seams = @'
function Assert-LTNativePe([IO.Stream]$Stream) { }
function Assert-LTPeVersion([string]$Path, [string]$Version) { }
function Assert-LTSignature([string]$Path, [string]$SignerSha256) { }
'@
[IO.File]::WriteAllText($modulePath, ([IO.File]::ReadAllText($modulePath).Replace('Export-ModuleMember', ($seams + "`nExport-ModuleMember"))), $utf8)
Import-Module $modulePath -Force
$channel = Get-LTChannel
Assert-That ((Get-LTDefaultVersion) -ceq $version) 'LocalTest default application version'
[xml]$packages = Get-Content (Join-Path $repo 'src/Directory.Packages.props') -Raw -Encoding UTF8
$sdkVersion = $packages.SelectSingleNode('/Project/ItemGroup/PackageVersion[@Include="Microsoft.WindowsAppSDK"]').GetAttribute('Version')
Assert-That ($channel.windowsAppSdkVersion -ceq $sdkVersion) 'LocalTest SDK metadata'
Assert-LTVersion $channel.minimumVersion
$minimum = [version]$channel.minimumVersion
$belowMinimum = '{0}.{1}.{2}.0' -f $minimum.Major, $minimum.Minor, ($minimum.Build - 1)
try { Assert-LTVersion $belowMinimum; throw 'Expected minimum version rejection.' }
catch { Assert-That ($_.Exception.Message.Contains("starts at $($channel.minimumVersion)")) 'configured minimum in error message' }
Pass 'LocalTest defaults, SDK source and historical minimum'

foreach ($selectedVersion in @($version, $overrideVersion)) {
    $source = Join-Path $WorkRoot ('layout-' + $selectedVersion)
    $output = Join-Path $WorkRoot ('local-test-' + $selectedVersion)
    [void][IO.Directory]::CreateDirectory($source)
    [xml]$manifest = Get-Content (Join-Path $repo 'src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Raw -Encoding UTF8
    $nodes = Get-LTManifestNodes $manifest
    $nodes.Identity.SetAttribute('Version', $selectedVersion)
    $nodes.Identity.SetAttribute('ProcessorArchitecture', 'x64')
    $nodes.App.SetAttribute('Executable', 'BiliCopilot.UI.exe')
    $nodes.App.SetAttribute('EntryPoint', 'Windows.FullTrustApplication')
    foreach ($resource in $manifest.Package.Resources.Resource) { $resource.SetAttribute('Language', 'en-US') }
    $manifest.Save((Join-Path $source 'AppxManifest.xml'))
    [IO.File]::WriteAllText((Join-Path $source 'BiliCopilot.UI.exe'), 'INERT EXE', $utf8)
    $files = @(Get-ChildItem -LiteralPath $source -File | ForEach-Object { [ordered]@{ path = $_.Name; sha256 = Get-Sha $_.FullName } })
    $inventory = Join-Path $WorkRoot ('inventory-' + $selectedVersion + '.json')
    Save-Json $inventory ([ordered]@{ schemaVersion = 1; files = $files })
    $arguments = @{ SourceLayout = $source; OutputRoot = $output; WorkspaceRoot = $fixture; ApprovedInventory = $inventory; ExpectedInventorySha256 = (Get-Sha $inventory); Prepare = $true }
    if ($selectedVersion -cne $version) { $arguments.Version = $selectedVersion }
    $prepared = & (Join-Path $lt 'Prepare-Package.ps1') @arguments
    Assert-That ($prepared.Version -ceq $selectedVersion) 'Prepare version selection'
    $delivery = Join-Path $output 'delivery'
    $config = Get-Content (Join-Path $delivery 'channel.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-That ($config.windowsAppSdkVersion -ceq $sdkVersion) 'self-contained SDK metadata'
    New-Zip $prepared.Package @{ 'AppxManifest.xml' = [IO.File]::ReadAllText((Join-Path $prepared.Layout 'AppxManifest.xml')); 'BiliCopilot.UI.exe' = 'INERT EXE'; 'AppxSignature.p7x' = 'INERT SIGNATURE' }
    $seal = @{ DeliveryRoot = $delivery; LooseExePath = (Join-Path $source 'BiliCopilot.UI.exe'); ExpectedLooseExeSha256 = (Get-Sha (Join-Path $source 'BiliCopilot.UI.exe')); ExpectedSignerCertificateSha256 = ('0' * 64) }
    if ($selectedVersion -cne $version) { $seal.Version = $selectedVersion }
    $null = & (Join-Path $lt 'Seal-Package.ps1') @seal
    $sealed = Get-Content (Join-Path $delivery 'test-artifacts.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-That ($sealed.package.version -ceq $selectedVersion -and $sealed.package.file -ceq "BiliFluent.LocalTest_${selectedVersion}_x64.msix") 'Seal version and file name'
    # No source tree is present here; installer-side channel reads must remain self-contained.
    Import-Module (Join-Path $delivery 'LocalTest.psm1') -Force
    Assert-That ((Get-LTChannel).windowsAppSdkVersion -ceq $sdkVersion) 'portable channel metadata'
    Pass ('Prepare/Seal ' + $(if ($selectedVersion -ceq $version) { 'manifest default' } else { 'explicit override' }))
}

$release = Join-Path $fixture 'scripts/local-release'
Import-Module (Join-Path $release 'ReleaseMetadata.psm1') -Force
$pin = (Get-ReleaseArtifactsTemplate).dependencies[0]
$releaseOutput = Join-Path $WorkRoot 'release'
$dependencies = Join-Path $releaseOutput 'dependencies'
[void][IO.Directory]::CreateDirectory($dependencies)
$runtimePath = Join-Path $dependencies ([IO.Path]::GetFileName($pin.relativePath))
$foundation = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
New-Zip $runtimePath @{ 'AppxManifest.xml' = "<Package xmlns='$foundation'><Identity Name='$($pin.name)' Publisher='$($pin.publisher)' Version='$runtimeVersion' ProcessorArchitecture='x64'/><Properties><Framework>true</Framework></Properties></Package>" }
$templatePath = Join-Path $release 'release-artifacts.json'
$template = Get-Content $templatePath -Raw -Encoding UTF8 | ConvertFrom-Json
$template.dependencies[0].sha256 = Get-Sha $runtimePath
Save-Json $templatePath $template
$certificate = [pscustomobject]@{ Subject = 'CN=BiliFluent'; RawData = $utf8.GetBytes('INERT CERTIFICATE'); Thumbprint = ('0' * 40); NotBefore = [DateTime]::UtcNow.AddDays(-1); NotAfter = [DateTime]::UtcNow.AddDays(1) }
function Get-Item { if ($args[0] -like 'Cert:/*') { return $certificate }; Microsoft.PowerShell.Management\Get-Item @args }
function Get-AuthenticodeSignature { [pscustomobject]@{ Status = 'Valid' } }
foreach ($selectedVersion in @($version, $overrideVersion)) {
    $appPath = Join-Path $releaseOutput "BiliFluent_${selectedVersion}_x64.msix"
    New-Zip $appPath @{ 'AppxManifest.xml' = "<Package xmlns='$foundation'><Identity Name='BiliFluent' Publisher='CN=BiliFluent' Version='$selectedVersion' ProcessorArchitecture='x64'/><Dependencies><PackageDependency Name='$($pin.name)' Publisher='$($pin.publisher)' MinVersion='$runtimeVersion'/></Dependencies></Package>" }
    $arguments = @{ OutputDirectory = $releaseOutput }
    if ($selectedVersion -cne $version) { $arguments.Version = $selectedVersion }
    & (Join-Path $release 'Make-Delivery.ps1') @arguments *> $null
    $delivery = Join-Path $releaseOutput "BiliFluent-$selectedVersion"
    $result = Get-Content (Join-Path $delivery 'release-artifacts.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-That ($result.package.version -ceq $selectedVersion -and $result.dependencies[0].version -ceq $runtimeVersion) 'delivery version sources'
    Assert-ReleaseTemplateComplete ([IO.File]::ReadAllText((Join-Path $delivery 'release-artifacts.json')))
    Assert-That ((Test-Path -LiteralPath "$delivery.zip") -and (Get-Sha (Join-Path $delivery $pin.relativePath)) -ceq $template.dependencies[0].sha256) 'ZIP and pinned framework copy'
    Pass ('Make-Delivery/Copy-ReleaseDependencies ' + $(if ($selectedVersion -ceq $version) { 'manifest default' } else { 'explicit override' }))
}
[IO.File]::AppendAllText($runtimePath, 'TAMPERED')
try {
    $null = & (Join-Path $release 'Copy-ReleaseDependencies.ps1') -PackagePath $appPath -SourceDirectory $dependencies -DestinationDirectory (Join-Path $WorkRoot 'tampered-copy')
    throw 'Expected framework hash rejection.'
} catch { Assert-That ($_.Exception.Message -match 'Framework SHA-256 differs from the release pin') 'framework hash remains enforced' }
Pass 'Copy-ReleaseDependencies rejects a mismatched SHA-256'

# Stub only network transport, archive extraction and tracked-Visor discovery; provisioning computes real fixture hashes.
$nativePath = Join-Path $fixture 'scripts/native-assets.json'
$native = Get-Content $nativePath -Raw -Encoding UTF8 | ConvertFrom-Json
$asset = $native.downloads[0]
$archiveFixture = Join-Path $WorkRoot 'native-fixture.7z'
New-Zip $archiveFixture @{ $asset.fileName = 'INERT MPV' }
$asset.archiveSha256 = Get-Sha $archiveFixture
$fileFixture = Join-Path $WorkRoot $asset.fileName
[IO.File]::WriteAllText($fileFixture, 'INERT MPV', $utf8)
$asset.fileSha256 = Get-Sha $fileFixture
Save-Json $nativePath $native
$target = Join-Path (Join-Path $fixture 'src/Desktop/BiliCopilot.UI/Assets') $asset.targetPath
$visor = Join-Path $fixture 'src/Desktop/BiliCopilot.UI/Assets/visor/x64/BiliCopilot.Visor.exe'
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($visor))
[IO.File]::WriteAllText($visor, 'INERT VISOR', $utf8)
$extractor = Join-Path $WorkRoot 'Extract-Fixture.ps1'
[IO.File]::WriteAllText($extractor, 'param($Mode, $Archive, $Output) Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Output.Substring(2)); $global:LASTEXITCODE = 0', $utf8)
$global:FixtureNativeDownloads = 0
$global:FixtureNativeArchive = $archiveFixture
function Invoke-WebRequest { param($Uri, $OutFile, [switch]$UseBasicParsing, $MaximumRedirection) $global:FixtureNativeDownloads++; [IO.File]::Copy($global:FixtureNativeArchive, $OutFile) }
function git { 'tracked-visor' }
$oldRunnerTemp = $env:RUNNER_TEMP
$env:RUNNER_TEMP = Join-Path $WorkRoot 'native-work'
try {
    $provision = Join-Path $fixture 'scripts/ci/Provision-NativeAssets.ps1'
    & $provision -SevenZipPath $extractor *> $null
    Assert-That ((Get-Sha $target) -ceq $asset.fileSha256 -and $global:FixtureNativeDownloads -eq 1) 'metadata-driven provisioning'
    & $provision -SevenZipPath $extractor *> $null
    Assert-That ($global:FixtureNativeDownloads -eq 1) 'matching asset avoids network access'
    Pass 'Provision-NativeAssets downloads metadata and reuses a matching target'
    [IO.File]::Delete($target)
    $asset.archiveSha256 = '0' * 64; Save-Json $nativePath $native
    try { & $provision -SevenZipPath $extractor *> $null; throw 'Expected archive hash rejection.' }
    catch { Assert-That ($_.Exception.Message -match 'Archive SHA-256 mismatch') 'archive hash remains enforced' }
    Pass 'Provision-NativeAssets rejects an archive SHA-256 mismatch'
    $asset.archiveSha256 = Get-Sha $archiveFixture; $asset.fileSha256 = '0' * 64; Save-Json $nativePath $native
    try { & $provision -SevenZipPath $extractor *> $null; throw 'Expected extracted-file hash rejection.' }
    catch { Assert-That ($_.Exception.Message -match 'Extracted .* SHA-256 mismatch') 'extracted file hash remains enforced' }
    Pass 'Provision-NativeAssets rejects an extracted-file SHA-256 mismatch'
} finally { $env:RUNNER_TEMP = $oldRunnerTemp }
Write-Output ('OFFLINE_PASS=' + $script:Passes + ' OFFLINE_FAIL=0; native PE/signature/network/extractor seams are mocked; no install or launch.')
