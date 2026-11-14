# 变更日志

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范。

## [Unreleased]

### 新增
- 直播支持 HDR：可获取 PQ 与 HLG 的 HDR 流，由 mpv（gpu-next）输出；画质菜单分别列出 HDR 与 SDR 档位。自动选择画质时，仅当显示器已开启 HDR 才选用 HDR，两者均可手动切换。
- 用户主页增加直播入口：主播开播时，个人主页显示“正在直播”，点击即可进入直播间。
- 字幕列表为 AI 生成的字幕加上“AI”标签，便于与上传的字幕区分。
- 安装包内置分发 Microsoft.WindowsAppRuntime.2 (2.6.5) 框架包：安装脚本在部署应用前离线安装该依赖（若系统已存在相同或更高版本则自动跳过），解决此前因缺失运行时导致安装终止的问题。
- 发布 CI 流水线增加安装器自动化测试。

### 变更
- 侧栏导航改用 DevWinUI 的 Store 风格；“消息”项的未读提示由数字徽标改为圆点。
- 播放器的清晰度与字幕列表、直播间的清晰度列表改为单击即切换，此前需要双击。
- 内容列表改用 WinUI 内置的 ItemsView 与 ItemsRepeater，内容卡片改用原生 Button，封面缩略图改用内置 Image。
- 设置页改用 CommunityToolkit 的设置控件（SettingsCard / SettingsExpander）；“关于”中的说明与仓库链接收进应用卡片下方默认折叠的展开项。
- 侧边面板宽度改用 CommunityToolkit 的 PropertySizer 拖动调整（主面板 220–300 px，副面板 180–300 px），显示与隐藏改用普通按钮；直播分区加载失败时以 InfoBar 提示并提供重试。
- 文字截断、加载提示与空列表改用内置控件：TextBlock 自带省略（仅在文字确实被截断时显示悬浮提示）、ProgressRing 与 ProgressBar、内置的空状态面板。
- 应用自定义样式改为在 WinUI 默认样式的基础上扩展。
- 主窗口、图片查看、启动与网页登录四个窗口改为基于 WinUIEx 的 WindowEx，自行设置标题栏与 Mica 背景；三个副窗口使用 WinUI 内置的 TitleBar 控件。
- 播放器的 mpv 绑定层改为仓库内实现（`src/Libs/BiliFluent.Mpv`，基于 HanumanInstitute.LibMpv），不再依赖 Richasy.MpvKernel。
- 内置 libmpv 升级至 mpv v0.42.0（shinchiro 20261109 构建）。
- 依赖注入容器换为 Microsoft.Extensions.DependencyInjection。
- BiliKernel 改为直接引用仓库内的项目（`src/Libs/BiliKernel`），不再混用 NuGet 包与本地打包的包。
- 运行时迁移至 .NET 10，Windows App SDK 升级至 2.6.5，FluentIcons.WinUI 升级至 2.x，CommunityToolkit 系列组件统一至 8.2.251219，并同步更新其余 NuGet 依赖与 CI Actions。
- 启用集中包管理的传递依赖固定（`CentralPackageTransitivePinningEnabled`），间接依赖也按统一声明的版本解析；包源只保留 nuget.org，删除没有项目使用的版本声明与分析器（Flurl.Http、NLog、Polly、SourceLink、StyleCop 及测试相关包）。

### 移除
- 移除 DirectX 弹幕渲染器及其依赖的 Danmaku.Core 预览包。弹幕渲染方式保留 mpv 内嵌（默认）与 Win2D 两种；此前选择 DirectX 的设置会自动改用 mpv 内嵌。
- 移除未被使用的内置 `ffmpeg.exe`（约 102 MiB），安装包体积相应减小。
- 移除未使用的 CommunityToolkit Labs 依赖（SegmentedControl、Shimmer）与 System.Linq.Async，清理重复的 Windows App SDK 版本声明。
- 移除不再使用的自定义控件与样式。

### 修复
- 修复 mpv 内嵌弹幕滚动过快的问题：滚动速度与 Win2D 渲染器保持一致。
- 修复字幕打开后消失的问题：字幕加载成功后才应用选择，重播、切换清晰度或自动重试后恢复此前选中的字幕。
- 修复空降助手在“标记开始”之后拖动进度或播放跨过片段边界时，“标记结束”失败、面板无法继续操作的问题。
- 修复列表所在页面被隐藏后仍不断触发“加载更多”、持续发起请求的问题。
- 修复视频加载失败后（例如播放地址过期后重播）点击返回，播放器无法关闭、此后任何视频都打不开的问题。
- 修复离开直播间后立即再次进入时，直播间可能一直停在“准备中”的问题。
- 修复点击视频标签没有反应的问题：现在会跳转到该标签的搜索结果。

## [2.2611.7.0] - 2026-11-09

### 新增
- 发布包内置双击安装脚本 `安装 BiliFluent.cmd`，自动处理证书信任并静默安装应用包。
- 引入 GitHub Actions 自动化流水线，支持 Pull Request 构建检查、main 分支预发布与 Git 标签正式发版。

