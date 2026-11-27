# 前端自动检查第一批：配置与本地验证

## 当前回归入口

每次向 main 提交或发起拉取请求时，Console CI 会从锁文件安装依赖并构建生产控制台，随后安装固定版本的 Playwright 和 Chromium，执行 `node scripts/check-console-browser.mjs`。该命令自动发现 `console/tests/*.browser.mjs`，并在回环地址提供本次构建的页面。单个流程失败、全部跳过或套件缺失都会使检查失败。工作流超时为 30 分钟。

| 回归内容 | 运行环境与入口 |
| --- | --- |
| 控制台页面及权限交互 | 已安装的 Node、Playwright、Chromium 和生产构建。用 `WEBAPI_PLAYWRIGHT_PACKAGE` 指向包的 package.json。可选的 `WEBAPI_CHROMIUM_EXECUTABLE` 指定本机浏览器，`WEBAPI_BROWSER_RESULTS` 保存逐套件输出。浏览器使用受控 API 响应。 |
| 服务端契约与数据库 | Contracts CI 分别运行 `bash scripts/check-contracts.sh domain`、`integration`、`gateway`。集成环境使用 Docker、PostgreSQL 和 Redis。 |
| 本地运行工具 | `bash scripts/check-local-runtime.sh unit` 发现全部顶层运行工具用例和前端单测。Docker 生命周期、卷和回环监听在隔离宿主用 `bash scripts/check-local-runtime.sh integration` 串行执行。 |
| 整套服务端到端 | 在具备 Docker Compose、可用测试端口、镜像、SDK 与独立存储的主机，先构建控制台，再运行 `bash scripts/e2e.sh`。它启动临时数据库、控制面、Worker、双网关和两个后端，执行 WebApi.EndToEnd.Tests 的核心流程。监控端到端使用 `bash scripts/check-observability-loop.sh e2e`，外部 SSO 与现场恢复使用相应部署手册的专用环境。 |

核心 Compose 会把本次运行的 owner 和实际源码提交传给 SDK 容器。带有 `Observability`、`Sso`、`Settings`、`Policies`、`NetworkPolicies` 标记的用例需要各自的运行环境与输入。对应入口为 `bash scripts/check-observability-loop.sh e2e`、`bash scripts/check-sso.sh e2e`、`bash scripts/check-settings.sh e2e`、`bash scripts/check-policies.sh e2e` 及[网络策略验收手册](network-policies-runbook.md)。专用运行器负责生成本轮回执，再交由对应的 SDK 用例核对。

日期：2026-11-10。配置阶段输入：主干 `7626115c769ae0926d38109ace46b2f5017bfcc3`。用户选择 A 路径，首批仅前端，三个后端套件为第二批。**后续状态：工作流已通过 PR #5 合入，修正候选和 main 的实际检查均通过；2026-11-11 只读确认四项主干必需检查已启用。**下文保留首批本地验证的原范围与结果，后续结果见文末。

## 本批配置

入口：[`.github/workflows/console-ci.yml`](../../.github/workflows/console-ci.yml)。一个检查 `console`，在 `ubuntu-24.04` 上执行：

1. checkout 事件对应源码，不持久化 Git 凭据。
2. 安装 Node `24.22.1`；安装 pnpm `11.25.0`。两版本与本轮本地工具一致，已核对上游 tag；本批不升级产品依赖。
3. `pnpm --dir console install --frozen-lockfile`，按现有锁文件及 `pnpm-workspace.yaml` 执行。
4. `bash scripts/check-console.sh`，先完整前端测试，再 TypeScript 和 Vite 构建。脚本、测试和锁文件均不修改。

触发：针对 main 的 PR、main push、手动执行。没有路径过滤，没有 `pull_request_target`；同一 PR 的新提交取消旧轮次，独立 PR/分支分别分组。15 分钟超时，未设置失败忽略或自动重试。

默认令牌权限仅 `contents: read`。不配置产品秘密、部署步骤、依赖缓存或工件上传。工具安装只发生在一次性 runner，不改本机全局工具。

