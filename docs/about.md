# 关于 BiliFluent

BiliFluent 是由 [Better Native APPs](https://github.com/Better-Native-APPs) 维护的第三方哔哩哔哩 Windows 桌面客户端。采用 Fluent UI 与 WinUI 3 原生控件，无广告，专注于低资源消耗与流畅播放。

## 特性亮点

- **Fluent UI 原生界面**：采用 WinUI 3 控件构建，支持高 DPI 缩放、深浅色主题与触控手势。
- **mpv 内嵌省电弹幕**：默认启用内嵌渲染，大幅降低 CPU 与显存占用（保留 Win2D 备选）。
- **播放资源及时释放**：关闭播放窗口时即时完全释放 mpv 实例、解码上下文与显存。
- **播放进度合并刷新**：节流合并高频进度更新，操作更平稳。
- **后台效率模式**：窗口最小化或后台空闲无播放任务时，自动调度 Windows 效率模式节省电量。
- **低配硬件友好预设**：默认 1080P60 画质上限与 AVC/H.264 优先硬解，默认 64 MiB 缓存，提供一键「快速 (Fast)」profile 切换。
- **WMP 风格全屏**：支持双击画面全屏，支持 `F` / `F11` / `Esc` 快捷切换。
- **内置空降助手**：自动跳过合作广告与片头片尾片段。

## 来源与许可

项目基于 Richasy 开源的「哔哩助理」([Bili Copilot](https://github.com/Richasy/Bili.Copilot)) 二次开发，遵循 [GPL-3.0 许可证](https://github.com/Better-Native-APPs/BiliFluent/blob/main/LICENSE)。保留上游及开源依赖组件的原作者署名与许可协议。

- [BiliFluent 源代码](https://github.com/Better-Native-APPs/BiliFluent)
- [定制 BiliKernel SDK](https://github.com/Better-Native-APPs/BiliKernel)
- [安装指南](./install.md) · [源码构建说明](https://github.com/Better-Native-APPs/BiliFluent/blob/main/scripts/README.md)
