# TEST→PROD 发布晋级 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成同项目已验收制品到生产的受控晋级，追溯测试、映射、审批、下发、节点确认和生产业务验证。

**Architecture:** 新增交付制品/验收/映射/晋级模块，复用目标Release两级审批及既有发布Worker。目标环境候选从冻结行为和类型化映射构建，执行前再次核验，ACK与交付完成分开持久化。

**Tech Stack:** 现有.NET10、EF Core/Npgsql10、PostgreSQL、Redis、YARP、React19.2、Node22+、xUnit；无新生产微服务，无新第三方依赖。

**Spec:** [用户已确认的发布晋级设计](../specs/2026-10-09-release-promotion-design.md)，前置[环境地址计划](2026-10-09-environment-access-address.md)。2026-10-09 用户回复“确认设计”。

状态：用户已回复“确认计划，按推荐执行”；选定 Native，正在隔离工作区执行。

## Global Constraints

- 同组织项目一条来源非生产→目标IsProduction=true连接，不以Code识别环境。
- 保留现有Release.Succeeded=全冻结节点ACK；Promotion.Completed另需生产验证。
- 制品SHA-256来自canonical API/路由/策略行为，不含上游URL、节点、AccessKey、SecretHash、JWT公钥材料或秘密引用。
- 来源测试类型默认接口功能/集成/契约兼容性；生产验证默认入口连通/认证授权/关键业务调用。有效期默认1440分钟，范围1–10080。
- 生产沿用两级、每级1–5名独立审核人和既有ApprovalEligibility；测试验收人不是申请人，生产验证人不是申请人或实际发布人。
- 报告仅人工登记，PDF/纯文本，每文件不超过10 MiB；不主动读取外部报告链接。
- 旧项目Legacy；显式启用PromotionRequired后正常生产直发阻断，保留原人工审批回滚/原目标恢复。
- If-Match、Idempotency-Key、治理锁、按UUID排序环境锁；执行启动和Worker构建两处复检。
- 修改制品行为需新制品并重测；目标基线/资源/规则/入口变化不得沿用旧批准。
- 不做任意阶段引擎、定时发布、紧急直发、流量灰度、自动指标/回滚或CI/CD接入；原资料/账号/SSO/本地修改保留。

## Review Focus

1. 现有内置角色用Codes.Except自动补权限，新增验收权限不能静默扩给组织/项目管理员；B1角色迁移断言。
2. 多API共享应用，候选显式凭证选择不能移除基线其他路由必需授权或让TEST凭证自动流入PROD；B5共享基线用例。
3. 撤销测试验收与Worker领取构建竞争，排队期间变更不能越过启动时检查；B6/B7并发用例。
4. 失败恢复新Release与正式晋级唯一关联冲突，必须由RecoveryOf追溯；B7恢复/晚到ACK用例。
5. 入口/运行序列改变后提交旧生产验证不能完成交付；B8冻结上下文与实际版本用例。

---

## 基线和执行规范

先完成A计划并固定其验证提交。沿用批准的隔离执行方式，保留A成果及两规格/计划；执行前重核源码树和所有相关修改。每任务依次RED、实现、GREEN/列明回归、只提交隔离本批文件，不能自动推送或覆盖原目录。

后端命令采用 `./scripts/check-contracts.sh domain|integration|gateway --filter ...`，其UUID数据库和SDK源码副本避免共享运行污染。前端使用 `"$WEBAPI_NODE" --test <具体文件>` 与 `./scripts/check-console.sh`。零匹配、构建失败或skip不算行为RED/GREEN。开发阶段不改原4192。

下面类型定义位于Contracts/Releases，服务均放Infrastructure/Delivery；ActorContext来自既有Security。新类型内部ID用Guid、Revision/配置/序列用long，时间使用DateTimeOffset，JSON沿用CanonicalJson.Options。验证类型固定：来源 `InterfaceFunction/Integration/ContractCompatibility`；生产 `EntryConnectivity/AuthenticationAuthorization/CriticalBusinessCall`。未指定的正文大小沿用已有请求限制，不允许无界附件或列表。

## 文件职责