## 固定 Action 来源

| Action | 固定官方提交 | 在线核验 |
| --- | --- | --- |
| checkout | `3d3c42e5aac5ba805825da76410c181273ba90b1` | 官方 [v7.0.1](https://github.com/actions/checkout/releases/tag/v7.0.1)，`action.yml` 使用 node24 |
| setup-node | `949feb2413d6458794dcd2491c4babbbce0c15c1` | 官方 [v7.1.0](https://github.com/actions/setup-node/releases/tag/v7.1.0)，`action.yml` 支持固定 node-version 与关闭隐式缓存 |

工作流不使用浮动 Action tag。输入参考 [GitHub 工作流语法](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)与[安全引用说明](https://docs.github.com/en/actions/reference/security/secure-use)。

## 本次实际验证

环境：本机 macOS 独立工作目录，Node `v24.22.1`、pnpm `11.25.0`。不是 Ubuntu/GitHub 验收。

| 检查 | 结果 |
| --- | --- |
| 工作流 YAML 解析及权限、范围、固定引用、步骤、并发和超时核对 | 通过；配置前已确认工作流缺失 |
| 干净锁定安装 | 独立空 node_modules 与空依赖缓存，下载 73 个依赖、0 复用，安装成功；esbuild postinstall 成功 |
| 前端测试 | 321 通过、0 失败、0 取消、0 跳过 |
| TypeScript 与 Vite 构建 | 通过；6405 个模块转换完成 |
| 原脚本、package.json、pnpm 锁文件及 workspace 配置 | 与输入提交一致 |
| 主目录保护 | 8 份前轮文档、另 3 个原有未跟踪文件共 11 份内容的 SHA-256 未变 |
| 独立只读审查 | Critical 0、Important 0、Minor 0；允许准备本地提交及首次远程评审，不代表 GitHub 运行通过 |

原始本地安装、测试和构建输出保留在本工作目录忽略的 `.superpowers/sdd/2026-11-10-console-ci-phase1/`，不加入公开工件。辅助 YAML 核对工具首次遇到本机 Ruby 2.6 不支持 `filter_map`，改用兼容写法后完成核对；产品和工作流未因此调整。

本轮没有把以下已有日志静默算作“无告警”：

- 两个现有 SSR 用例仅关闭 hmr，未关闭 Vite ws；并行时记录 `WebSocket server error: Port 24678 is already in use`。321 个实际断言全部通过，但该日志仍保留。本批没有修改用例或占用端口的其他进程，也未证明 Ubuntu 无此日志。
- Vite 输出单个 JS chunk 超过 500 kB 的构建警告（约 798 kB）。构建成功，本批未提高警告阈值或调整应用拆包。

## 首批交付范围与后续实际结果

首批提交仅包含工作流、本设计/计划和本文，不包含主目录前轮尚未提交的八份文档修正。首批当时没有执行三个后端套件、固定端口 E2E、安全准入或 4192 部署；后端后续结果见[第二批记录](contracts-ci-phase2.md)。

后续经明确授权发布并合入 [PR #5](https://github.com/leileipei/WebAPI-Platform-Enterprise/pull/5)。修正候选 `7694c4a` 的 [PR Console CI](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42828796307) 和 main `3774ccc` 的 [push Console CI](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42839001626) 均实际完成：各 321 通过、0 失败/跳过，类型检查与构建通过，runner 为 `ubuntu-24.04`。这两次结果与上述本机验证分别记录；不据成功结论抹去既有告警。

2026-11-11 最新只读读取已确认 `console` 列为 main 必需检查，本轮未写入规则；其余完整保护设置仍需相应管理接口核验。完整当前状态见[状态页](current-project-status.md)。

设计：[首批设计](../superpowers/specs/2026-11-10-console-ci-phase1-design.md)。计划：[首批实施计划](../superpowers/plans/2026-11-10-console-ci-phase1.md)。
