# 前端自动检查第一批：配置与本地验证

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