| 文件组 | 职责 |
| --- | --- |
| Contracts/Releases/DeliveryContracts.cs、Domain/Delivery | 交付DTO、策略模板、纯状态/行为规则 |
| Infrastructure/Delivery | 制品、证据、验收、目标准备、预检、晋级和生产验证各单元 |
| Persistence/Entities、Configurations、Migrations | 不可变事实、修订、唯一关联和索引 |
| ControlPlane/Releases/DeliveryEndpoints.cs | 新接口、权限、ETag/幂等/分页 |
| 原Releases/Gateway服务与Worker | 在现有执行链上接入门禁、投影和恢复追溯 |
| console/src/delivery | 独立页面、映射向导、资格和安全状态 |
| scripts/delivery、tests/delivery | 合成双环境真实请求与故障证据 |

## Task 1 (B1)：交付持久化、权限及连接规则

**Files:** Create `src/WebApi.Contracts/Releases/DeliveryContracts.cs`、`src/WebApi.Infrastructure/Delivery/ProjectDeliveryPolicyService.cs`；Create Persistence/Entities下 `ProjectDeliveryPolicy.cs`、`ReleaseArtifact.cs`、`ReleaseVerification.cs`、`ReleaseTestAcceptance.cs`、`ReleasePromotion.cs`、`ReleasePromotionMapping.cs`、`ReleasePromotionEvent.cs`、`VerificationReport.cs` 及对应Configurations；Create Migrations `20261009020000_ReleaseDelivery.cs` 与Designer；Modify ModelSnapshot、`Entities/ReleaseRecord.cs`、`Governance/PermissionCatalog.cs`；Test `tests/WebApi.Integration.Tests/DeliveryPersistenceTests.cs`。

**Interfaces:** `SaveDeliveryPolicyRequest(Guid SourceEnvironmentId,Guid TargetEnvironmentId,string Mode,IReadOnlyList<string> RequiredTestTypes,int VerificationValidityMinutes)`；`DeliveryPolicyDto`补Id/ProjectId/Revision；`SaveAsync(Guid projectId,SaveDeliveryPolicyRequest request,string? etag,ActorContext actor,CancellationToken ct) -> Task<CommandResult<DeliveryPolicyDto>>`；`GetAsync(Guid projectId,ActorContext actor,CancellationToken ct) -> Task<DeliveryPolicyDto>`。新增权限`release.test.record/accept`、`release.verify`只默认授予PlatformAdmin；其他原角色默认定义显式排除三项，不删除用户已显式授予的权限。

- [x] **Step 1：写断言。** 迁移旧项目Get显示Legacy、不改变旧Release；跨项目/停用/错误生产标识422；生产入口未配/有进行中旧Release不能启用；有效期0/10081拒绝；重复Seed不扩给OrganizationAdmin/ProjectAdmin、已有显式授权保留；正式Promotion唯一目标Release，恢复行用RecoveryOf关联。
- [x] **Step 2：RED。** `./scripts/check-contracts.sh integration --filter DeliveryPersistenceTests`。
- [x] **Step 3：实现。** 表约束/索引/全Scope外键核验及授权后分页；连接PUT沿用审计+幂等+ETag。Legacy的缺省策略通过Get稳定投影，不悄悄启用；新项目也默认Legacy。报告元数据表见B3，文件存储由B3管理。
- [x] **Step 4：GREEN。** 同上及GovernanceTests/ApprovalTests；升级旧库、重复初始化、ModelSnapshot一致性通过。
- [x] **Step 5：隔离提交。** `feat(delivery): persist scoped promotion governance`。

## Task 2 (B2)：不可变制品与策略环境参数拆分

**Files:** Create `src/WebApi.Domain/Delivery/ArtifactPolicyTemplate.cs`、`ReleaseArtifactCanonicalizer.cs`、`src/WebApi.Infrastructure/Delivery/ReleaseArtifactService.cs`、`tests/WebApi.Domain.Tests/ReleaseArtifactTests.cs`、`tests/WebApi.Integration.Tests/ReleaseArtifactServiceTests.cs`；Modify DeliveryContracts。

