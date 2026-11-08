# BiliFluent 构建、签名与发布

BiliFluent 是采用 Fluent UI 与 WinUI 3 原生控件、专为 Windows 优化的哔哩哔哩第三方桌面客户端。无广告，注重低资源占用与流畅播放。
项目基于 Richasy 的「哔哩助理」([Bili Copilot](https://github.com/Richasy/Bili.Copilot)) 二次开发，遵循 [GPL-3.0 协议](../LICENSE)。

普通用户请直接从 [BiliFluent Releases](https://github.com/Better-Native-APPs/BiliFluent/releases) 下载交付 ZIP 包并按[安装指南](../docs/install.md)安装；本文档面向开发人员与维护者。

## 构建环境要求

- **操作系统**：Windows 10（Build 19041 及以上）或 Windows 11，x64 架构。
- **PowerShell**：Windows PowerShell 5.1 或 PowerShell 7+（构建、签名与本地包部署必须在原生 Windows 宿主环境下运行，请勿在 WSL Linux 环境中执行）。
- **.NET SDK**：必须使用 `global.json` 固定的 **.NET SDK 10.0.403**，严禁自动滚动到未经测试的更高版本。
- **构建工具链**：
  - Visual Studio 2022 / Build Tools（包含 .NET 桌面开发、通用 Windows 平台开发与 Windows App SDK C# / C++ 组件）。
  - Windows 10/11 SDK（包含 x64 版本的 `makeappx.exe` 与 `signtool.exe`，默认检测 `10.0.26100.0` 路径）。
  - NativeAOT C++ 编译环境（用于 Visor 原生二进制组件编译）。
- **网络访问**：可畅通访问 NuGet 源、GitHub Submodule 以及固定的原生外部资产下载源。

## 1. 获取源码与构建定制 SDK

```powershell
git clone --recurse-submodules https://github.com/Better-Native-APPs/BiliFluent.git
cd BiliFluent
.\scripts\Build-BiliKernel.ps1
```

对于已有仓库克隆，请执行 `git submodule update --init --recursive`。子模块 `src/Libs/BiliKernel` 指向定制分支 [Better-Native-APPs/BiliKernel](https://github.com/Better-Native-APPs/BiliKernel)。构建脚本编译 `Abstractions`、`Services.Comment`、`Services.Media` 与 `Services.Moment`，将 `0.0.3-bilicopilot.1` 包输出至本地 feed 目录 `artifacts/bilikernel-feed`，并通过 `nuget.config` 完成命名空间映射。

## 2. 准备固定的原生运行时依赖

CI 流程与发布脚本固定从 [shinchiro/mpv-winbuild-cmake 20261006 Release](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261006) 下载并核验以下原生资产。手工编译时请务必使用哈希完全一致的输入，严禁随意替换为其他版本。

### libmpv 运行时
- **归档名称**：`mpv-dev-x86_64-20261006-git-6c092d978b.7z`
- **归档 SHA-256**：`bc0e016bdf6573bada824873d4746ab58d9bfac0ddd59c046f160312f81f9548`
- **提取目标**：`libmpv-2.dll`
- **文件 SHA-256**：`d10d0994bd1398813da87fdb299961b378c71561bae1d135cb2b641b5864d7eb`
- **放置路径**：`src/Desktop/BiliCopilot.UI/Assets/libmpv/x64/libmpv-2.dll`
- **版本标识**：`v0.41.0-1102-g6c092d978`

### FFmpeg 运行时
- **归档名称**：`ffmpeg-x86_64-git-47313ad3f.7z`
- **归档 SHA-256**：`32f3ab53954901c6fd7a0fd811b0724ded3239306009af41b99b10094d91a25a`
- **提取目标**：`ffmpeg.exe`
- **文件 SHA-256**：`97cba10ab56e0ed7b3fc09004f1caedd8468924fd5fb39db126383a053da9c5e`
- **放置路径**：`src/Desktop/BiliCopilot.UI/Assets/ffmpeg/x64/ffmpeg.exe`
- **版本标识**：`N-127218-g47313ad3f`

### Visor 原生二进制
Visor 直接基于仓库源码通过 NativeAOT 编译，保证纯原生无外部运行时依赖：

```powershell
dotnet publish .\src\Desktop\BiliCopilot.Visor\BiliCopilot.Visor.csproj `
  -c Release -r win-x64 -p:PublishAot=true -o .\artifacts\visor
New-Item -ItemType Directory -Force .\src\Desktop\BiliCopilot.UI\Assets\visor\x64 | Out-Null
Copy-Item .\artifacts\visor\BiliCopilot.Visor.exe .\src\Desktop\BiliCopilot.UI\Assets\visor\x64\BiliCopilot.Visor.exe
```

构建脚本在打包前会按 `scripts/native-assets.json` 严格核验上述三个原生资产文件的存在性。

## 3. 构建与签名 MSIX

```powershell
.\scripts\Build-Release.ps1 -CertificateThumbprint <证书指纹>
```

产物生成至 `artifacts/release/BiliFluent_2.2611.7.0_x64.msix`。包名固定为 `BiliFluent`，发布者主体必须精确为 `CN=BiliFluent`。

正式签名证书指纹为：`6727300457DD924DAD363FDD27C5166E0E62CF32`。本地个人调试开发可使用 PowerShell 创建自签名测试证书进行签名：

```powershell
$cert = New-SelfSignedCertificate -Type Custom `
  -Subject 'CN=BiliFluent' -FriendlyName 'BiliFluent Test' `
  -KeyUsage DigitalSignature -CertStoreLocation 'Cert:\CurrentUser\My' `
  -HashAlgorithm SHA256 `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
.\scripts\Build-Release.ps1 -CertificateThumbprint $cert.Thumbprint
```

## 4. 生成完整交付 ZIP 包

完成 MSIX 构建与签名后，运行交付包制作脚本：

```powershell
.\scripts\local-release\Make-Delivery.ps1 -CertificateThumbprint <证书指纹>
```

脚本将自动计算包哈希、提取证书公钥、生成 `release-artifacts.json` 清单并压缩为可供用户直接下载的发布包 `artifacts/release/BiliFluent-2.2611.7.0.zip`。解压目录结构如下：

```text
BiliFluent-2.2611.7.0/
  Install.cmd                  # 安装脚本（.cmd，UAC 提权信任证书并部署）
  Install.ps1                  # PowerShell 安装与预检主脚本
  Trust-Certificate.ps1        # 证书导入辅助脚本（仅导入 LocalMachine\TrustedPeople）
  release-artifacts.json       # 发布包文件清单与 SHA-256 签名摘要
  payload/
    BiliFluent_2.2611.7.0_x64.msix  # 已签名的应用主包
    BiliFluent.cer                  # 发布者公钥证书（CN=BiliFluent）
```

用户解压后双击解压目录中的 `安装 BiliFluent.cmd`，通过 UAC 确认即可完成安装，无需手动键入命令。

## GitHub Actions 自动构建与发布策略

仓库配置了严谨的分支管理与全自动化 CI 流水线：

- **分支策略**：
  - 日常开发与 Bug 修复在 `dev` 分支进行。
  - 通过 Pull Request 合并至受保护的 `main` 分支。
- **预发布自动构建（Pre-Release）**：
  - 每次代码推送到 `main` 分支时，GitHub Actions 自动执行完整编译、下载核验原生资产、以 NativeAOT 构建 Visor、签名 MSIX 并生成交付 ZIP，自动发布为带有预发布标记的 Pre-Release。
- **正式版本发布（Release）**：
  - 当在仓库推送符合版本格式的 Git Tag（如 `v2.2611.7.0`）时，流水线自动构建、签名并发布正式的 GitHub Release。
- **密钥管理（Secrets）**：
  - 代码签名私钥由仓库 Actions Secrets 保护：
    - `SIGNING_PFX_BASE64`：PFX 证书二进制的 Base64 编码字符串。
    - `SIGNING_PFX_PASSWORD`：PFX 私钥保护密码。
    - `SIGNING_CERT_THUMBPRINT`：匹配的 SHA-1 证书指纹。
  - 严禁在仓库、提交历史、文档或发布说明中泄露任何私钥、PFX 文件或开发者本地敏感路径。

## 独立测试通道 LocalTest

如需在不影响正式版的情况下进行高频本地模块迭代测试，请参考 [LocalTest 说明](local-test/README.md)。LocalTest 使用独立的包名 `BiliFluent.LocalTest` 与独立的进程实例，可与正式版安全共存。
