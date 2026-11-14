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
- **网络访问**：可畅通访问 NuGet 源以及固定的原生外部资产下载源。

## 1. 获取源码与构建内置 SDK

```powershell
git clone https://github.com/Better-Native-APPs/BiliFluent.git
cd BiliFluent
.\scripts\Build-BiliKernel.ps1
```

BiliKernel SDK 源码已作为 Monorepo 纳入仓库目录 `src/Libs/BiliKernel`（保留提交历史，无需初始化子模块）。构建脚本从 `src/Libs/BiliKernel` 编译 `Abstractions`、`Services.Comment`、`Services.Media` 与 `Services.Moment`，将 `0.0.3-bilicopilot.1` 包输出至本地 feed 目录 `artifacts/bilikernel-feed`，并通过 `nuget.config` 完成命名空间映射。

## 2. 准备固定的原生运行时依赖

CI 流程与发布脚本以 [`scripts/native-assets.json`](native-assets.json) 为原生资产的唯一固定来源，下载并核验其中指定的资产。手工编译时请务必使用哈希完全一致的输入，严禁随意替换为其他版本。

### libmpv 运行时
`native-assets.json` 的 `files` 保留资产相对路径到版本标识的映射，路径相对于 `src/Desktop/BiliCopilot.UI/Assets`；`downloads` 中各字段含义如下：

- **`repository` / `releaseTag`**：下载来源的 GitHub 仓库与固定发布 tag。
- **`archiveName` / `archiveSha256`**：归档文件名与归档 SHA-256。
- **`fileName` / `fileSha256`**：归档中唯一的提取目标文件名与文件 SHA-256。
- **`targetPath`**：资产放置路径，同样相对于应用的 `Assets` 目录；其版本标识由 `files` 中同名路径给出。

`scripts/ci/Provision-NativeAssets.ps1` 读取这些字段，归档或提取文件哈希不匹配时立即失败。

### Visor 原生二进制
Visor 直接基于仓库源码通过 NativeAOT 编译，保证纯原生无外部运行时依赖：

```powershell
dotnet publish .\src\Desktop\BiliCopilot.Visor\BiliCopilot.Visor.csproj `
  -c Release -r win-x64 -p:PublishAot=true -o .\artifacts\visor
New-Item -ItemType Directory -Force .\src\Desktop\BiliCopilot.UI\Assets\visor\x64 | Out-Null
Copy-Item .\artifacts\visor\BiliCopilot.Visor.exe .\src\Desktop\BiliCopilot.UI\Assets\visor\x64\BiliCopilot.Visor.exe
```

构建脚本在打包前会按 `scripts/native-assets.json` 的 `files` 严格核验原生资产文件的存在性。

## 3. 构建与签名 MSIX

```powershell
.\scripts\Build-Release.ps1 -CertificateThumbprint <证书指纹>
```

产物生成至 `artifacts/release/BiliFluent_<version>_x64.msix`，`<version>` 自动读取 `src/Desktop/BiliCopilot.UI/Package.appxmanifest` 的 `Identity Version`。包名固定为 `BiliFluent`，发布者主体必须精确为 `CN=BiliFluent`。

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

脚本从清单读取版本，自动计算包哈希、提取证书公钥、生成 `release-artifacts.json` 清单并压缩为可供用户直接下载的发布包 `artifacts/release/BiliFluent-<version>.zip`。解压目录结构如下：

```text
BiliFluent-<version>/
  安装 BiliFluent.cmd          # 一键安装脚本（.cmd，UAC 提权信任证书并部署）
  Install.ps1                  # PowerShell 安装与预检主脚本
  Trust-Certificate.ps1        # 证书导入辅助脚本（仅导入 LocalMachine\TrustedPeople）
  release-artifacts.json       # 发布包文件清单与 SHA-256 签名摘要
  payload/
    BiliFluent_<version>_x64.msix  # 已签名的应用主包
    BiliFluent.cer                  # 发布者公钥证书（CN=BiliFluent）
