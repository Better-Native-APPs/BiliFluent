[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Title,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string[]]$Assets,
    [string]$ManifestSha256,
    [switch]$Prerelease,
    [int]$KeepPrereleases = 3
)
# CI: creates or updates the GitHub release for $Tag with the delivery ZIP + public .cer, then prunes old pre-releases.
# Requires the gh CLI with GH_TOKEN (contents: write) and GITHUB_REPOSITORY / GITHUB_SHA.
$ErrorActionPreference = 'Stop'
$repo = $env:GITHUB_REPOSITORY
if (-not $repo) { throw 'GITHUB_REPOSITORY is not set.' }
foreach ($asset in $Assets) { if (-not (Test-Path -LiteralPath $asset)) { throw "Release asset not found: $asset" } }

function Invoke-Gh([string[]]$GhArgs) {
    & gh @GhArgs
    if ($LASTEXITCODE) { throw "gh $($GhArgs -join ' ') failed ($LASTEXITCODE)" }
}

$zipName = Split-Path ($Assets | Where-Object { $_ -like '*.zip' } | Select-Object -First 1) -Leaf
$kind = if ($Prerelease) { '> [!WARNING]' + "`n" + '> 这是由 `main` 分支自动构建的**预发布版**，可能不稳定。正式版请使用标记为 Latest 的 Release。' + "`n`n" } else { '' }
$notes = @"
$kind## 安装方法

1. 下载 ``$zipName``，右键 →「全部解压」到一个普通文件夹（不要在压缩包里直接运行）。
2. 双击 **``安装 BiliFluent.cmd``**，按窗口中的中文提示操作。
   - 首次安装会弹出一次 UAC（管理员）确认，用于把 BiliFluent 的公钥证书加入「受信任的人」；
   - 应用本身以当前用户身份安装，不需要管理员权限。
3. 安装完成后，在「开始」菜单搜索 **BiliFluent** 启动。

> 若提示“已是最新版本”，说明相同版本号（$Version）已安装。签名证书 ``BiliFluent.cer`` 也单独附在本页，可用于手动核对指纹。
"@
if ($ManifestSha256) { $notes += "`n`n``release-artifacts.json`` SHA-256: ``$ManifestSha256``" }
$notesFile = Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }) 'release-notes.md'
[IO.File]::WriteAllText($notesFile, $notes, (New-Object System.Text.UTF8Encoding($false)))

& gh release view $Tag --repo $repo --json tagName 1>$null 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Release $Tag exists; updating assets and metadata."
    Invoke-Gh (@('release', 'upload', $Tag) + $Assets + @('--repo', $repo, '--clobber'))
    Invoke-Gh @('release', 'edit', $Tag, '--repo', $repo, '--title', $Title, ('--prerelease=' + $Prerelease.IsPresent.ToString().ToLowerInvariant()))
} else {
    $createArgs = @('release', 'create', $Tag) + $Assets + @('--repo', $repo, '--title', $Title, '--notes-file', $notesFile, '--generate-notes')
    if ($Prerelease) { $createArgs += @('--target', $env:GITHUB_SHA, '--prerelease', '--latest=false') }
    else { $createArgs += @('--verify-tag', '--latest') }
    Invoke-Gh $createArgs
}

if ($Prerelease) {
    $json = & gh release list --repo $repo --limit 200 --json tagName,isPrerelease,createdAt
    if ($LASTEXITCODE) { throw 'gh release list failed.' }
    $old = @($json | ConvertFrom-Json | Where-Object { $_.isPrerelease -and $_.tagName -match '^v\d+(\.\d+){3}-pre\.\d+$' } |
            Sort-Object { [DateTime]$_.createdAt } -Descending | Select-Object -Skip $KeepPrereleases)
    foreach ($release in $old) {
        Write-Host "Pruning old pre-release $($release.tagName)"
        Invoke-Gh @('release', 'delete', $release.tagName, '--repo', $repo, '--yes', '--cleanup-tag')
    }
}
Write-Host "Published $Tag"