**Interfaces:** `ArtifactRoute(string Key,Guid ApiId,Guid VersionId,string Path,IReadOnlyList<string> Methods,int Priority,bool Enabled,int? TimeoutTemplate,IReadOnlyList<ArtifactPolicyTemplate> Policies)`；Key来自规范化的ApiId+VersionId+Path+排序Methods，重复键拒绝。`ArtifactApiContract(Guid ApiId,Guid VersionId,string Version,long SourceRevision,IReadOnlyList<ParameterDto> Parameters,IReadOnlyList<SchemaDto> Schemas)`；`ArtifactContent`含这些契约和ArtifactRoute，不直接复制FrozenApiVersion/VersionDto，尤其不复制可能含servers/地址的OpenapiSource/OpenapiDocument。`CreateAsync(Guid sourceReleaseId,ActorContext actor,CancellationToken ct) -> Task<ReleaseArtifactDto>`；`Split(string type,string normalizedConfig) -> ArtifactPolicyTemplate`；`Resolve(ArtifactPolicyTemplate template,JsonElement targetParameters) -> string`返回规范化目标策略，未知字段拒绝。

类型允许环境参数：timeout=`TimeoutMs`；rate_limit=`RefillTokens/WindowMs/Burst`；authentication/JWT=`Issuer/Audiences/Jwks/ApplicationMappings`，所有取自目标已保存策略，不读网络JWK；Route级Timeout从目标映射获取。其它字段冻结，circuit_breaker/retry/cache全部冻结。JWT算法/tokenTypes/clockSkew/maxLifetime/applicationClaim/forwardBearer冻结，模板无公钥或ClaimValue→ApplicationId实际映射值。Anonymous/ApiKey模式无环境参数。模板仅表达存在的策略槽位、类型和顺序，不携带来源Policy ID。

- [x] **Step 1：写断言。** 输入集合重排Hash相同；Schema、Method、重试/缓存语义变化Hash不同；TEST地址/AccessKey/SecretHash/Jwks/秘密引用不在artifact字节；未知策略/参数422；新目标issuer不改冻结认证模式；制品只从Succeeded且SnapshotHash匹配来源产生；重复生成同Release/hash同ID。
- [x] **Step 2：RED。** 分别运行domain `--filter ReleaseArtifactTests`、integration `--filter ReleaseArtifactServiceTests`。
- [x] **Step 3：实现。** 使用强类型白名单和CanonicalJson，不做全JSON替换。来源冻结正文、所选版本、Snapshot对应关系和可见性都验证；artifact_hash和source_snapshot_hash分别保存，制品只读无更新接口。
- [x] **Step 4：GREEN。** 两命令及PolicyConfigurationTests/AdvancedPolicyConfigurationTests；目标Resolve仍交给原策略Validator验证，不放宽旧规则。
- [x] **Step 5：隔离提交。** `feat(delivery): freeze portable api release artifacts`。

## Task 3 (B3)：人工验证证据与受控报告

**Files:** Create `src/WebApi.Infrastructure/Delivery/VerificationReportStore.cs`、`ReleaseVerificationService.cs`、`src/WebApi.ControlPlane/Releases/VerificationReportEndpoints.cs`、`tests/WebApi.Integration.Tests/ReleaseVerificationTests.cs`、`tests/WebApi.Integration.Tests/VerificationReportTests.cs`；Modify DeliveryContracts、`ControlPlaneApp.cs`、`deploy/compose.runtime.yml`、`scripts/runtime/context.mjs`。

**Interfaces:** `RecordVerificationRequest(string Type,string Result,DateTimeOffset StartedAt,DateTimeOffset FinishedAt,Guid? ReportId,string Comment)`；`RecordSourceAsync(Guid artifactId,RecordVerificationRequest request,ActorContext actor,CancellationToken ct) -> Task<ReleaseVerificationDto>`；`StoreAsync(Stream input,string contentType,ScopeRef scope,ActorContext actor,CancellationToken ct) -> Task<VerificationReportDto>`；`OpenAsync(Guid reportId,ActorContext actor,CancellationToken ct) -> Task<Stream>`。报告POST `/verification-reports`需关联artifactId或promotionId之一，由服务器解析Scope并要求对应release.test.record或release.verify和写Scope；不接受客户端直接指定Scope。GET `/{id}/download`按同一实际资源读取权限重新核验，attachment+nosniff+no-store。

