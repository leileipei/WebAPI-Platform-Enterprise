# 当前源码与验证状态

核对日期：2026-11-11。本页以 GitHub 实际主干和本轮资料整理的代码输入为依据，统一替代各历史“当前索引”的现状说明。资料整理只修改文档，不改变产品实现或安装环境。

## 版本与事实来源

| 层次 | 已核实的版本或状态 | 说明 |
| --- | --- | --- |
| [GitHub main](https://github.com/leileipei/WebAPI-Platform-Enterprise/tree/3774ccc7fd97a59ff4851d3a9f55179c4635c61f) | `3774ccc7fd97a59ff4851d3a9f55179c4635c61f` | 本次在线读取；[PR #5](https://github.com/leileipei/WebAPI-Platform-Enterprise/pull/5) 已合入，四项主干检查全通过 |
| 本地主分支 | `3774ccc7fd97a59ff4851d3a9f55179c4635c61f` | 已同步远端；原目录 14 份未提交文件及其状态保留 |
| 本地资料整理工作树 | 产品及 CI 输入与上述 main 一致 | 资料整理、合并后状态更新及需求复核仅在本地，不包含在 PR #5 中 |
| 其他本地目录和分支 | 含未提交文档、补验资料及不同阶段的工作目录 | 不自动并入本页结论，不推断分支名对应功能未实现或已交付 |
| 持久本机安装 | 本轮未现场核对 | 当前源码不能代替镜像、维护工具、数据和健康的实际身份 |
| 生产验收 | 无本轮新增验收 | 企业 TLS/网络、HA、AMD64、容量/SLA及业务验收仍需目标环境证据 |

### 已通过 PR #5 合入的五个提交

| 提交 | 内容 | 作用范围 |
| --- | --- | --- |
| `536efe1` | 前端自动检查配置与本地验证说明 | 新增 `console` 工作流，未改变产品实现 |
| `8b75524` | 三套后端自动检查配置与说明 | 新增 `contracts-*` 工作流，未改变产品实现 |
| `b73357e` | 纠正安全能力、安装和验证状态的文档 | 不启用安全准入、不部署 |
| `00f45a8` | 修复授权迁移审计越界与 Outbox 恢复阻塞 | 三个实现文件、两个回归测试文件 |
| `7694c4a` | 将 Integration 作业总上限从 45 分钟调为 60 分钟 | 仅工作流一行；Domain/Gateway 仍为 45 分钟，产品及测试限制不变 |

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

以下区分实际 GitHub 运行与既有固定版本验证。本轮状态更新核对已完成的运行及完整日志，没有重新执行产品测试。

| 输入与记录 | 实际结果 | 不能外推的结论 |
| --- | --- | --- |
| 合并后 main `3774ccc`，push 触发；[Console CI](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42839001626)、[Contracts CI](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42839002248) | Console 321、Domain 1251、Gateway 555、Integration 2128，合计 4255 通过，0 失败/跳过；前端类型检查和构建通过 | 是该提交在托管 runner 上的四套检查，不代表 E2E、AMD64 或生产验收 |
| PR 修正候选 `7694c4a`；[Console CI](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42828796307)、[Contracts CI](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42828796227) | 同样 4255 通过，0 失败/跳过；之后才合并 | PR 与 main 是两次独立验证，不相加为一个总数 |
| [前端首批](console-ci-phase1.md)，`536efe1` | 321 通过，0 失败/跳过；类型检查与构建通过 | 本机 macOS 结果，不是 GitHub runner 结果；SSR 端口及大 chunk 告警保留 |
| [后端第二批](contracts-ci-phase2.md)，配置阶段产品基线 `7626115` | Domain 1251、Gateway 555、Integration 2115，合计 3921 通过，0 失败/跳过 | 固定 SDK 10.0.401、Linux ARM64 容器；Integration 本身耗时 34 分 21 秒 |
| 本地代码 `00f45a8` 的 P2 修复回归 | 50 通过，0 失败/跳过；含 13 个新增回归场景 | 定向验证，不是修改后全量重跑 |
| 同一 `00f45a8` 的关联路径复核 | 29 份源码检查；通知、凭证、导入和流水线相关 94 项通过，0 失败/跳过 | 有限范围复核，不是全仓库安全审计 |
| [网络策略固定交付](../evidence/network-policies/2026-11-10-c946bbb/delivery-index.md)，`c946bbb` | 五组 4540 通过；SDK E2E 1 通过；[45 项工件清单](../evidence/network-policies/2026-11-10-c946bbb/delivery-manifest.json) | 属于该固定版本，不改标为本地修复后的完整验收 |

50/94 项的原日志在本地忽略目录保存；修复对应的回归入口为[审计测试](../../tests/WebApi.Integration.Tests/AuditPermissionMoveScopeTests.cs)与[分发隔离测试](../../tests/WebApi.Integration.Tests/OutboxFailureIsolationTests.cs)。当前完整四套结果以 main 的 4255 项为准；不把历史 321、3921、50、94、4540 与其相加。

## GitHub 实际状态

- PR #5 已合并。2026-11-11 最新只读读取确认 `main` 的 `protected` 为 `true`，`console`、`contracts-domain`、`contracts-integration`、`contracts-gateway` 四项状态检查均列为必需，接口报告 `enforcement_level: everyone`；仓库规则集列表为空，保护来自分支设置。本轮没有写入远端规则，也未重新统计开放 PR 数。
- [前端工作流](../../.github/workflows/console-ci.yml)和[后端工作流](../../.github/workflows/contracts-ci.yml)已公开，修正后的 PR 及 main push 检查均完成。Console 使用 `ubuntu-24.04`，后端三套使用 `ubuntu-24.04-arm`。
- 首次 [Contracts CI](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42820580697) 的 Integration 因超过 45 分钟作业上限被取消，无完整测试汇总；该次不算通过。经明确授权追加 `7694c4a` 后，PR Integration 测试耗时 55 分 9 秒，main 为 52 分 44 秒；两次均在 60 分钟作业上限内完成。测试耗时不等于整个作业耗时。
- [主干保护方案](github-quality-gates-proposal.md)中的真实运行及四项必需检查均已有实际记录；PR 入口、分支新鲜度、审批、强推/删除和管理绕过等完整设置仍未由本轮分支摘要接口核实，不能推断整份提案已经实施。
- 原批准范围的五个提交、21 个文件已发布并合入。资料整理提交、本地原始日志及原目录未提交资料未公开；本次更新不推送、修改规则或部署。

## 目前进度与下一步

已完成源码层的两项 P2 修复、两批 CI 接入、PR 全量验证、主干合并及主干四套复跑。2026-11-11 又完成[454 项需求追溯与 58 项历史缺口的静态复核](requirements-current-review.md)：50 项仍有实现或呈现差距、6 项缺针对性验收、1 项需澄清语义、1 项与发布模型冲突。随后核对 23 个重点单元，完成 39 项本地离线测试及 20 方法/26 用例的隔离 HTTP/PostgreSQL 定向回归，均零失败/跳过；随后从固定源码构建临时镜像，完成17个1440px真实页面检查点，五组场景部分实测通过，完整验收仍待补齐；首次测试启动遗漏参数的失败及待核验标记保留，测试服务已停止。LKG 管理视图缺口的分类已据源码更正。未新增需求验收关闭，不能将上述完成项表述为整个项目已交付。

| 未完成项 | 当前依据与处理边界 |
| --- | --- |
| 主干完整规则核对 | 四项必需检查已只读确认启用；其余 PR/审批/严格同步/强推等配置仍需完整管理接口核实 |
| 资料公开同步 | 原五个资料整理提交、合并后状态更新及本次需求复核尚未公开；先审查差异及公开内容范围 |
| 凭证安全准入 | 签发入口仍关闭、网关准入未接线；既有设计与组件不能代替生产路径验证 |
| 需求逐项验收 | 454 项已建立当前源码追溯；328 项引用文件未变、126 项有变化，涉及 16 个历史已关闭项。历史 33/40/18/363 状态保留，未重新授予当前验收，尚不能给出可靠的完成百分比 |
| 实际安装与生产验收 | 本轮未升级；目标环境身份、TLS/HA/AMD64、容量及业务验收仍需各自证据 |

## 历史记录与待处理项

- [需求台账](../evidence/work-package4/requirements-status.md)的 454 单元及其原关闭状态属于历史基线，不能直接换算当前项目完成率。其他目录中的未提交补验不能冒充 GitHub 已收录的事实。
- [G14 故障矩阵](governance-g14-fault-matrix.md)、原失败及 `Unknown` 保持原版本和原资格；第 2 包暂停状态不因本轮整理解除。
- 原 4192 安装记录从[运维目录](README.md)查找；仅凭源码提交或旧 `Ready` 记录不能推断今天的运行状态。
- 四个旧 ZIP 下载入口的文件当前缺失，已改成不可用的历史包名；历史归档和计划中其余失效引用在[资料清单](../documentation-index.json)登记，不补造文件。
- 旧资源清单中的一个历史容器缺失，原因及发生时间未确认；后续关联验证以自己的起始清单核对 185 个容器、473 个卷和 36 个网络均保留，不倒填旧清单。
