# BiliFluent.WinUIKernel

BiliFluent 使用的 WinUI 共享基类、控件、转换器和工具箱的源码副本。程序集名为 `BiliFluent.WinUIKernel`，保留上游 `Richasy.WinUIKernel.Share` 命名空间与应用使用的公开成员。

现有 43 个 C# 类型（包含泛型版本和辅助类型），共 39 个 C# 文件、4,487 行，以及 11 个 XAML 文件、1,618 行；不计工程、说明和编译器生成代码。最初移植时为 61 个类型、52 个 C# 文件 6,097 行、17 个 XAML 文件 2,112 行，其后已换成内置控件的部分见“与上游的差异”。

## 来源与许可

- 上游：[Richasy/winui-kernel](https://github.com/Richasy/winui-kernel/tree/3f1086f6cfa8c1e0c3a64b930a8577dc5496c616) 的 `src/WinUIKernel.Share/`。
- 提交：`3f1086f6cfa8c1e0c3a64b930a8577dc5496c616`，即 `Richasy.WinUIKernel.Share` `0.0.12-preview4` NuGet 包对应的源码。
- 许可：包的 nuspec 声明 MIT，源文件保留上游 `Copyright (c) Richasy` 与 MIT 许可声明。
- 此提交的上游仓库根目录没有独立许可文件，许可依据为该包 nuspec 的 MIT 表达式及上游源文件头。

## 与上游的差异

- 只保留应用使用的类型、它们的包内依赖和对应资源；不引入未使用控件。
- `Themes/Generic.xaml` 删除未移植的 DraggableCardControl、SettingsCard、SettingsExpander 的合并字典，其他字典的 `ms-appx:///` 路径改为新程序集名；控件模板保持原样。
- `WinUIKernelShareExtensions.InitializeShareKernel` 改为只接收可选 `ILogger`，删除 `Kernel` 与内部资源工具箱的保存/初始化。保留的代码不通过 Kernel 查询服务；查询服务的 `AppDialog` 和使用内部本地化资源的 `TipHeader`/`LocaleExtension` 没有移植，因此无需服务定位器、内部字符串资源或 `RichasyKernel.Abstractions`。
- 工程目标框架与平台跟随 `Danmaku.Legacy`，使用中央包版本，不升级依赖。关闭本工程的分析器及文档生成，保留上游源码风格；应用工程的分析器设置不变。
- .NET 11 已由框架提供 Logging.Abstractions，删除上游的冗余显式包引用以消除 NU1510；日志初始化和控件日志调用保持原样，不使用 NoWarn 或 pragma。
- 有内置等价物的控件已从本库删除，应用改用 WinUI 或 CommunityToolkit 的控件：
  - `AppRepeater`、`AppItemsRepeater`、`ConvertingCollectionProxy`、`ItemContentPresenterData`、`ItemContentPresenterDataCollection`、`ItemsRepeaterExtension`、`LayoutExtensions` → 内置 `ItemsRepeater`。
  - `CardControl`（连同 `ShadowConfig`、`SharedShadowResources`）→ 内置 `Button` 加应用内样式。
  - `AppToggleCard` → 内置 `ToggleButton` 加应用内样式。
  - `SideSplitButton` → 内置 `Button`。
  - `ColumnSplitter`、`VisibilityToggleButton` → 直接使用 CommunityToolkit 的 `PropertySizer`。
  - `IFontToolkit`、`SharedFontToolkit` 没有使用者，已删除；`SystemFont` 保留，移到 `Toolkits/SystemFont.cs`。
- `Themes/Generic.xaml` 相应去掉 CardControl、AppToggleCard 的合并字典；`WinUIKernelShareExtensions` 去掉只有 `CardControl` 读取的 `IsCardShadowEnabled`、`IsCardAnimationEnabled` 两个开关。

## 构建集成

工程使用 WinUI，目标框架为 `net11.0-windows10.0.22621.0`，语言版本 C# 15，跟随主线的 `Danmaku.Legacy` 与中央配置；支持 x86、x64 与 ARM64，不单独生成 NuGet 包。

## 移植核对（.NET 10 历史结果）

以下两节记录的是移植当时的状态，其中的类型数、XBF 数是删减之前的数字。

- .NET 10 候选 `e641778c` 的整体 Debug 编译和 Release 发布/打包均为 0 警告、0 错误；未签名的本地 MSIX 含新程序集及资源。
- 旧 PRI 的 21 个 XBF 中，保留的 17 个在新程序集路径下全部存在；6 条指向库的资源 URI 均有索引匹配。未保留资源只属于未使用的 DraggableCardControl、SettingsCard、SettingsExpander 和 TipHeader。
- 比较旧包与新 DLL 的 54 个公开/受保护类型及 729 条 API 元数据记录，除上述初始化适配外全部一致；全部指定仓库外自检的通过数与基点一致。
- 51 个 C# 文件与 16 个 XAML 文件和固定提交的上游源码完全相同，只有上述初始化入口与 Generic 字典存在差异。

## 合并后的 .NET 11 核对

- 整体 Debug 编译为 0 警告、0 错误；C# 15 不需要额外修改移植源码。
- 新 DLL 的 54 个公开/受保护类型、729 条 API 记录仍与旧包一致，仅上述初始化适配除外；61 个手写类型全部保留。
- 保留的 17 个 XBF、6 条资源 URI 和新 DLL/PRI/XAML 产物全部通过对照；旧包及 RichasyKernel.Abstractions 均已退出依赖图。
- 合并树上全部指定仓库外自检通过，通过数合计 2,260；未启动应用。

## 控件删减后的核对

- 整体 Debug 编译为 0 警告、0 错误。
- 产物的资源索引里本库剩 11 个 XBF，与保留的 11 个 XAML 文件一一对应；已删除控件的 XBF 不再出现。
- 全部指定仓库外自检通过，通过数与删减前一致；未启动应用，界面效果由人工在测试包上核对。