- [x] **Step 1：写断言。** report恰好10*1024*1024允许，多1字节413；MIME伪造/可执行HTML/非法UTF8拒绝；PDF识别头且仅下载；外Scope附件404；来源配置/序列/入口自动解析不信任请求伪造；Failed可登记但不满足门禁；过期时间按冻结连接政策计算。
- [x] **Step 2：RED。** `./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~ReleaseVerificationTests|FullyQualifiedName~VerificationReportTests'`。
- [x] **Step 3：实现。** 每Scope随机文件名、原子落盘+Hash后存元数据，数据库事务不等待网络/扫描；读文件重新授权。限制真实读取字节而非Content-Length；纯文本严格UTF8，PDF只检查格式头，不声明无恶意。卷只挂ControlPlane，新报告纳入备份；孤立失败文件按本次owner回收，不清理其他附件。
- [x] **Step 4：GREEN。** 同命令；检查公开证据不含全文/认证头/秘密，报告下载权限撤销后404；旧库/卷恢复可读。
- [x] **Step 5：隔离提交。** `feat(delivery): record bounded verification evidence`。

## Task 4 (B4)：独立测试验收与撤销

**Files:** Create `src/WebApi.Infrastructure/Delivery/TestAcceptanceService.cs`、`tests/WebApi.Integration.Tests/TestAcceptanceTests.cs`；Modify DeliveryContracts。

**Interfaces:** `RequestAsync(Guid artifactId,IReadOnlyList<Guid> verificationIds,ActorContext actor,CancellationToken ct) -> Task<TestAcceptanceDto>`；`ActAsync(Guid acceptanceId,string action,string comment,string? etag,ActorContext actor,CancellationToken ct) -> Task<TestAcceptanceDto>`，action=accept/reject/revoke。验收固定证据集合摘要；撤销新增事件不更改证据原记录。

- [x] **Step 1：写断言。** 缺必需类型、不同制品/来源、失败或过期证据拒绝；管理员申请人自批403；缺release.test.accept/来源写Scope/资料可见性拒绝；连接规则改变要重新申请；拒绝、撤销留原事实，重复命令不重复事件。
- [x] **Step 2：RED。** `./scripts/check-contracts.sh integration --filter TestAcceptanceTests`。
- [x] **Step 3：实现。** 当前来源成功Release/运行配置/序列和入口需与证据一致；验收、撤销在治理事务持相同来源锁，B6/B7复用检查；不创建旧生产ApprovalTask替代测试验收。
- [x] **Step 4：GREEN。** 同命令和ApprovalEligibilityTests；确认测试验收不会赋予生产审批或发布权限。
- [x] **Step 5：隔离提交。** `feat(delivery): gate artifacts on independent test acceptance`。

## Task 5 (B5)：目标映射、资源准备与显式凭证候选

**Files:** Create `src/WebApi.Infrastructure/Delivery/PromotionMappingService.cs`、`PromotionCandidateBuilder.cs`、`PromotionCredentialSelection.cs`、`tests/WebApi.Integration.Tests/PromotionMappingTests.cs`、`PromotionCredentialTests.cs`；Modify `Releases/ReleaseCandidateBuilder.cs`、DeliveryContracts。

**Interfaces:** `PromotionMappingRequest(IReadOnlyList<PromotionRouteMapping> Routes,IReadOnlyList<PromotionApplicationMapping> Applications)`；route映射含ArtifactRoute.Key、目标Route可空ID、ClusterId、目标策略ID/修订/槽位和TimeoutMs；application映射含目标ApplicationId、CredentialIds、目标AuthorizationIds。`SaveAsync(Guid promotionId,PromotionMappingRequest request,string? etag,ActorContext actor,CancellationToken ct) -> Task<PromotionDto>`；`PrepareAsync(ReleasePromotion promotion,ActorContext actor,CancellationToken ct) -> Task<PreparedPromotionCandidate>` 内含FrozenReleaseCandidate、ResourceRevisions、CandidateHash、MappingRevision、基线和A4访问上下文。

给原Builder增加明确`BuildPromotionAsync`入口，原BuildAsync签名行为保留；新入口接收批准的凭证选择集合。保留基线其他API授权所需应用，若共享应用则选择集合必须覆盖保留业务需要的现有运行凭证；删除凭证必须作为单独受审影响，不能为本晋级隐含删除。

