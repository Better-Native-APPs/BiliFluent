Set-StrictMode -Version 2

function Get-ReleasePackageVersion {
    [xml]$manifest = Get-Content (Join-Path $PSScriptRoot '../../src/Desktop/BiliCopilot.UI/Package.appxmanifest') -Raw -Encoding UTF8
    return [string]$manifest.Package.Identity.Version
}

function Get-ReleaseRuntimeVersion {
    [xml]$packages = Get-Content (Join-Path $PSScriptRoot '../../src/Directory.Packages.props') -Raw -Encoding UTF8
    $sdk = $packages.SelectSingleNode('/Project/ItemGroup/PackageVersion[@Include="Microsoft.WindowsAppSDK"]')
    if ($null -eq $sdk) { throw 'Microsoft.WindowsAppSDK must be pinned in src/Directory.Packages.props.' }
    $version = [version]$sdk.GetAttribute('Version')
    return ('{0}.{1}.{2}.{3}' -f $version.Major, $version.Minor, $version.Build, [Math]::Max(0, $version.Revision))
}

function Set-ReleaseScriptVersion([string]$Path, [string]$Version) {
    # Stamp only the fixed version line; additional version placeholders indicate a broken template.
    $text = [IO.File]::ReadAllText($Path)
    $pattern = "(?m)^(\s*Version\s*=\s*')__VERSION__(')"
    if ([regex]::Matches($text, $pattern).Count -ne 1) { throw "Expected exactly one pinned Version placeholder in $Path" }
    $text = [regex]::Replace($text, $pattern, ('${1}' + $Version + '${2}'))
    if ($text.Contains('__VERSION__')) { throw "Unresolved version placeholder in $Path" }
    [IO.File]::WriteAllText($Path, $text, (New-Object Text.UTF8Encoding($true)))
}

function Get-ReleaseArtifactsTemplate([string]$Version) {
    if (-not $Version) { $Version = Get-ReleasePackageVersion }
    $runtimeVersion = Get-ReleaseRuntimeVersion
    $runtimeName = 'Microsoft.WindowsAppRuntime.' + ([version]$runtimeVersion).Major
    $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'release-artifacts.json'))
    $text = $text.Replace('__VERSION__', $Version).Replace('__WINDOWS_APP_RUNTIME_VERSION__', $runtimeVersion).Replace('__WINDOWS_APP_RUNTIME_NAME__', $runtimeName)
    if ($text -cmatch '__(VERSION|WINDOWS_APP_RUNTIME_[A-Z_]+)__') { throw ('Unresolved release version placeholder: ' + $Matches[0]) }
    return ($text | ConvertFrom-Json)
}

function Assert-ReleaseTemplateComplete([string]$Text) {
    if ($Text -cmatch '__[A-Z][A-Z0-9_]*__') { throw ('Unresolved delivery manifest placeholder: ' + $Matches[0]) }
}

Export-ModuleMember -Function Get-ReleasePackageVersion, Get-ReleaseRuntimeVersion, Set-ReleaseScriptVersion, Get-ReleaseArtifactsTemplate, Assert-ReleaseTemplateComplete
