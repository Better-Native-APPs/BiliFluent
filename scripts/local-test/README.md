# BiliFluent LocalTest 测试通道

BiliFluent 是由 [Better Native APPs](https://github.com/Better-Native-APPs) 维护的第三方哔哩哔哩 Windows 桌面客户端，采用 Fluent UI / WinUI 3 原生控件，无广告并为 Windows 与低配平板深度调优。

正式发布与安装入口为 [BiliFluent Releases](https://github.com/Better-Native-APPs/BiliFluent/releases)；普通终端用户请直接使用[正式安装指南](../../docs/install.md)，请勿将本页的开发者本地测试工具当作常规安装方式。

LocalTest 提供独立的包名、实例标识与通知激活标识，可与正式版 BiliFluent 及官方应用同时安装共存，互不干扰。

## 固定通道身份

| 属性 | 值 |
| --- | --- |
| 应用包名 | `BiliFluent.LocalTest` |
| 发布者 | `CN=BiliFluent` |
| 包系列名称 | `BiliFluent.LocalTest_7rrwb2e69v8er` |
| 显示名称 | `BiliFluent（测试）` |
| 实例键 | `BiliFluent.LocalTest` |
| 应用 ID | `App` |
| 默认起始版本 | `2.2611.2.0`，后续测试版本须递增 |
| 架构 | `x64` |
| 通知激活 CLSID | `CF0E1EDD-C7F4-56B5-BFD8-C6D8EFBCC0FE` |

CLSID 按 `channel.json` 中新仓库 URL 的 UUIDv5 规则生成；可执行文件仍为内部名称 `BiliCopilot.UI.exe`。发布者 ID `7rrwb2e69v8er` 由 `CN=BiliFluent` 的 UTF-16LE SHA-256 摘要前 8 字节进行 Crockford Base32 编码得到。

## 工具边界与输入

- 使用 Windows x64 与 Windows PowerShell 5.1，构建 SDK 仍按仓库 `global.json` 的 `10.0.403`。
- `Prepare-Package.ps1` 要求已经生成的、文件清单及摘要获确认的原生布局；拒绝包含 CLR 头的托管 PE。正式 ReadyToRun Release 不是该工具要求的 NativeAOT / 原生测试布局。
- 源清单可来自新 `BiliFluent` / `CN=BiliFluent` 身份；仍兼容工具原有的上游开发布局输入。转换后强制使用上表的测试身份。
- 工具不自动寻找或导入私钥、不签名、不自动下载依赖、不修改全局策略。
- 当前正式 CI 流程见[构建与发布说明](../README.md)，不要假定它同时生成 LocalTest 原生布局。

## 1. 准备并打包测试布局

```powershell
# 仅预检
.\scripts\local-test\Prepare-Package.ps1 `
  -SourceLayout <原生布局目录> -OutputRoot <新的测试输出目录> `
  -WorkspaceRoot <工作区目录> -ApprovedInventory <文件清单JSON路径> `
  -ExpectedInventorySha256 <文件清单哈希> -Version '2.2611.2.0'

# 确认后转换并打包
.\scripts\local-test\Prepare-Package.ps1 `
  -SourceLayout <原生布局目录> -OutputRoot <新的测试输出目录> `
  -WorkspaceRoot <工作区目录> -ApprovedInventory <文件清单JSON路径> `
  -ExpectedInventorySha256 <文件清单哈希> -Version '2.2611.2.0' `
  -Prepare -Pack -MakeAppxPath <makeappx.exe路径> `
  -ExpectedMakeAppxSha256 <makeappx.exe哈希>
```

输入、输出与工作区必须是互不包含的普通绝对路径；输出目录必须是全新目录。脚本核对布局、版本、x64 原生 PE 及通知注册后，生成 `delivery/BiliFluent.LocalTest_2.2611.2.0_x64.msix`。

## 2. 签名并预先信任测试证书

签名证书的主题须为 `CN=BiliFluent`。使用对应私钥的证书指纹签名：

```powershell
$msix = '<测试输出目录>\delivery\BiliFluent.LocalTest_2.2611.2.0_x64.msix'
& signtool.exe sign /fd SHA256 /sha1 <证书指纹> /s My $msix
```

LocalTest 安装器不会自动导入证书。由开发者在安装前核对并信任其公钥到 `TrustedPeople`，不得导入 `Root`。证书、公钥或私钥均不要额外放入下面要求精确文件清单的测试交付目录。

## 3. 封包与固化清单

```powershell
.\scripts\local-test\Seal-Package.ps1 `
  -DeliveryRoot '<测试输出目录>\delivery' `
  -LooseExePath '<测试输出目录>\layout\BiliCopilot.UI.exe' `
  -ExpectedLooseExeSha256 <EXE哈希> `
  -ExpectedSignerCertificateSha256 <证书DER哈希> `
  -Version '2.2611.2.0'
```

封包前交付目录仅包含签名 MSIX、`Install.ps1`、`LocalTest.psm1` 和 `channel.json`。脚本核对签名、公钥摘要、包身份与原生 EXE 一致性，生成 `test-artifacts.json`；已封包的版本不覆盖，升级时分配新版本和新目录。外部记录并传递脚本输出的清单 SHA-256。

## 4. 只读预检与部署

在测试交付目录运行：

```powershell
.\Install.ps1 -ExpectedManifestSha256 <test-artifacts.json哈希>
.\Install.ps1 -ExpectedManifestSha256 <test-artifacts.json哈希> -Install
```

缺失的框架依赖不会自动下载。同版本写入与降级被拒绝；封包检查通过也不等于已经完成真实 Windows 安装、启动或运行时验证。

## 来源与反馈

基于 Richasy 的「哔哩助理」([Bili Copilot](https://github.com/Richasy/Bili.Copilot)) 二次开发，遵循 [GPL-3.0](../../LICENSE)。问题请提交 [BiliFluent Issues](https://github.com/Better-Native-APPs/BiliFluent/issues)，分享日志前删除身份信息、凭据与本地路径。
