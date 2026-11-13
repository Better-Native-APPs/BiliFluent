<p align="center">
  <img src="assets/StoreLogo.png" width="48" height="48" alt="BiliFluent" />
</p>

<h1 align="center">BiliFluent</h1>

<p align="center">
  <a href="https://github.com/Better-Native-APPs/BiliFluent/releases"><img src="https://img.shields.io/github/v/release/Better-Native-APPs/BiliFluent" alt="Release" /></a>
  <a href="https://github.com/Better-Native-APPs/BiliFluent/releases"><img src="https://img.shields.io/github/release-date/Better-Native-APPs/BiliFluent" alt="Release Date" /></a>
  <a href="https://github.com/Better-Native-APPs/BiliFluent/releases"><img src="https://img.shields.io/github/downloads/Better-Native-APPs/BiliFluent/total" alt="Downloads" /></a>
  <a href="https://github.com/Better-Native-APPs/BiliFluent/actions/workflows/release.yml"><img src="https://img.shields.io/github/actions/workflow/status/Better-Native-APPs/BiliFluent/release.yml?branch=main" alt="CI Status" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Better-Native-APPs/BiliFluent" alt="License" /></a>
  <a href="#安装"><img src="https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-blue" alt="Platform" /></a>
  <a href="https://github.com/Better-Native-APPs/BiliFluent/stargazers"><img src="https://img.shields.io/github/stars/Better-Native-APPs/BiliFluent" alt="Stars" /></a>
</p>

<p align="center">
  专为 Windows 优化的哔哩哔哩第三方桌面客户端，采用 Fluent UI 与 WinUI 3 原生控件，流畅省电无广告。
</p>

<p align="center">
  <a href="#特性">特性</a>
  &nbsp;&bull;&nbsp;
  <a href="#安装">安装</a>
  &nbsp;&bull;&nbsp;
  <a href="#从源码构建">从源码构建</a>
  &nbsp;&bull;&nbsp;
  <a href="#更新日志">更新日志</a>
  &nbsp;&bull;&nbsp;
  <a href="#参与贡献">参与贡献</a>
</p>

<p align="center">
  项目基于 Richasy 的「哔哩助理」（<a href="https://github.com/Richasy/Bili.Copilot">Bili Copilot</a>）二次开发，遵循 <a href="LICENSE">GPL-3.0</a> 许可协议。
</p>

---

## 特性

- **Fluent UI 原生体验**：采用 WinUI 3 控件与现代设计，支持触控手势，可与官方客户端并存。
- **mpv 内嵌省电弹幕**：默认启用基于 libass 的内嵌弹幕渲染，降低 CPU 与显存占用（保留 Win2D 备选）。
- **播放进度合并刷新**：节流合并高频进度更新，进度拖拽与状态同步更平稳。
- **播放器资源彻底释放**：关闭播放窗口时即时完全释放 mpv 实例、解码句柄与显存。
- **WMP 风格全屏**：双击画面切换全屏，支持 `F` / `F11` 进入或退出全屏、`Esc` 退出全屏。
- **内置空降助手**：支持自动或手动跳过合作广告、片头片尾与闲聊片段。
- **后台效率模式**：窗口最小化或后台空闲无播放任务时，自动调度 Windows 效率模式节省电量。
- **低配硬件预设**：默认 1080P60 画质上限与 AVC/H.264 优先硬解，默认 64 MiB 缓存，提供「快速 (Fast)」profile 切换。

## 安装

当前版本：`2.2611.8.0`。支持 Windows 10（Build 19041 及以上）与 Windows 11（x64）。

1. 从 [BiliFluent Releases](https://github.com/Better-Native-APPs/BiliFluent/releases) 下载 `BiliFluent-2.2611.8.0.zip` 并完整解压。
2. 双击解压目录中的 `安装 BiliFluent.cmd`。
3. 在 UAC 提示窗口中确认允许。脚本会自动将公钥证书导入 `LocalMachine\TrustedPeople` 并安装应用。

### 手动安装（PowerShell）

在解压目录下打开 Windows PowerShell 执行：

```powershell
# 只读预检
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1

# 安装
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Install
```

更多安装选项见[安装指南](docs/install.md)。

## 从源码构建

需要 `global.json` 固定的 **.NET SDK 10.0.403** 与 Windows SDK 工具。

```powershell
git clone https://github.com/Better-Native-APPs/BiliFluent.git
cd BiliFluent
.\scripts\Build-BiliKernel.ps1
# 按 scripts/README.md 放置 libmpv 并编译 Visor 后执行：
.\scripts\Build-Release.ps1 -CertificateThumbprint <证书指纹>
.\scripts\local-release\Make-Delivery.ps1 -CertificateThumbprint <证书指纹>
```

详细环境配置与原生依赖校验值见[构建说明](scripts/README.md)。

## CI 与自动发布

- **分支策略**：日常开发在 `dev` 分支，经 PR 合并到受保护的 `main` 分支。
- **预发布构建**：向 `main` 分支推送提交时自动构建已签名的 Pre-Release。
- **正式发布**：推送 `v*` 标签时构建并发布 GitHub Release。
- **代码签名**：由仓库 Secrets（`SIGNING_PFX_BASE64`、`SIGNING_PFX_PASSWORD`、`SIGNING_CERT_THUMBPRINT`）提供。

## 更新日志

版本更新历史记录见 [CHANGELOG.md](CHANGELOG.md)。

## 参与贡献

分支管理、提交规范（Conventional Commits）与发版清单见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 致谢

- 上游项目：Richasy 的「哔哩助理」([Richasy/Bili.Copilot](https://github.com/Richasy/Bili.Copilot))
- 组件与框架：[Windows App SDK](https://github.com/microsoft/windowsappsdk) / [WinUI 3](https://github.com/microsoft/microsoft-ui-xaml) · [mpv](https://mpv.io/) · [FFmpeg](https://ffmpeg.org/) · [Danmaku.Legacy](src/Libs/Danmaku.Legacy/README.md)
