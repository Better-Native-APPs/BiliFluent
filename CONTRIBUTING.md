# BiliFluent 贡献指南

感谢参与 BiliFluent 项目！在提交代码或 Pull Request 前，请阅读本指南。

## 分支策略与发布流程

- **`dev` 分支**：日常开发分支。所有新功能开发、性能优化与缺陷修复均提交至 `dev` 分支（或在个人 Fork 中以 `dev` 为基线创建特性分支后向 `dev` 发起 PR）。
- **`main` 分支**：受保护的发布主分支。仅通过审查后的 Pull Request 从 `dev` 分支合并。
- **预发布（Pre-Release）**：向 `main` 分支推送提交时，GitHub Actions 自动构建并发布已签名的预发布包（`v<version>-pre.<run_number>`）。
- **正式发布（Release）**：在 `main` 分支创建并推送符合版本格式的 Git Tag（`v<version>`，例如 `v2.2611.7.0`），触发自动构建、签名并发布正式 GitHub Release。

## Git 提交规范

提交信息遵循 [Conventional Commits](https://www.conventionalcommits.org/) 规范，格式如下：

```text
type(scope): subject
```

### 提交类型（Type）

- `feat`: 新增功能
- `fix`: 缺陷修复
- `perf`: 性能调优与资源占用优化
- `refactor`: 重构（无功能变更且无 API 变更）
- `build`: 构建工具链、外部原生依赖或项目工程配置变更
- `ci`: CI 流水线与自动化发版脚本调整
- `docs`: 文档内容更新
- `chore`: 杂项维护与常规辅助任务

提交标题应简明清晰，直接说明变更内容。

## 变更日志维护

- 任何包含面向用户变动（新功能、行为变更、体验优化或缺陷修复）的 Pull Request，必须在 `CHANGELOG.md` 的 `## [Unreleased]` 节下补充单行更新说明。
- 正式发版时，将 `## [Unreleased]` 下积累的条目移动至对应版本号标题（如 `## [2.2611.8.0] - YYYY-MM-DD`）下，保留空的 `## [Unreleased]` 占位，并更新底部的版本对比链接。

## 发版与版本号升级清单

发布新版本时，需保持代码、清单与标签版本号一致（采用 4 段式版本号 `x.y.z.w`）。请按顺序更新以下文件：

1. `src/Desktop/BiliCopilot.UI/Package.appxmanifest`：更新 `<Identity ... Version="x.y.z.w" />`（CI 与发版流程的基准版本）。
2. `src/Desktop/BiliCopilot.UI/BiliCopilot.UI.csproj`：更新 `<FileVersion>x.y.z.w</FileVersion>`。
3. `src/Desktop/BiliCopilot.UI/SponsorBlock/Api/SponsorClient.cs`：更新 `UserAgent = "BiliFluent/x.y.z.w";`。
4. `CHANGELOG.md`：将 `## [Unreleased]` 内容归档为 `## [x.y.z.w] - YYYY-MM-DD`，并在文件底部补充版本比较与 Release 链接。
5. `README.md` 与 `docs/install.md`：更新下载文件名与当前版本号描述。
6. `scripts/README.md`：若产物名发生变化，同步更新对应的包名示例。
7. 合并至 `main` 后，创建对应的 Git Tag（如 `vx.y.z.w`，必须与清单中的版本严格匹配）并推送触发正式发版。

## 安全与隐私规范

- 严禁在代码、提交记录、Issue、日志或文档中提交任何私钥、PFX 证书、Cookie、Token 或开发者本地敏感绝对路径。
- 提交前请仔细自查变更内容，遵循最小权限原则。