### 变更
- 应用更名为 BiliFluent，包标识切换为 `BiliFluent` / `CN=BiliFluent`，延续版本号且可与官方客户端共存。
- 定制 BiliKernel SDK 源码合并入代码库目录 `src/Libs/BiliKernel`（保留提交历史），转为单体仓库（Monorepo），移除 Git 子模块依赖。
- 构建与 CI 流程固定采用经 SHA-256 校验的 shinchiro (20261006) 版本 libmpv 与 FFmpeg 原生依赖。
- 关于页面更新致谢信息，明确标注对上游 Richasy「哔哩助理」的二次开发来源与开源许可。
- 全面重写项目说明、构建说明与功能指南等文档。

### 移除
- 移除原上游专用且已失效的旧版 GitHub 工作流配置。

## [2.2611.6.0] - 2026-11-08

### 新增
- 引入 Windows Media Player 风格全屏交互：支持双击画面切换全屏，支持快捷键 `F` / `F11` 进入或退出全屏、`Esc` 退出全屏，控制栏无操作 2.5 秒后自动隐藏。
- 引入后台效率模式调度：窗口最小化且无播放任务时，自动调度 Windows 效率模式（EcoQoS 与空闲优先级）节约能耗。
- 引入本地交付打包脚本 `scripts/local-release/Make-Delivery.ps1`，自动校验文件摘要并输出发布包。

### 变更
- 默认弹幕渲染引擎切换为「mpv 内嵌（省电）」：采用 libass 作为 ASS 轨道内嵌渲染弹幕并提供一次性配置迁移，保留 Win2D 渲染器作为备选。
- 弹幕加载与调度机制重构：关闭弹幕时不发起网络下载，并发滚动弹幕数限制在 20 条以内，弹幕解析移至后台线程，暂停、最小化或窗口隐藏时主动暂停处理。
- 播放进度通知节流合并：合并高频播放进度通知（约 4 次/秒），进度条按像素步进刷新，控制栏隐藏时不执行每帧更新计算。
- 播放硬件预设调整：默认自动画质上限设为 1080P60，默认解码偏好调整为 H.264 > HEVC > AV1，默认解复用缓存调整为 64 MiB。
- 内存与资源占用深度调优：减少页面与列表缓存项，降低封面图解码尺寸，字体按需加载，停止不可见的加载动画，禁用未使用的 mpv 脚本与 ytdl 模块。
- 空降助手时间轴标记样式更新：优化进度条片段标记的分段与高亮展示效果。

### 修复
- 修复播放器退出时的资源泄漏问题：关闭播放窗口时先持久化保存观看进度并彻底退出 mpv 实例，杜绝后台残留播放与线程泄漏。
- 修复点击标题栏返回键关闭播放器时因度量指标异常导致的崩溃路径。
- 修复退出播放器时的界面状态：退出播放前优先恢复主窗口界面。
- 修复按空格键意外退出全屏的问题。
- 修复空降助手手动与自动跳过失效的问题，提示弹窗增加自动隐藏。
- 修复 Win2D 弹幕逐帧触发 GC 暂停的问题：内置并修补 `Danmaku.Legacy`，移除逐帧强制申请与释放 NoGC 区域的逻辑。

## [2.2611.3.0] - 2026-10-18

### 新增
- 集成空降助手（SponsorBlock）：支持视频片段标记显示与跳过控制，提供自动跳过与提示跳过策略设置，支持本地片段草稿与编辑提交。
- 增加专栏文章阅读器：基于 WebView2 渲染文章内容，支持选中文字复制与快捷网页搜索。
- 集成经典播放器模式：支持多播放后端切换（原生渲染、Island、网页以及外部 mpv/mpv.net 进程唤起）。
- 增加消息中心与未读提醒：支持查询未读通知与私信列表，解析消息正文内容并提供链接跳转。
- 新增发布构建与测试脚本：提供 MSIX 正式打包脚本 `scripts/Build-Release.ps1` 与 LocalTest 独立测试通道工具集。

### 变更
- 重构直播播放与弹幕聊天室：提供独立直播窗口与播放控制，重构直播弹幕 WebSocket 连接与数据解析。
- 重构 UP 主空间页面：重新设计个人空间布局，支持浏览投稿视频、图文动态、专栏合集与空间内视频搜索。
- 调整侧边导航与内容页面：优化分区索引、动态推荐、热门榜单与稍后再看等聚合页面的响应式布局。
- 补充中文界面本地化文案与资源定义。
- 引入定制 BiliKernel SDK：修补评论、媒体与动态元数据契约，支持本地离线编译生成依赖包。

### 修复
- 优化动态流多图展示：改进动态图片画廊与大图查看窗口的交互展示。
- 优化界面过渡动画与交互响应。

[Unreleased]: https://github.com/Better-Native-APPs/BiliFluent/compare/v2.2611.7.0...HEAD
[2.2611.7.0]: https://github.com/Better-Native-APPs/BiliFluent/releases/tag/v2.2611.7.0
