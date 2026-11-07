# Governance Snapshot 2.4 Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans for the user-selected current-session execution. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将治理工作区经验证的授权、凭证和安全状态能力整合到当前主线，同时保留网络策略 2.3、原有发布与轮换行为。

**Architecture:** 以主线为起点，在隔离候选逐项整合治理源码；新增可区分的 2.4 协议，以可信环境状态、安全账本及节点租约控制发布和恢复。数据库升级分为主线历史兼容、治理历史阻断、具备身份和写栅栏的隔离验证。

**Tech Stack:** .NET SDK 10.0.401、C#、ASP.NET Core、EF Core／PostgreSQL、Redis、React／TypeScript、Node.js、xUnit。

**Spec:** `docs/superpowers/specs/2026-11-04-governance-snapshot-24-integration-design.md`，SHA-256 `d0f1c8293fb2255a826dc5c31eb353dc25a0ab4dfb2d6196fa1809549a925cad`。

## Global Constraints

- 主线基线和 GitHub `main`：`369139df0fde68c8309849038e7f7eb7c58955a1`；治理基线 `40759aac50f1a6281223b0ec0d45d58e6fb44cb5`，以已核对的 433 文件清单和原始 SHA 为输入。
- 原治理工作区、历史失败和证据不修改；治理侧 22 个迁移草稿不直接并入或伪装为已应用历史。
- 网络策略 2.3 原始字节与行为保留；治理协议 2.4 不允许旧节点误识别或越过当前安全下限。
- 旧 PUT 撤销仍是工作配置，需审批发布；只有独立即时撤销或明确确认的正式轮换写安全撤销账本。
- 任意验证失败保留原始日志；不推送、不部署、不操作 4192、4194 及既有服务。G14／第 2 包资格状态独立保留，不启动新的性能 A/B。
- 不下载新依赖；候选使用本机已核验 SDK 镜像和只读 NuGet 缓存。真实 PostgreSQL、Redis 和网关测试仅使用独立 owner、卷及端口。

## Review Focus

1. 2.3 混合字段、重复 JSON 键或缺失独立来源不能绕过 2.4 安全下限；任务 2、4 的拒绝测试覆盖。
2. 旧实例、旧控制面、未知环境或缺失租约不能把治理状态当成兼容状态；任务 3、4、6 的节点与写栅栏测试覆盖。
3. 普通撤销、即时撤销和历史全局凭证作用域不得混淆；任务 5 的状态及跨环境测试覆盖。
4. 自动轮换未确认、截止故障和新钥未验证不能产生错误放行或伪造成功；任务 8 的时间与证明测试覆盖。
5. 数据迁移中断、草稿历史和独立 SQL 脚本不能破坏已有数据；任务 3、10 的升级／阻断测试覆盖。

## Task 1: 固定输入与基线

**Files:** 读取 `outputs/governance-audit-20261103-7b3togm3/workspace-files.json`、`outputs/merge-check-20261104-apzqzhfd/merge-simulation.json`；候选内保留本计划及规格。

**Interfaces:** 输入为 `mainCommit`、`baseCommit`、每文件 `workingSha256` 和冲突分类；输出为仅含产品源码、测试及必要文档的可复核导入清单，区分迁移草稿、证据及秘密。

- [x] 重新校验两端 HEAD、433 个文件 Hash、治理未暂存状态及 G14 检查点；任何变化停止并重新审查。
- [x] 对 46 个冲突、16 个文本可合并文件、51 个主线未变文件及 3 个新增／新增差异逐项归属后续任务；不因文本可合并而直接判业务正确。
- [x] 离线运行当前主线的前端类型、策略／权限／轮换测试及领域测试；记录精确 SDK、缓存身份、命令和所有结果。

## Task 2: 2.4 wire 格式和方言分类

**Files:** 修改 `src/WebApi.Contracts/Runtime/SnapshotContracts.cs`、`src/WebApi.Domain/Runtime/SnapshotValidator.cs`；创建 `src/WebApi.Domain/Runtime/SnapshotDialectClassifier.cs` 和 2.4 治理校验器；测试 `tests/WebApi.Domain.Tests/NetworkPolicySnapshotTests.cs`、`SnapshotGovernanceTests.cs`、新增 `SnapshotDialectTests.cs`。

