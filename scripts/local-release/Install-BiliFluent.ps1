[CmdletBinding()]
param()
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# BiliFluent 一键安装向导（由同目录的“安装 BiliFluent.cmd”调用）。
# 步骤：检查环境 → 检查证书信任 →（如需要）经 UAC 信任证书 → 为当前用户安装 MSIX。
# 实际的校验、证书信任与部署全部由 Install.ps1（及其提权助手 Trust-Certificate.ps1）完成，本脚本只负责引导和提示。

# 打包时由 Make-Delivery.ps1 写入交付清单 release-artifacts.json 的 SHA-256。
$EmbeddedManifestSha256 = '__MANIFEST_SHA256__'
$root = $PSScriptRoot

function Write-Step([string]$Text) { Write-Host ''; Write-Host ('==> ' + $Text) -ForegroundColor Cyan }
function Write-Info([string]$Text) { Write-Host ('    ' + $Text) }
function Write-Ok([string]$Text) { Write-Host ('    [OK] ' + $Text) -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Host ('    [!] ' + $Text) -ForegroundColor Yellow }
function Write-Fail([string]$Text) { Write-Host ('    [X] ' + $Text) -ForegroundColor Red }

function Get-FileSha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($Path)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Test-Elevated {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    try { return (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
}

function Test-CertificateTrusted([string]$CerPath) {
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (, [System.IO.File]::ReadAllBytes($CerPath))
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'LocalMachine')
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        foreach ($candidate in $store.Certificates) { if ($candidate.Thumbprint -ceq $cert.Thumbprint) { return $true } }
        return $false
    } finally { $store.Close(); $cert.Reset() }
}

