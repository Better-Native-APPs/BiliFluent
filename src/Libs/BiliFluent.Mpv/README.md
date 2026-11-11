# BiliFluent.Mpv

BiliFluent 的 mpv 播放层：`MpvClient`（选项、属性、命令、事件）和 `MpvPlayer`（可绑定的播放状态、媒体源与历史记录解析器接口）。libmpv 的原生调用全部经由 [HanumanInstitute.LibMpv](https://github.com/mysteryx93/LibMpv-OpenGL) 的 `MpvApi` 绑定。

libmpv 本体不在此项目中，由应用随包提供（`Assets/libmpv/<架构>/libmpv-2.dll`）。

## 来源与许可

- 上游：[Richasy/mpv-kernel](https://github.com/Richasy/mpv-kernel/tree/c1ef0f403097129c3b6db4c466cc4147054471d2) 的 `MpvKernel.Core` 与 `MpvKernel.Player`，以及 `MpvKernel.Interop` 中的 `MpvError`、`MpvLogLevel` 两个枚举。
- 提交：`c1ef0f403097129c3b6db4c466cc4147054471d2`，即 `Richasy.MpvKernel.*` `0.0.2-preview2` NuGet 包对应的源码。
- 许可：上游为 MIT，`LICENSE.txt` 是上游文件的原样副本，移植来的源文件保留原有的版权与许可声明（`Copyright (c) Richasy`）。
- HanumanInstitute.LibMpv 同为 MIT，以 NuGet 包形式引用，版本在 `src/Directory.Packages.props` 中固定。

## 与上游的差异

- 上游的 P/Invoke 层（`MpvKernel.Interop`）没有移植。`Core/Interop` 下是新写的薄封装：`MpvLibrary` 指定 libmpv 文件，`MpvCoreHandle` 包装一个 mpv 句柄，`MpvNodeReader` 把 `mpv_node` 复制成托管对象。
- 去掉了 `FluentResults` 和 `RichasyKernel.Abstractions` 依赖。不抛异常的调用改为返回 `MpvResult` / `MpvResult<T>`。
- 只保留应用用到的客户端方法。其余选项和属性通过 `MpvClient` 的按名称访问方法读写：`SetOptionString`、`SetProperty`、`SetPropertyString`、`GetProperty`、`GetPropertyString`、`Command`、`CommandString`。
- 事件循环使用独立线程。`DisposeAsync` 在销毁句柄前先唤醒并等待事件循环结束。
- 读取 `mpv_node` 后会释放 mpv 分配的内存（上游没有释放）。
- 命名空间和类型名与上游一致（`Richasy.MpvKernel.*`），应用中的调用代码无需改动。
- `StopAsync` 沿用上游行为：写入并不存在的 `stop` 属性，始终返回失败。

## libmpv 文件名

HanumanInstitute.LibMpv 按 `<名称>-<版本>.dll` 拼出库文件名，所以自定义的 libmpv 文件名也必须是这种形式，例如 `libmpv-2.dll`、`mpv-2.dll`。进程内第一次成功加载的文件会一直使用到应用退出，之后传入其他路径不会生效。

## 构建集成

项目目标框架为 `net10.0`（Any CPU），不可打包。依赖版本由 `src/Directory.Packages.props` 管理。分析器与“警告视为错误”保持开启，项目文件里只对移植代码关闭了几条规则，原因写在 `BiliFluent.Mpv.csproj` 的注释中。
