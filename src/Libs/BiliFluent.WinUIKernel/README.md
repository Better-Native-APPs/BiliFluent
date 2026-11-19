# BiliFluent.WinUIKernel

BiliFluent 使用的 WinUI 共享基类、控件、转换器和工具箱的源码副本。程序集名为 `BiliFluent.WinUIKernel`，保留上游 `Richasy.WinUIKernel.Share` 命名空间与应用使用的公开成员。

## 来源与许可

- 上游：[Richasy/winui-kernel](https://github.com/Richasy/winui-kernel/tree/3f1086f6cfa8c1e0c3a64b930a8577dc5496c616) 的 `src/WinUIKernel.Share/`。
- 提交：`3f1086f6cfa8c1e0c3a64b930a8577dc5496c616`，即 `Richasy.WinUIKernel.Share` `0.0.12-preview4` NuGet 包对应的源码。
- 许可：包的 nuspec 声明 MIT，源文件保留上游 `Copyright (c) Richasy` 与 MIT 许可声明。

## 与上游的差异

- 只保留应用使用的类型、它们的包内依赖和对应资源；不引入未使用控件。
- 工程目标框架与平台跟随 `Danmaku.Legacy`，使用中央包版本，不升级依赖。关闭本工程的分析器及文档生成，保留上游源码风格；应用工程的分析器设置不变。

## 构建集成

工程使用 WinUI，目标框架为 `net10.0-windows10.0.22621.0`，支持 x86、x64 与 ARM64，不单独生成 NuGet 包。
