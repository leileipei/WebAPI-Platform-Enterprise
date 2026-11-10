# 当前源码与验证状态

核对日期：2026-11-10。本页以 GitHub 实际主干和本轮资料整理的代码输入为依据，统一替代各历史“当前索引”的现状说明。资料整理只修改文档，不改变产品实现或安装环境。

## 版本与事实来源

| 层次 | 已核实的版本或状态 | 说明 |
| --- | --- | --- |
| [GitHub main](https://github.com/leileipei/WebAPI-Platform-Enterprise/tree/7626115c769ae0926d38109ace46b2f5017bfcc3) | `7626115c769ae0926d38109ace46b2f5017bfcc3` | 本次在线读取；远端尚未包含下面四个本地提交 |
| 本地代码输入及本地主分支 | `00f45a8617cbf8634e58621594bdbb7e8b0ede16` | 相对 GitHub main 多四个提交；本页后续文档提交不改变此产品代码输入 |
| 其他本地目录和分支 | 含未提交文档、补验资料及不同阶段的工作目录 | 不自动并入本页结论，不推断分支名对应功能未实现或已交付 |
| 持久本机安装 | 本轮未现场核对 | 当前源码不能代替镜像、维护工具、数据和健康的实际身份 |
| 生产验收 | 无本轮新增验收 | 企业 TLS/网络、HA、AMD64、容量/SLA及业务验收仍需目标环境证据 |

### GitHub 与本地之间的四个提交

| 提交 | 内容 | 作用范围 |
| --- | --- | --- |
| `536efe1` | 前端自动检查配置与本地验证说明 | 新增 `console` 工作流，未改变产品实现 |
| `8b75524` | 三套后端自动检查配置与说明 | 新增 `contracts-*` 工作流，未改变产品实现 |
| `b73357e` | 纠正安全能力、安装和验证状态的文档 | 不启用安全准入、不部署 |
| `00f45a8` | 修复授权迁移审计越界与 Outbox 恢复阻塞 | 三个实现文件、两个回归测试文件 |

两项修复的直接入口是[审计范围查询](../../src/WebApi.Infrastructure/Governance/AuditAccessQuery.cs)、[Outbox 分发器](../../src/WebApi.Infrastructure/Messaging/OutboxDispatcher.cs)与[后台分发任务](../../src/WebApi.Worker/Workers/OutboxWorker.cs)。历史审计原文保持不变，读取时逐快照校验真实环境及项目权限；单个环境恢复失败与消息分发分别处理。

## 源码能力及关闭边界

| 能力 | 源码依据 | 当前解释 |
| --- | --- | --- |
| API、路由、策略、应用、审批与跨环境交付 | [控制面](../../src/WebApi.ControlPlane/)、[网关](../../src/WebApi.Gateway/)、[控制台](../../console/src/) | 有对应实现；功能入口不等于全部需求或生产验收通过 |
| 普通凭证轮换和撤销 | [ApplicationService](../../src/WebApi.Infrastructure/Applications/ApplicationService.cs)、[ApiKeyMiddleware](../../src/WebApi.Gateway/Security/ApiKeyMiddleware.cs) | 保存工作配置后须再次审批发布，不能承诺即时拒绝旧 Key |
| 安全检查点/租约签发 | [SecurityLeaseIssuer](../../src/WebApi.Infrastructure/CredentialSecurity/SecurityLeaseIssuer.cs) | 入口仍固定返回 `503 security_evidence_unavailable` |
| 网关安全准入 | [GatewayApp](../../src/WebApi.Gateway/GatewayApp.cs) | 尚未接入安全状态存储及准入链路，不能宣称正式轮换硬截止已上线 |
| G01–G13 安全组件 | [独立评审记录](../evidence/governance-merge/p2-4-crypto-review.md)、[数据字典](../credential-security-data-dictionary.md) | 历史合并与附条件评审保留；不能由表结构、组件或测试存在推导生产路径启用 |

操作解释以[凭证安全手册](credential-security-operations.md)为准。完整资料按[总目录](../README.md)和[运维目录](README.md)查阅。

## 固定版本验证

以下是既有验证记录的复核汇总，本轮资料整理没有重新执行这些产品测试。

| 输入与记录 | 实际结果 | 不能外推的结论 |
| --- | --- | --- |
| [前端首批](console-ci-phase1.md)，`536efe1` | 321 通过，0 失败/跳过；类型检查与构建通过 | 本机 macOS 结果，不是 GitHub runner 结果；SSR 端口及大 chunk 告警保留 |
| [后端第二批](contracts-ci-phase2.md)，产品实现仍为远端基线 | Domain 1251、Gateway 555、Integration 2115，合计 3921 通过，0 失败/跳过 | 固定 SDK 10.0.401、Linux ARM64 容器；Integration 本身耗时 34 分 21 秒 |
| 本地代码 `00f45a8` 的 P2 修复回归 | 50 通过，0 失败/跳过；含 13 个新增回归场景 | 定向验证，不是修改后全量重跑 |
| 同一 `00f45a8` 的关联路径复核 | 29 份源码检查；通知、凭证、导入和流水线相关 94 项通过，0 失败/跳过 | 有限范围复核，不是全仓库安全审计 |
| [网络策略固定交付](../evidence/network-policies/2026-11-10-c946bbb/delivery-index.md)，`c946bbb` | 五组 4540 通过；SDK E2E 1 通过；[45 项工件清单](../evidence/network-policies/2026-11-10-c946bbb/delivery-manifest.json) | 属于该固定版本，不改标为本地修复后的完整验收 |

50/94 项的原日志在本地忽略目录保存；本地修复对应的回归入口为[审计测试](../../tests/WebApi.Integration.Tests/AuditPermissionMoveScopeTests.cs)与[分发隔离测试](../../tests/WebApi.Integration.Tests/OutboxFailureIsolationTests.cs)。不将 321、3921、50、94、4540 相加为一个当前版本的通过总数。

## GitHub 实际状态

- 本次读取：公开仓库，开放 PR 为 0，[Actions](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions) 记录总数为 0，`main` 的 `protected` 为 `false`，规则集为空。
- 本地已有[前端工作流](../../.github/workflows/console-ci.yml)和[后端工作流](../../.github/workflows/contracts-ci.yml)，尚未推送并运行；不能写成“GitHub 全绿”或“必需检查已启用”。
- [主干保护方案](github-quality-gates-proposal.md)仍需先实际运行检查，再配置并读回规则。Integration 的冷启动与总耗时预算尚未得到 GitHub 验证。
- 已完成本地快进集成；公开推送仍待明确的发布授权。资料清理不执行推送、远端规则修改或部署。

## 历史记录与待处理项

- [需求台账](../evidence/work-package4/requirements-status.md)的 454 单元及其原关闭状态属于历史基线，不能直接换算当前项目完成率。其他目录中的未提交补验不能冒充 GitHub 已收录的事实。
- [G14 故障矩阵](governance-g14-fault-matrix.md)、原失败及 `Unknown` 保持原版本和原资格；第 2 包暂停状态不因本轮整理解除。
- 原 4192 安装记录从[运维目录](README.md)查找；仅凭源码提交或旧 `Ready` 记录不能推断今天的运行状态。
- 四个旧 ZIP 下载入口的文件当前缺失，已改成不可用的历史包名；历史归档和计划中其余失效引用在[资料清单](../documentation-index.json)登记，不补造文件。
- 旧资源清单中的一个历史容器缺失，原因及发生时间未确认；后续关联验证以自己的起始清单核对 185 个容器、473 个卷和 36 个网络均保留，不倒填旧清单。