**Interfaces:** 定义 `SnapshotDialect`（旧 2.0—2.2、网络 2.3、治理 2.3、治理 2.4、非法）和 `SnapshotDialectClassifier.Classify(ReadOnlySpan<byte> payload)`；返回 wire 形态，不自行证明环境可信。2.4 DTO 保留八个现有字段并加入四个必需治理字段；`LegacyGlobal.environmentId` 明确序列化为 null。

- [x] 编写失败测试：现有网络 2.3 原始字节可解析；2.3 四字段全部缺失与全部存在分别分类；部分字段、大小写冲突／重复安全键、非法嵌套及跨环境绑定被拒绝。
- [x] 编写失败测试：2.4 可同时包含 CORS、IP、Transform 和治理字段；丢失四字段任一项被拒绝；`LegacyGlobal` 的显式 null 可往返；失效路由修订保留但无法授权。
- [x] 实现 classifier、DTO 与分协议 wire／语义校验，保留旧快照 Hash、16 MiB 上限、非安全未知字段既有兼容行为及旧版字节。
- [x] 执行域测试及既有 `NetworkPolicySnapshotTests`；断言旧／新样本的分类、拒绝和 Hash 均符合规格。

## Task 3: 数据模型、初始化证明与迁移门禁

**Files:** 修改 `src/WebApi.Infrastructure/Persistence/Entities/`、`Configurations/`、`WebApiDbContextModelSnapshot.cs`、`deploy/initial-core.sql`；基于主线新增有序迁移；新增 `src/WebApi.Infrastructure/CredentialSecurity/LegacyEnvironmentAttestationService.cs` 与迁移前检查；参考治理侧 `CredentialMigrationTests.cs`，新增 `tests/WebApi.Integration.Tests/Governance24MigrationTests.cs`。

**Interfaces:** 环境状态区分 `Unknown`、`FreshNonStrict`、`VerifiedLegacyNetwork`、已治理；迁移前检查返回 `Supported` 或附原因的 `Blocked`，且不可修改业务／迁移历史。凭证工作状态与即时撤销事实分开存储。

- [ ] 编写 PostgreSQL 失败测试：主线祖先历史及空库允许；存在任一治理草稿迁移、治理专属结构、未知历史或不一致时，EF 和 SQL 入口均在第一项写入前阻断。
- [ ] 将经清单核对的治理实体／约束按当前模型重建到主线之后的迁移；支持 2.4 `CHECK`，不改主线已应用迁移或旧账本事件。
- [ ] 编写初始化证明测试：证据齐全且 `environment.write` 与 `gateway.config.read` 授权时可确认；缺证、证据变化、旧实例和已治理向非严格回退均拒绝。
- [ ] 验证主线已有账号、凭证、旧快照、统计与业务行在迁移后身份、内容和约束保持；验证旧数据库角色或旧进程无法在迁移后写入。

## Task 4: 节点能力、签名状态和防降级

**Files:** `src/WebApi.Infrastructure/Gateway/SnapshotSchemaCapabilities.cs`、`NodeRegistry.cs`、`AckService.cs`；治理侧 `SecurityProtocolCapabilities.cs`、`CredentialSecurity/SecurityLedgerReader.cs`、`SecurityPolicyRules.cs`、`SecurityLeaseIssuer.cs`；网关 `Security/SecurityStateStore.cs`、`Configuration/SnapshotActivation.cs`。测试 `tests/WebApi.Gateway.Tests/SecurityStateTests.cs`、`GatewaySecurityAckTests.cs` 和相关集成测试。

**Interfaces:** 节点支持集合包含 2.0—2.4；2.4 同时要求与当前实例绑定的 `security-ledger-v1`。旧治理最低 2.3 通过语义映射比较，不通过字符串大小比较。

- [ ] 测试五个版本声明、伪造／旧实例、缺失心跳、混合节点、所有启用目标和至少双节点门槛；节点变更后旧 ACK、租约与采集事实失效。
- [ ] 测试已证明非严格环境可获当前实例签名租约；`Unknown`、旧控制面、新节点重启或签发不可用时受保护请求拒绝。
- [ ] 移植并整合节点注册、账本、签名 checkpoint、租约、高水位和 LKG；保留主线 NodeRegistry 的采集器与运行确认清理。
- [ ] 测试历史安全下限、墓碑、时钟回拨、持久化失败及过期租约均不能由旧网络 2.3 绕过。