- [x] **Step 1：写断言。** 目标无Route创建草稿、已有Route正确更新；不可写/外项目资源拒绝；制品Path/Method/认证模式无法被映射更改；共享策略不原地修改；TEST凭证不自动复制；共享应用遗漏基线凭证409，明确共享影响需审批确认；未选API路由/授权保持。
- [x] **Step 2：RED。** integration筛选PromotionMappingTests/PromotionCredentialTests。
- [x] **Step 3：实现。** 仅Draft可编辑。准备事务创建目标独立策略副本/绑定与Route，不覆盖共享策略，使用B2 Resolve和旧地址/策略验证；不得调用带独立事务的普通服务导致嵌套事务，抽取只在持锁事务运行的内部写入原语并保留原外部接口。共享凭证影响写入Candidate评审摘要。
- [x] **Step 4：GREEN。** 两测试及SnapshotCompilerTests/PolicyReleaseTests/RoutePolicyBindingTests；目标准备不创建Outbox、DesiredConfigVersion不变。
- [x] **Step 5：隔离提交。** `feat(delivery): prepare target scoped promotion candidates`。

## Task 6 (B6)：预检、冻结提交与生产直发门禁

**Files:** Create `src/WebApi.Infrastructure/Delivery/PromotionPrecheckService.cs`、`ReleasePromotionService.cs`、`DeliveryReleaseGuard.cs`、`DeliveryLockCoordinator.cs`、`src/WebApi.ControlPlane/Releases/DeliveryEndpoints.cs`、`tests/WebApi.Integration.Tests/PromotionSubmissionTests.cs`、`PromotionConcurrencyTests.cs`；Modify `Releases/ReleaseService.cs`、`ControlPlaneApp.cs`、`WorkerApp.cs`、DeliveryContracts。

**Interfaces:** `PrecheckAsync(Guid promotionId,ActorContext actor,CancellationToken ct) -> Task<PromotionPrecheckDto>`（各检查含Passed/Failed/Unknown及阻断原因）；`CreateAsync(Guid artifactId,ActorContext actor,CancellationToken ct) -> Task<PromotionDto>`；`SubmitAsync(Guid promotionId,string? etag,ActorContext actor,CancellationToken ct) -> Task<PromotionDto>`；`RequireReadyAsync(ReleaseRecord release,ActorContext actor,CancellationToken ct) -> Task`；`LockAsync(Guid sourceEnvironmentId,Guid targetEnvironmentId,CancellationToken ct) -> Task` 按Guid排序，包含统一治理锁顺序约定。

- [x] **Step 1：写断言。** 未测试/撤销/过期/来源已换版本阻断；目标基线/映射/地址/连接修订改变需要新申请；并发Submit只一个目标Release+一次ApprovalTasks；合法重复键返回原ID、不同正文冲突；Legacy旧直发有效，PromotionRequired新直发/伪造promotionId拒绝；回滚仍按原批准路径。
- [x] **Step 2：RED。** integration筛选PromotionSubmissionTests/PromotionConcurrencyTests。
- [x] **Step 3：实现。** precheck生成持久结果和修订摘要但不写运行配置，submit锁内复算与B5准备并冻结，创建正式目标Release和既有两级席位；审批投影沿用原流程。新直发门禁在创建/提交/执行分别检查，不只禁前端按钮。预检契约Unknown需关联人工评审，不转为零风险；上游健康用B3显式人工证据。
- [x] **Step 4：GREEN。** 两测试及ApprovalTests/ComparisonReleaseTests/RollbackTests；故意并发撤销验收、准备资源和Submit验证事务一致性及无死锁。
- [x] **Step 5：隔离提交。** `feat(delivery): freeze promotion approval and enforce production gates`。

## Task 7 (B7)：发布执行、后台复检与恢复追溯

