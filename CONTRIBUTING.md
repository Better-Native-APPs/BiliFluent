# BiliFluent 贡献指南

感谢参与 BiliFluent 项目！在提交代码或 Pull Request 前，请阅读本指南。

## 分支策略与发布流程

- **`dev` 分支**：日常开发分支。所有新功能开发、性能优化与缺陷修复均提交至 `dev` 分支（或在个人 Fork 中以 `dev` 为基线创建特性分支后向 `dev` 发起 PR）。
- **`main` 分支**：受保护的发布主分支。集中完成一批变更后再开 `dev` → `main` PR，待 `build` 通过，在本地 fast-forward 后推送；不使用 GitHub 网页合并。
- **预发布（Pre-Release）**：清单版本第四段大于 0 时，代码推送至 `main` 自动发布已签名预发布包，tag 为 `v<version>-pre`，不再使用构建序号。
- **正式发布（Release）**：清单版本第四段为 0 时，推送至 `main` 只构建打包；推送与清单一致的正式 tag `v<version>` 才发布正式版。
- **手动运行**：`workflow_dispatch` 按所选 ref 套用相同规则。CI 自动创建的 `-pre` tag 不触发正式发布。

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
- 合并至 `main` 前，将 `[Unreleased]` 改名为 `## [<version>] - YYYY-MM-DD`，预发布可在日期后加“（预发布）”，并重新留下空的 `[Unreleased]`。更新底部的比较与 Release 链接。
- 预发布小节只记录相对上一个发布（预发布或正式）的变化；正式版小节汇总自上一个正式版以来的全部变化。两类 Release 都只读取本次版本小节，不读取 `[Unreleased]`。
- 正式发布时，将本周期各预发布小节与尚未发布的条目合并整理进正式版小节，删除已合并的预发布小节，避免文件内重复；各预发布 GitHub Release 页面和 tag 保留。

## 发版与版本号升级清单

应用版本号唯一来源是 `src/Desktop/BiliCopilot.UI/Package.appxmanifest` 的 `Identity Version`。采用四段 `a.b.c.d`：`d = 0` 为正式版，`d > 0` 为预发布版。正式版 `a.b.c.0` 之后依次发布 `a.b.c.1`、`a.b.c.2`…，下一正式版为 `a.b.(c+1).0`。每次清单版本必须严格大于所有已有发布 tag 所代表的包版本（包括旧格式预发布 tag）；CI 不自动编号。脚本、产物名、标题和 tag 从清单派生，文档示例统一使用 `<version>`，不逐处填写当前版本。

### 合并至 main 前

1. 预发布只提升清单版本第四段；不得复用已发布 tag 或包版本。
2. 将 `[Unreleased]` 归档为与清单同名、有日期且内容非空的版本小节，并保留空的 `[Unreleased]`。
3. 开 `dev` → `main` PR，等待 `build` 通过。非纯文档 PR 若未提升版本，或缺少对应 CHANGELOG 小节，会在构建前失败；纯文档 PR 仍跳过构建。
4. 确认 `main` 是 `dev` 的祖先，按下面的本地快进流程推送，不生成网页合并提交：

   ```sh
   git fetch origin
   git merge-base --is-ancestor origin/main dev
   git push origin dev:main
   ```

### 正式发布

1. 在 `dev` 将清单版本改为 `a.b.(c+1).0`，确保大于本周期所有预发布包。
2. 合并本周期预发布小节为正式版小节，补入未发布条目，删除重复的预发布小节。
3. PR 构建通过后按上述流程 fast-forward `main`；此时 CI 只构建打包。
4. 在该 `main` 提交打正式 tag 并推送，tag 必须等于 `v` 加清单版本、指向同一提交：

   ```powershell
   git fetch origin
   git switch main
   git merge --ff-only origin/main
   [xml]$manifest = Get-Content ./src/Desktop/BiliCopilot.UI/Package.appxmanifest -Raw
   $tag = 'v' + $manifest.Package.Identity.Version
   git tag -s $tag
   git push origin $tag
   ```

   正式 tag 触发校验时只排除本次 tag 自身；其余历史包版本仍必须更低。后续有新发布时，不重跑旧版本发布。

## 安全与隐私规范

- 严禁在代码、提交记录、Issue、日志或文档中提交任何私钥、PFX 证书、Cookie、Token 或开发者本地敏感绝对路径。
- 提交前请仔细自查变更内容，遵循最小权限原则。
