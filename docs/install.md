# 安装 BiliFluent

BiliFluent 是采用 Fluent UI 与 WinUI 3 原生控件、专为 Windows 优化的第三方哔哩哔哩客户端。包身份为 `BiliFluent` / `CN=BiliFluent`，可与官方客户端及上游「哔哩助理」安全并存。

## 系统要求

- Windows 10（Build 19041 及以上）或 Windows 11。
- x64 架构。
- Windows PowerShell 5.1 或更高版本。

## 安装步骤

### 便捷安装

1. 从 [BiliFluent Releases](https://github.com/Better-Native-APPs/BiliFluent/releases) 下载 `BiliFluent-2.2611.7.0.zip` 并完整解压。
2. 双击解压目录中的 `安装 BiliFluent.cmd`。
3. 弹出 UAC 提权提示时确认允许。脚本会自动将公钥证书导入 `LocalMachine\TrustedPeople` 并安装应用。
4. 安装完成后，在「开始」菜单启动 **BiliFluent**。

> **证书说明**：证书仅导入「受信任的人」（`TrustedPeople`），不会导入「受信任的根证书颁发机构」（`Root`）。

### PowerShell 手动安装

在解压目录打开 Windows PowerShell：

```powershell
# 1. 只读预检
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1

# 2. 正式安装
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Install
```

若希望手动导入证书，可在管理员 PowerShell 中运行：

```powershell
Import-Certificate -FilePath .\payload\BiliFluent.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

随后回到普通用户窗口执行 `.\Install.ps1 -Install`。当前发布证书指纹为 `6727300457DD924DAD363FDD27C5166E0E62CF32`。

## 更新与卸载

- **更新**：下载新版本 ZIP，解压后双击运行安装脚本即可平滑升级。
- **卸载**：在 Windows「设置 → 应用 → 安装的应用」中找到 BiliFluent 点击「卸载」。如需移除证书，可在计算机证书控制台的「受信任的人」分类中按指纹删除。

## 排障

若安装遇到错误：
1. 确认已完整解压 ZIP，未在压缩包内部运行。
2. 确认系统为 64 位且版本不低于 Build 19041。
3. 若提示权限或策略受限，确认当前账户具备管理员权限，或检查侧加载组策略。
4. 反馈问题时请附上错误码与脱敏后的日志，提交至 [BiliFluent Issues](https://github.com/Better-Native-APPs/BiliFluent/issues)。

---

*基于 Richasy 的「哔哩助理」(Bili Copilot) 二次开发，遵循 GPL-3.0 协议。*
[关于项目](./about.md) · [播放指南](./player.md) · [源码构建与 CI](https://github.com/Better-Native-APPs/BiliFluent/blob/main/scripts/README.md)