## Task 5: 凭证工作状态与即时撤销

**Files:** `src/WebApi.Infrastructure/Applications/ApplicationService.cs`、`CredentialImpactService.cs`；治理侧 `CredentialRevocationService.cs`、`CredentialRevocationWriter.cs`、`LegacyCredentialUsage.cs`；`src/WebApi.ControlPlane/Applications/ApplicationEndpoints.cs`。测试新增 `tests/WebApi.Integration.Tests/StagedAndImmediateRevocationTests.cs`。

**Interfaces:** 原 PUT 保持 `credential.manage` 与影响确认，只更新工作配置；`POST /credentials/{id}/revoke` 要求 `credential.revoke`，追加不可回退的即时撤销事实。全局凭证按所有关联环境原子处理。

- [ ] 测试 PUT 前后已发布网关继续按旧快照运行，审批发布和双节点 ACK 后才执行工作撤销；409／412 和一次性 Secret 合同保持。
- [ ] 测试即时撤销的权限、全部当前 2.4 节点／入口、签名账本和状态；旧／混合节点或未隔离入口返回 409 且不创建操作。
- [ ] 测试全局凭证覆盖当前授权、映射、冻结候选与历史快照；无节点环境仅可证明隔离后封存，遗漏、不可读、未授权或账本异常导致全事务阻断。
- [ ] 实现工作状态／即时墓碑分离及原子写入；测试旧快照、回滚、新环境和重新启用无法绕过已确认即时撤销。

## Task 6: 发布、审批、回滚和网关链

**Files:** `ReleaseCandidateBuilder.cs`、`SnapshotCompiler.cs`、`PublishCoordinator.cs`、`HistoricalSnapshotService.cs`、`RollbackService.cs`、`ReleaseRecoveryService.cs`、`GatewayApp.cs`、`ApiKeyMiddleware.cs`、`RuntimeGenerationStore.cs`；现有发布／网关与网络策略测试。

**Interfaces:** 所有入口使用任务 2 的方言分类及任务 4 的可信能力／账本判断；严格回滚从历史业务配置生成携带当前安全状态的 2.4 新候选。

- [ ] 测试预览、冻结、发布、ACK、跨环境审批、失败重试及 LKG 逐入口拒绝不兼容版本／已变化的 generation、epoch、目标或权限。
- [ ] 测试首次 2.4 切换需要全量可信冻结，不删除未选择的 API／Cluster／策略，也不虚构授权；严格环境不能直接回滚旧网络 2.3。
- [ ] 逐语义整合主线 Header 清理、generation、IP／CORS、认证、Transform、JSON、缓存、熔断、重试和治理租约／RouteMethod；测试 ApiKey／JWT／匿名的 401／403／503、预检、变换与并发。
- [ ] 测试旧网关不 ACK 2.4，安全状态缺失时不借缓存或旧 LKG 放行；至少双节点的真实确认归任务 10。

## Task 7: 主线统计与治理证明

**Files:** 主线 `src/WebApi.Gateway/CredentialUsage/`、`src/WebApi.Infrastructure/CredentialUsage/`；治理侧 `src/WebApi.Gateway/Observability/CredentialUsageRecorder.cs` 与采集证据；`ApiKeyMiddleware.cs`。测试现有 `CredentialUsageRecordingTests.cs` 及新增 `DualUsageSemanticsTests.cs`。

**Interfaces:** 主线每环境认证及 API 授权通过次数保留；治理证明绑定环境、当前节点实例、发布序号／Hash 与安全租约，只有最终 200—399、无传输失败／中断的请求可作为成功证明。

- [ ] 测试缓存命中及授权后的 429／5xx 对主线各计一次、重试不重计；其中 429／5xx 不构成治理轮换成功证明。
- [ ] 测试无效、过期、403、JWT／匿名及预检不计主线凭证使用；工作已撤销但旧已发布快照仍允许时按实际使用记录。
- [ ] 证明两套统计不相加、不以小时汇总代替单实例证明；WAL／批次去重、缺报、重启与容量故障维持各自合同。