**Files:** Create `src/WebApi.Infrastructure/Delivery/PromotionExecutionService.cs`、`PromotionReadService.cs`、`tests/WebApi.Integration.Tests/PromotionExecutionTests.cs`；Modify Infrastructure下 `Releases/PublishCoordinator.cs`、`Releases/ReleaseRecoveryService.cs`、`Releases/RollbackService.cs`、`Releases/ReleaseService.cs`、`Gateway/AckService.cs`、`Gateway/ReleaseTimeoutService.cs`、`src/WebApi.Worker/Workers/ReleaseBuildWorker.cs` 及两宿主DI。

**Interfaces:** `RequireExecutionAsync(ReleaseRecord release,ActorContext actor,CancellationToken ct) -> Task`；`RecordDeploymentStateAsync(ReleaseRecord release,CancellationToken ct) -> Task`；`ResolvePromotionAsync(Guid releaseId,CancellationToken ct) -> Task<ReleasePromotion?>`，正式Release由PromotionId，恢复/回滚由RecoveryOf/RollbackOf链解析，检测循环和跨环境拒绝；`ReadAsync(Guid id,ActorContext actor,CancellationToken ct) -> Task<PromotionDto>` 投影实时审批阶段而非前端写状态。

- [x] **Step 1：写断言。** Ready后资源/入口/连接/身份变化不能启动；启动后Worker执行前撤销/过期同样阻断；部分ACK/超时=DeploymentFailed且实际已应用节点可读；恢复新Release不冲突唯一PromotionId，保留原候选/制品；新序列拒绝晚到旧ACK；全ACK时Promotion=Verifying、Release=Succeeded，非Completed。
- [x] **Step 2：RED。** `./scripts/check-contracts.sh integration --filter PromotionExecutionTests`。
- [x] **Step 3：实现。** 原事务和Worker路径接入B6 Guard及统一锁序；凭原发布身份重鉴权。新失败重试仅复用已存在目标Snapshot，不可恢复成任意新内容；其PromotionId留NULL由RecoveryOf追溯，原正式目标关联不改。ACK、timeout、构建失败在同事务追加Promotion事件，重启可从持久Release状态恢复投影，不依赖进程内回调。
- [x] **Step 4：GREEN。** 本测试、原RollbackTests/网关ACK相关integration、gateway全回归；故障fixture验证Worker/ControlPlane重启状态恢复。
- [x] **Step 5：隔离提交。** `feat(delivery): coordinate promotion deployment and recovery`。

## Task 8 (B8)：生产验证与人工回滚完成

**Files:** Create `src/WebApi.Infrastructure/Delivery/ProductionVerificationService.cs`、`src/WebApi.Domain/Delivery/PromotionCompletionRules.cs`、`tests/WebApi.Integration.Tests/ProductionVerificationTests.cs`；Modify DeliveryContracts、DeliveryEndpoints、B7执行服务。

**Interfaces:** `RecordAsync(Guid promotionId,RecordVerificationRequest request,ActorContext actor,CancellationToken ct) -> Task<PromotionDto>` 从服务端解析目标实际版本与入口；`TryCompleteAsync(Guid promotionId,CancellationToken ct) -> Task<bool>` 在已持治理/目标环境锁的事务检查三类独立Passed、有效期、全节点实际序列和冻结入口。Failed证据进入VerificationFailed，后续重新验证追加事实；只有当前全部必需类型有效且各最新结果Passed才可完成。

- [ ] **Step 1：写断言。** 申请人/发布人即管理员也不可验证；缺权限403、缺项/过期不Completed；旧Snapshot/部署序列/入口证据拒绝；Failed不触发回滚Outbox；已有审批回滚全ACK后RolledBack且目标地址不还原；Completed历史不会因新部署改写为失败。
- [ ] **Step 2：RED。** `./scripts/check-contracts.sh integration --filter ProductionVerificationTests`。
- [ ] **Step 3：实现。** A4执行入口为首次验证绑定；若仅入口已改、实际目标版本/序列仍在，追加明确重新验证上下文并收齐三类证据，不复用旧Passed；若实际版本/序列已换为其他部署，本次晋级不能凭新版本完成，保留历史并显示已被替代，需要新晋级或其合法恢复执行重新验证。成功回滚取B7可追溯链更新状态。
- [ ] **Step 4：GREEN。** 本测试及B7/RollbackTests；并发最后一条验证和回滚不能同时完成旧交付。
- [ ] **Step 5：隔离提交。** `feat(delivery): verify production outcomes separately from node ack`。