```

用户解压后双击解压目录中的 `安装 BiliFluent.cmd`，通过 UAC 确认即可完成安装，无需手动键入命令。

## GitHub Actions 自动构建与发布策略

应用版本只在 `src/Desktop/BiliCopilot.UI/Package.appxmanifest` 的 `Identity Version` 填写，CI 从清单派生 tag、标题与产物名，不按构建次数自动编号：

- **分支策略**：
  - 日常开发与 Bug 修复在 `dev` 分支进行。
  - 集中完成一批工作后开 `dev` → `main` PR，待 `build` 通过后，在本地 fast-forward 并推送 `main`，不使用 GitHub 网页合并。
- **四段版本 `a.b.c.d`**：
  - `d = 0` 为正式版，`d > 0` 为预发布版。`a.b.c.0` 正式版后依次发布 `a.b.c.1`、`a.b.c.2`…，下一正式版为 `a.b.(c+1).0`，确保可直接覆盖升级。
  - 清单版本必须严格大于所有已有发布 tag 代表的包版本；旧格式 `v<version>-pre.<run_number>` 的包版本仍按 `<version>` 比较，不能复用。
- **预发布自动构建（Pre-Release）**：
  - 向 `main` 推送代码且清单第四段大于 0 时，自动构建、签名并发布 `v<version>-pre`，标题为 `BiliFluent <version> 预发布版`。CI 创建的 `-pre` tag 不再触发正式版流程。
  - 说明只取 `## [<version>] - YYYY-MM-DD` 小节，只记录相对上一个发布的变化；旧预发布 Release 与 tag 保留。
- **正式版本发布（Release）**：
  - 清单第四段为 0 时，向 `main` 推送只构建打包。推送匹配清单的 `v<version>` 正式 tag 才发布，第四段非 0、版本不匹配或 tag 目标不是本次提交都会失败。
  - 说明只取对应正式版小节，汇总上一个正式版之后的全部变化。
- **合并至 main 前检查单**：
  1. 在 `dev` 显式提升清单版本；预发布递增第四段。
  2. 将 `[Unreleased]` 改名为与清单同名、有日期且非空的版本小节，可在日期后加“（预发布）”；重新保留空的 `[Unreleased]` 并更新底部链接。
  3. PR 的 `build` 通过后，执行 `git fetch origin`、`git merge-base --is-ancestor origin/main dev`，确认可快进，再执行 `git push origin dev:main`。
  4. CI 在构建前检查远端 tag、版本递增和 CHANGELOG 小节；非纯文档 PR 未提升版本会失败，纯文档变更仍跳过构建。不要复用已有 tag。
- **正式版步骤**：
  1. 将清单改为 `a.b.(c+1).0`，合并本周期预发布小节及未发布条目到正式版小节，删除文件中已合并的预发布小节；它们的 GitHub Release 页面保留。
  2. PR 构建通过后在本地 fast-forward `main`，等待该次打包完成。
  3. 在最新 `main` 提交从清单读取版本并打 tag：`[xml]$manifest = Get-Content ./src/Desktop/BiliCopilot.UI/Package.appxmanifest -Raw; $tag = 'v' + $manifest.Package.Identity.Version; git tag -s $tag; git push origin $tag`。
  4. 校验历史版本时只排除本次已推送且提交匹配的正式 tag 自身，其余 tag 仍参与比较。
- **手动运行与脚本自检**：
  - `workflow_dispatch` 按所选 ref 使用相同规则；不要选择预发布 tag 来发布正式版。
  - 运行 `./scripts/ci/Test-ReleaseInfo.ps1` 可离线模拟事件和版本门禁；脚本只修改临时清单副本，注入 tag 列表，不访问远端。
- **密钥管理（Secrets）**：
  - 代码签名私钥由仓库 Actions Secrets 保护：
    - `SIGNING_PFX_BASE64`：PFX 证书二进制的 Base64 编码字符串。
    - `SIGNING_PFX_PASSWORD`：PFX 私钥保护密码。
    - `SIGNING_CERT_THUMBPRINT`：匹配的 SHA-1 证书指纹。
  - 严禁在仓库、提交历史、文档或发布说明中泄露任何私钥、PFX 文件或开发者本地敏感路径。

## 独立测试通道 LocalTest

如需在不影响正式版的情况下进行高频本地模块迭代测试，请参考 [LocalTest 说明](local-test/README.md)。LocalTest 使用独立的包名 `BiliFluent.LocalTest` 与独立的进程实例，可与正式版安全共存。