function Invoke-Wizard {
    Write-Host '============================================================'
    Write-Host '                 BiliFluent 安装向导'
    Write-Host '============================================================'
    Write-Host '本向导将为当前 Windows 用户安装 BiliFluent。整个过程会：'
    Write-Host '  1. 校验安装包、证书和脚本的完整性（SHA-256）；'
    Write-Host '  2. 如本机尚未信任 BiliFluent 的签名证书，弹出一次 UAC（管理员）确认，'
    Write-Host '     仅把公钥证书加入“本地计算机\受信任的人”(TrustedPeople)；'
    Write-Host '  3. 以普通用户身份安装应用（不需要管理员权限）。'
    Write-Host '脚本不会修改系统策略、不会卸载任何软件，也不会自动启动应用。'

    Write-Step '步骤 1/4：检查运行环境'
    if (Test-Elevated) {
        Write-Fail '检测到当前以管理员身份运行。'
        Write-Info '请关闭此窗口，直接双击“安装 BiliFluent.cmd”（不要选择“以管理员身份运行”）。'
        Write-Info '需要管理员权限的步骤会单独弹出 UAC 确认。'
        return 1
    }
    if (-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess) {
        Write-Fail 'BiliFluent 仅支持 64 位 (x64) Windows，并需在 64 位 PowerShell 中运行。'
        return 1
    }
    $installScript = Join-Path $root 'Install.ps1'
    $manifest = Join-Path $root 'release-artifacts.json'
    $missing = @(@($installScript, $manifest, (Join-Path $root 'Trust-Certificate.ps1')) | Where-Object { -not (Test-Path -LiteralPath $_) })
    if ($missing.Count -gt 0) {
        Write-Fail ('缺少文件: ' + (($missing | ForEach-Object { Split-Path $_ -Leaf }) -join ', '))
        Write-Info '请先把整个 ZIP 压缩包“全部解压”到一个普通文件夹（不要在压缩包内直接运行），再重新双击安装。'
        return 1
    }
    $data = (Get-Content -LiteralPath $manifest -Raw -Encoding UTF8) | ConvertFrom-Json
    $cerPath = Join-Path $root ([string]$data.certificate.relativePath)
    if (-not (Test-Path -LiteralPath $cerPath)) { Write-Fail '缺少证书文件 payload\BiliFluent.cer，请重新下载并完整解压。'; return 1 }
    Write-Ok ('Windows PowerShell {0}，x64。待安装版本：{1}' -f $PSVersionTable.PSVersion, $data.package.version)

    if ($EmbeddedManifestSha256 -cmatch '^[0-9a-f]{64}$') {
        $manifestSha = $EmbeddedManifestSha256
    } else {
        Write-Warn '安装器未内置清单摘要（开发版本），改为使用当前 release-artifacts.json 的 SHA-256。'
        $manifestSha = Get-FileSha256 $manifest
    }

    Write-Step '步骤 2/4：检查签名证书是否已受信任'
    $trusted = $false
    try { $trusted = Test-CertificateTrusted $cerPath } catch { Write-Warn ('无法读取证书存储：' + $_.Exception.Message) }
    if ($trusted) {
        Write-Ok '本机已信任 BiliFluent 证书，无需管理员权限。'
    } else {
        Write-Info ('本机尚未信任 BiliFluent 证书（指纹 {0}）。' -f $data.certificate.thumbprint)
        Write-Info '接下来会弹出 Windows 的 UAC 窗口，请点击“是”以信任该证书；点击“否”将取消安装且不做任何更改。'
        Write-Info '提示：受信任后，本机会信任由该证书签名的所有应用包，请只信任来自官方发布页的安装包。'
    }

    Write-Step '步骤 3/4：校验并安装（由 Install.ps1 完成）'
    # Install.ps1 支持被点源加载（只定义函数、不自动执行），这里直接调用其安装入口以拿到结构化结果。
    # 注意：点源会引入 Install.ps1 的参数变量（$Install、$ExpectedManifestSha256），因此本脚本不使用这些变量名。
    . $installScript
    $result = Invoke-ReleaseInstaller -DeliveryRoot $root -InstallRequested $true -ExpectedManifestSha256 $manifestSha -InstallScriptPath $installScript
    $code = Get-InstallerExitCode $result
    $failure = [string]$result.failure
    $hresult = [string]$result.hresult
    Write-Info ('Install.ps1 结果：{0}' -f $result.outcome)
    if ($failure) { Write-Info ('原因：' + $failure) }

    Write-Step '步骤 4/4：结果'
    if ($result.outcome -ceq 'AlreadyPresent') {
        Write-Ok ('已是最新版本：BiliFluent {0} 已安装在当前用户下，无需重复安装。' -f $data.package.version)
        Write-Info '可在“开始”菜单中搜索 BiliFluent 启动。'
        return 0
    }
    if ($result.outcome -ceq 'Installed') {
        Write-Ok ('安装成功：BiliFluent {0}。' -f $data.package.version)
        Write-Info '请在“开始”菜单中搜索 BiliFluent 启动应用。'
        return 0
    }
    if ($hresult -ieq '0x80073CFB' -or $failure -match '(?i)0x80073CFB' -or $failure.Contains('同版本已安装包')) {
        Write-Ok ('已是最新版本：当前用户已安装版本号相同的 BiliFluent {0}（可能来自另一次构建，例如预发布版）。' -f $data.package.version)
        Write-Info '如果应用能正常使用，可直接关闭本窗口。'
        Write-Info '如确需改装此构建：先在“设置 > 应用 > 已安装的应用”中卸载 BiliFluent，再重新双击安装。'
        return 0
    }
    if ($code -eq 3) {
        Write-Warn '你取消了 UAC 确认，证书未被信任，应用未安装，系统没有任何更改。'
        Write-Info '如需安装，请重新双击“安装 BiliFluent.cmd”，并在 UAC 窗口中点击“是”。'
        return 3
    }
    Write-Fail ('安装未完成（{0}，退出码 {1}）。' -f $result.outcome, $code)
    if ($result.outcome -ceq 'PartialFailure') { Write-Info '证书可能已被信任，但应用未成功安装；修复原因后重新双击安装即可（会自动跳过已完成的步骤）。' }
    Write-Info '常见原因与处理：'
    Write-Info '  * 已安装较高版本：不支持降级，请使用更新的安装包或先卸载。'
    Write-Info '  * SHA-256 不匹配 / 缺少文件：安装包被修改或未完整解压，请从发布页重新下载并“全部解压”。'
    Write-Info '  * 重解析点/云同步：请把文件夹复制到本地磁盘（例如“下载”或桌面），不要放在 OneDrive 占位目录中。'
    Write-Info '  * 旁加载被组策略禁止：请联系管理员开启“允许安装受信任的应用”。'
    Write-Info '如问题仍然存在，请把本窗口的全部输出附在 Issue 中：https://github.com/Better-Native-APPs/BiliFluent/issues'
    return $code
}

$exit = 1
try { $exit = Invoke-Wizard }
catch {
    Write-Fail ('安装向导出错：' + $_.Exception.Message)
    Write-Info '请把本窗口的全部输出附在 Issue 中：https://github.com/Better-Native-APPs/BiliFluent/issues'
    $exit = 1
}
exit $exit