## Task 9 (B9)：制品、验收与交付连接页面

**Files:** Create `console/src/delivery/api.ts`、`delivery-state.mjs`、`delivery-state.d.mts`、`ArtifactDetail.tsx`、`VerificationForm.tsx`、`TestAcceptancePanel.tsx`、`DeliveryPolicyEditor.tsx`、`console/src/pages/ReleaseArtifacts.tsx`、`console/tests/delivery-artifact.test.mjs`；Modify `console/src/main.tsx`、`pages/ReleaseDetail.tsx`、`sidebar-model.mjs`、`sidebar-model.d.mts`、`console/src/styles/tokens.css`。

**Interfaces:** `deliveryReducer(state,event) -> state` 同时绑定actorAuthority、真实Source/Target、epoch；新路由`/delivery/artifacts`、`/delivery/artifacts/{id}`、`/delivery/policy`。表单消费真实资格与B3/B4接口，上传/登记、申请/验收分开动作，服务器实际ID作为关联。

- [ ] **Step 1：写断言。** 人工报告明确标识；缺权限/申请人验收按钮不可用但服务端也拒绝；规则Legacy显示未启用门禁；旧响应不覆盖其他制品，撤权清空报告与资料；真实证据默认三类与有效期显示。
- [ ] **Step 2：RED。** `"$WEBAPI_NODE" --test console/tests/delivery-artifact.test.mjs`。
- [ ] **Step 3：实现。** 新增制品列表/详情、报告下载与验收记录、连接规则编辑；原发布详情保留字段和动作，只增生成制品与追溯入口。侧栏更名而旧路由保留，无任意Stage编辑器假入口。
- [ ] **Step 4：GREEN。** 新测试与check-console；实际浏览器1440/1280完成报告上传和独立验收，键盘焦点/失权/冲突检查。
- [ ] **Step 5：隔离提交。** `feat(console): manage verified artifacts and delivery policy`。

## Task 10 (B10)：晋级向导、审批差异和交付总览

**Files:** Create `console/src/delivery/PromotionWizard.tsx`、`PromotionDiff.tsx`、`PromotionExecution.tsx`、`console/src/pages/ReleasePromotions.tsx`、`DeliveryOverview.tsx`、`console/tests/promotion-flow.test.mjs`、`tests/WebApi.Integration.Tests/DeliveryOverviewTests.cs`；Modify `main.tsx`、`sidebar-model.mjs`、`pages/Approvals.tsx`、`ReleaseDetail.tsx`、`src/WebApi.Infrastructure/Releases/ApprovalInboxService.cs`、`src/WebApi.Contracts/Releases/ApprovalInboxContracts.cs`、B7 ReadService、DeliveryEndpoints。

**Interfaces:** GET `/delivery/overview?projectId=...` 的DTO含授权环境当前实际配置/待验证/失败晋级，分页预算10秒/默认50/最大100，授权后Count；`PromotionWizard({promotionId})` 六步消费B5/B6；`PromotionExecution({promotion})` 显示审批/下发/ACK/业务验证；审批列表新增可空申请类型、来源及制品安全摘要，资料无权时受限。

- [ ] **Step 1：写断言。** TEST入口到PROD映射不能显示上一环境URL；预检失败不能提交；目标无Route准备后可继续；共享凭证影响必须展示确认；ACK全绿仍显示待生产验证；风险受限不显示0；跨环境返回URL不失筛选、409/412保留草稿、撤权清空。
- [ ] **Step 2：RED。** Node promotion-flow测试及 integration `--filter 'FullyQualifiedName~ApprovalInboxTests|FullyQualifiedName~DeliveryOverviewTests'`（新建后者）。
- [ ] **Step 3：实现。** 真实服务器资格、状态及修订控制操作，正文固定对应幂等键；总览不在浏览器逐环境拼接。执行页提供现有回滚申请入口，区别DeploymentFailed/VerificationFailed与部分节点事实，历史来源完整可追溯。
- [ ] **Step 4：GREEN。** Node、integration、check-console；真实浏览器两独立审批者、一个独立验证者走完流程，51条分页/未授权Scope/1440/1280/键盘及原导航回归。
- [ ] **Step 5：隔离提交。** `feat(console): guide environment promotion through production verification`。