## Task 8: 正式轮换及截止

**Files:** 治理侧 `CredentialRotationService.cs`、`CredentialRotationProcessor.cs`、`CredentialRotationMachine.cs`、`SecurityLeaseIssuer.cs`；主线轮换 UI、`console/src/applications/rotation-state.mjs`；测试 `RotationTests.cs`、`RotationProcessorTests.cs`、`RotationDeadlineRuntimeTests.cs` 和 `console/tests/rotation-state.test.mjs`。

**Interfaces:** 正式轮换只接受 `EnvironmentBound`，需要明确自动撤旧确认、预览指纹、权限和硬截止；普通轮换不创建自动任务。

- [ ] 测试未确认、旧／混合节点和风险变化被拒绝；目标变更不复用旧实例证明。
- [ ] 测试重叠结束即使已验证也不自动撤旧；人工完成可提前撤旧；硬截止到达后即使未验证仍发起撤销。
- [ ] 测试既发租约不得跨越截止减时钟偏差；故障时停止续签并返回 503、恢复后幂等撤销；已准入请求不被误报为截止后新准入。
- [ ] 测试中止／截止竞态、部分 ACK 与 200—399 成功请求证明；界面明确显示可能影响同环境其他受保护流量，不泄露 Secret。

## Task 9: 页面与 API 合同整合

**Files:** `console/src/api/types.ts`、`console/src/pages/Applications.tsx`、`Credentials.tsx`、`Approvals.tsx`、`ReleaseDetail.tsx`；`console/src/applications/` 及对应测试。

**Interfaces:** 保留主线 `rotationStep`、`canAdvanceRotation`、`maskAccessKey` 等公开导出；新增治理视图不替代原接口。普通／正式轮换、工作／运行／即时撤销状态和统计完整性可区分。

- [ ] 为 3 个新增／新增冲突编写双侧导出兼容测试；整合未冲突且有调用证据的治理组件，不批量复制原工作区。
- [ ] 测试权限不足、范围变化、风险变化 409、一次性 Secret、跨环境和错误状态；运行 TypeScript 与 Node 回归。
- [ ] 在独立测试环境按 1440px 核对应用、凭证、轮换、审批和发布交互；截图只记录脱敏材料。

## Task 10: 固定候选全链路验证

**Files:** 现有 `tests/WebApi.Domain.Tests/`、`WebApi.Gateway.Tests/`、`WebApi.Integration.Tests/`、`WebApi.EndToEnd.Tests/`；隔离证据输出到新 owner 目录。

**Interfaces:** 所有运行结果绑定候选内容、SDK／镜像身份、独立数据库与节点身份；失败和诊断证据不可覆盖。

- [ ] 离线锁定恢复、构建所有受影响应用；运行完整领域、网关、集成、端到端与前端回归，保留首次失败及复验日志。
- [ ] 在独立 PostgreSQL 执行空库、受支持主线升级、故障重入、治理草稿阻断及独立 SQL 路径；比较行身份、Hash、迁移历史、约束和索引。
- [ ] 启动隔离双网关，覆盖新旧节点、策略／授权、发布 ACK、跨环境、轮换截止、旧入口隔离和回滚；不启动 G14／性能 A/B。
- [ ] 核验自有容器／卷清理与原环境身份；测试无法运行或失败即保留未完成状态。

## Task 11: 审阅与主线合并

**Files:** 精确产品清单、候选差异、测试证据及原始输入 Hash；原通知恢复分支保持独立。

**Interfaces:** 只有 2.4 候选所有必要门禁通过、独立代码审查完成且 main／远端身份仍符合基线，才可提交候选并合入本地主线；不推送、不部署。

- [ ] 审查所有冲突处置、依赖锁、迁移及安全边界，确认不存在未审阅证据、秘密或无关文件。
- [ ] 重新验证主线干净、治理原件及 G14 历史不变；若主线或 GitHub 已前进，重新评估差异并复验。
- [ ] 完整门禁通过后按用户授权提交并合并；记录提交、测试及任何未完成的外部验收。通知恢复旧分支只保留，不做空合并。
