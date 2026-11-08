# BiliFluent 内置的 Danmaku.Legacy

此目录是 BiliFluent 使用的弹幕组件源码副本，保留基于 Win2D 的弹幕渲染路径。弹幕引擎方案与使用说明见[弹幕文档](../../../docs/danmaku.md)。

## 来源与许可

- 上游：[Richasy/danmaku-kernel 的 Danmaku.Legacy](https://github.com/Richasy/danmaku-kernel/tree/4d0f8691f08f1e143294402477e72306a1b38b6f/src/Danmaku.Legacy)。
- 提交：`4d0f8691f08f1e143294402477e72306a1b38b6f`，记录于 `Danmaku.Legacy` `0.0.1-preview6` NuGet 包中。
- 组件许可：MIT；原始 `LICENSE.txt` 与源码署名保持不变。主应用 BiliFluent 基于 Richasy 的「哔哩助理」(Bili Copilot)，按 GPL-3.0 发布，不改变该组件的 MIT 许可。

## 性能调整

源码补丁仅从 `DanmakuRender.OnCanvasDraw` 移除 `GC.TryStartNoGCRegion(50_000_000, true)` 及配对的 `GC.EndNoGCRegion()` 清理。逐帧进入、退出 NoGC 区域会反复触发 GC 暂停并提交较大的堆预算；渲染与异常处理逻辑保持不变，其他 C# 文件保留上游实现。

## 构建集成

项目跟随应用的 .NET / Windows 目标框架与架构。依赖由 `src/Directory.Packages.props` 管理，包括未修改的 `Danmaku.Share` NuGet 包。由于此副本是不可打包的本地库，移除了上游打包导入。

仅此项目关闭分析器、代码风格、可空引用及 XML 文档约束；`TreatAsLocalProperty` 确保 Release 脚本通过全局 MSBuild 属性启用分析器与警告即错误时，这些局部选择仍然生效。