## Task 11 (B11)：双环境真实验收、故障与安装交付

**Files:** Create `scripts/check-promotion.sh`、`scripts/delivery/promotion-scenario.mjs`、`promotion-faults.mjs`、`promotion-evidence.mjs`、`tests/delivery/promotion-evidence.test.mjs`、`docs/deployment/release-promotion-runbook.md`；Modify `scripts/runtime/acceptance-backup.mjs`、`scripts/runtime/context.mjs`、`tests/runtime/backup.test.mjs`、`tests/runtime/integration/backup.test.mjs`、`docs/console-coverage.md`；证据 `docs/evidence/release-promotion/<固定SHA>/`。

**Interfaces:** shell `domain|integration|gateway|console|e2e|faults|browser|verify`；`runPromotionScenario({sourceRevision,directory,ports}) -> Promise<PromotionProof>`；`runPromotionFaults(context) -> Promise<PromotionFaultProof>`；`validatePromotionProof(proof,files) -> {passed:boolean,errors:string[]}`。proof包括两环境真实节点Scope、不同上游/入口、源与目标SnapshotHash、artifact_hash、测试验收/两级审批/执行人/独立验证人、实际响应、回滚序列及附件备份恢复事实。

- [ ] **Step 1：写证据断言。** 缺TEST真实请求/PROD实际响应、只ACK无业务验证、相同人员自验、错SHA/镜像、未测试恢复附件、含秘密的proof全部false。
- [ ] **Step 2：RED。** Node promotion-evidence及runtime备份恢复测试。
- [ ] **Step 3：实现。** 自有UUID项目随机本机端口，TEST和PROD各独立Gateway组/后端；新账号仅fixture Scope，两个生产审核者及独立业务验证者。报告卷纳入冷备与隔离恢复工具；不删共享卷或读企业服务。故障含部分ACK、超时、Worker重启、排队撤销、连接修订、共享应用、验证失败和人工审批回滚。
- [ ] **Step 4：GREEN。** 三类后端全回归、Console、e2e/faults/browser/verify和runtime备份恢复各一次，通过/零skip；封存固定SHA/镜像/包逐文件摘要及1440/1280截图。先完成独立整分支审查与问题修复，再更新Coverage。
- [ ] **Step 5：隔离提交。** `test(delivery): seal cross environment promotion acceptance`。

## 原本机安装及完成条件

- [ ] 确认A已安装事实或将A+B候选作为一个明确固定包升级；代码验收和原实例安装身份分别记录。
- [ ] 原4192只读核对当前镜像/数据/卷/账号/SSO/通知/监控。冷备数据库、受控报告、既有附件及私有卷，隔离恢复并读取验证；不公开密钥。
- [ ] 新迁移不能直接以旧库覆盖恢复；保留新增交付事实。候选出故障时优先修复前进，兼容旧软件需独立验证，不能宣称无条件降级。
- [ ] 在已批准安装范围内执行维护升级；默认保留原项目Legacy，不擅自启用真实业务PromotionRequired。演示连接用独立合成项目验证。
- [ ] 复验原业务、账户/SSO、双网关、监控/通知及报告恢复，记录原4192页面和实际API响应；证明本机安装成功后才标`localInstalled=true`。`productionAcceptance=false`直到实际企业环境取得证据。

## 规格覆盖自查与执行选择

§1/2目标导航→B1/B9/B10；§3复用→B5/B7；§4制品/测试/映射→B2/B3/B4/B5/B6；§5数据→B1；§6状态/并发→B6/B7/B8；§7接口/权限/报告→B1/B3/B6/B10；§8UI→B9/B10；§9验收升级→B11/安装门禁；§10延期→全局约束。五项Review Focus均落入行为测试；A规格接口签名与A计划一致。

两种执行方式：Native由本聊天逐任务执行并在末尾独立整分支审查；Subagent-driven每任务由实施与评审子代理执行。推荐Native，原因是映射、冻结候选、生产门禁和恢复在同一发布事务链上，连续维护接口上下文更合适。计划审阅通过并选择方式后才进入执行。
