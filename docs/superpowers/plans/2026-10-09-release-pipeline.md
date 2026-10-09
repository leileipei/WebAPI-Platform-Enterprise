# 标准阶段发布流水线 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现已批准的线性、多环境 API 交付流水线，以独立版本、逐阶段证据和真实发布事实推进，完成隔离验证及原本机实例安装。

**Architecture:** 新增 Pipeline 定义、版本、运行、阶段、尝试及事件模型；通过服务器解析的 DeliveryGateContext 复用既有 Artifact、Promotion、Release、审批、Snapshot 和 ACK。协调器只投影持久事实并开放操作，合资格人员显式准备、发布及验证；历史精确快照恢复沿用原机制。

**Tech Stack:** 现有 .NET 10、ASP.NET Core、EF Core/Npgsql、PostgreSQL、Redis、YARP、React/TypeScript、Node 测试和 Playwright；不新增服务、依赖或文件卷。

**Spec:** [已批准规格](../specs/2026-10-09-release-pipeline-design.md)。本计划等待审阅，执行方式沿用用户已选 **Native**。

## Global Constraints

- 继续使用隔离工作区 `enterprise/.worktrees/api-delivery-promotion` / `feature/api-delivery-promotion`；原非洁净源码目录不改动、不提交、不推送。起点 `e42b5d0`，原4192已安装应用源码仍为 `a445534f82f11ae9a45c3b2904bd07108bd165a9`。
- 名称1–100字符；同组织项目2–8个不同启用环境，首个非生产、恰好一个生产且位于末尾。按实际环境ID和IsProduction判断。
- 非生产必需测试1–3种，默认 InterfaceFunction、Integration、ContractCompatibility；生产固定 EntryConnectivity、AuthenticationAuthorization、CriticalBusinessCall 三种。
- 证据有效期及阶段等待时限各1–10080分钟，默认1440；阶段截止时间UTC。既有ACK超时默认120秒，独立保留。
- 非生产部署审批 None 或既有两级模板；生产必须当前合法两级模板，每级1–5独立席位。所有审批/验收人员还受当前范围及Run申请人约束。
- 每项目一个显式生效版本及至多一个非终态Run；Paused、TimedOut仍占用。不得绕过服务端关联、前置阶段、审批或业务验证。
- ETag、Idempotency-Key、审计、no-store及真实Scope沿用；锁序：治理→项目规则/Run→UUID排序的环境→Stage/尝试。事务不访问外部网络或扫描报告。
- 新增pipeline.read/manage/run只默认授予PlatformAdmin；不扩大其他默认角色，保留已有显式授权。权限不足不能伪装成计数0。
- 不自动复制来源凭证、域名，不破坏共享应用旧业务；保持纯JWT零APIKey规则。附件仍归真实Artifact或Promotion，下载逐次鉴权。
- 不实现自由脚本、DAG、并行环境、CI/CD、定时启动、自动测试、外部报告抓取、紧急免审、灰度或自动回滚。DNS/TLS/LB仍需另行配置。
- RED必须为运行到断言的行为失败；编译失败、容器故障、空过滤或Skipped不算RED。GREEN要求实际命中测试、失败0、跳过0；保留命令、数量和证据。
- 每任务只暂存该任务明确文件后提交。中间提交不安装；P13固定候选后才维护原实例。前期回归数量不能作为本期证据。

## Review Focus

1. 相同部署重新办理后，旧表单摘要仍须409，新证据结束时间不得早于新窗口；P9 `ReopenSameDeploymentRejectsOldContextAndEarlyEvidence`。
2. 更早阶段验收被撤销时，排队候选停止、已下发事实保留，不能被投影器推进；P8 `RevokedEarlierAcceptanceStopsQueuedCandidateButPreservesEmittedFacts`。
3. 委托办理不改变Run申请人独立性：Run创建人不得借其他申请人身份自批、自验；P7 `DelegatedRequesterCannotHideRunCreatorForApprovalOrVerification`。
4. 已取消Run仍有Publishing时不能切模式或开始新Run，暂停/超时同样占用；P4 `CancelledRunWithPublishingStillBlocksActivationAndNewRun`。
5. 阶段委托人员可以合法办理相邻阶段，但无权更早资料不得泄露，受限总数不得显示0；P10 `StageDelegateCanActWithoutEarlierChainDisclosure`，P12对应浏览器用例。

---

## 文件职责与共享接口

新增领域目录 `src/WebApi.Domain/Delivery/Pipelines/`：`PipelineDefinitionRules.cs` 管参数及链，`PipelineStateRules.cs` 管纯状态判断。新增基础设施目录 `src/WebApi.Infrastructure/Delivery/Pipelines/`：`PipelineDefinitionService.cs` 管草稿版本、`PipelineActivationService.cs` 管模式切换、`PipelineRunService.cs` 管开始、`PipelineStageService.cs` 管阶段命令、`PipelineProjectionService.cs` 管持久投影、`PipelineRecoveryService.cs` 管人工处置、`PipelineReadService.cs` 管安全投影。每个服务使用现有授权和命令执行器，不做另一个发布引擎。

共用上下文放在 `src/WebApi.Infrastructure/Delivery/DeliveryGateContext.cs` / `DeliveryGateContextResolver.cs`；锁序统一修改 `DeliveryLockCoordinator.cs`。新增 `src/WebApi.Contracts/Releases/PipelineContracts.cs` 与 `src/WebApi.ControlPlane/Releases/PipelineEndpoints.cs`。注册在实际宿主 `src/WebApi.ControlPlane/ControlPlaneApp.cs`、`src/WebApi.Worker/WorkerApp.cs`。

六个实体及Configuration各用同名文件：`ReleasePipeline`、`ReleasePipelineVersion`、`ReleasePipelineRun`、`ReleasePipelineRunStage`、`ReleasePipelineStageAttempt`、`ReleasePipelineEvent`，分别位于现有 `Persistence/Entities/`、`Persistence/Configurations/`。修改现有 `ProjectDeliveryPolicy`、`ReleasePromotion`、`ReleaseVerification`、`ReleaseTestAcceptance`及对应Configuration；迁移名 `20261009030000_ReleasePipelines`（含Designer与现有ModelSnapshot），按锁定SDK生成，不手写快照。

Console新增 `console/src/delivery/pipeline-state.mjs`及`.d.mts`、`PipelineDefinitionEditor.tsx`、`PipelineStageActions.tsx`；页面分为 `pages/ReleasePipelines.tsx`、`ReleasePipelineDetail.tsx`、`ReleasePipelineRuns.tsx`、`ReleasePipelineRunDetail.tsx`、`ReleasePipelineStageDetail.tsx`。沿用现有组件和导航，不在 `main.tsx` 内堆业务实现。

以下名称是任务间契约；新增record遵循现有JSON命名和ETag回执模式，不引入客户端可写关联ID：

```csharp
// PipelineContracts.cs；ApprovalRule复用ReleaseContracts.cs中的StepOrder/RoleCode/RequiredCount。
record PipelineStageDefinition(int Order, Guid EnvironmentId, IReadOnlyList<string> RequiredTestTypes,
    int EvidenceValidityMinutes = 1440, int WaitTimeoutMinutes = 1440, Guid? ApprovalFlowId = null);
record PipelineDefinition(string Name, string Description, IReadOnlyList<PipelineStageDefinition> Stages);
record PipelineApprovalProfile(Guid FlowId, long FlowRevision, IReadOnlyList<ApprovalRule> Rules);
record PipelineStageProfile(int Order, Guid EnvironmentId, bool IsProduction,
    IReadOnlyList<string> RequiredTypes, int EvidenceValidityMinutes, int WaitTimeoutMinutes,
    PipelineApprovalProfile? Approval);
record PipelineVersionContent(PipelineDefinition Definition, IReadOnlyList<PipelineStageProfile> Profiles);
record CreatePipelineRunRequest(Guid PipelineVersionId, Guid RootArtifactId);
record ActivatePipelineRequest(Guid PipelineVersionId);
record RestoreDeliveryPolicyRequest(SaveDeliveryPolicyRequest Connection);
record PipelineStageVerificationRequest(string ExpectedContextHash, RecordVerificationRequest Evidence);
record PipelineAcceptanceRequest(string ExpectedContextHash, IReadOnlyList<Guid> VerificationIds);
record PipelineActionRequest(string Comment = "");
```

读取DTO由P2/P4/P10定义：`PipelineDto`含Id/ProjectId/Revision/Status/DefinitionVisibility/可空Draft；`PipelineVersionDto`含Id/PipelineId/VersionNo/DefinitionHash/可空Content；`PipelineRunDto`含Id/PipelineVersionId/DefinitionHash/Status/可空CurrentStageOrder/Coverage/可空RootArtifactId、RootArtifactHash/Revision/授权Stages；`PipelineStageDto`含Id/RunId/Order/EnvironmentId/SourceStageId/Status/Revision/可空CurrentAttemptId/ActivatedAt/DeadlineAt（DateTimeOffset?）/StageArtifactId/PromotionId/ActualReleaseId/Eligibility。无权字段为null或省略，不返回虚假完整对象；`PipelineVerificationContextDto`含ContextHash、AttemptId、ProfileHash、实际Release/配置/序列/入口及时间下限。

内部上下文分清来源证据与目标验证：`DeliveryEvidenceProfile(RequiredTypes, ValidityMinutes, ProfileHash)`；`DeliveryGateContext`含Origin、ProjectId、SourceEnvironmentId、TargetEnvironmentId、PolicyRevision、Mode、SourceEvidence、TargetEvidence及可空`PipelineGateBinding`（context属性名Pipeline）。Binding含RunId/StageId/FormalAttemptId/CurrentAttemptId/DefinitionHash/RootArtifactHash/RunCreatedBy/冻结Approval；所有值从数据库解析。解析Promotion时SourceEvidence来自直接前置Stage，TargetEvidence来自本Stage，禁止共用一份有效期。

测试支持新增 `tests/WebApi.TestSupport/Support/PipelineScenario.cs`：沿用 `ApiFixture`、`DeliveryScenario`及真实测试数据库；`InitializeAsync(int environmentCount=2, bool activate=true, bool nonProductionApproval=false)`建立同项目环境/独立角色/资源和成功来源，发布版本但按参数决定激活。提供 `Api`、`Definition`、`PipelineId`、`VersionId`、`RootArtifactId`、`RunId`（Start或Seed后赋值）、`SourceDeploymentCompletedAt`、`Environments`（有序ID）、`Creator`/独立角色客户端；`StartAsync()`返回Run，`CurrentStageAsync(runId)`、`PassSourceAsync(runId)`、`PrepareCurrentAsync(runId)`、`DeployCurrentAsync(runId)`、`PassCurrentAsync(runId)`返回真实API结果，逐任务加入实现。后四者只在测试宿主发送实际命令并模拟节点ACK，不是本机Gateway验收证据；故障种子使用显式`Seed*`命名，不能包装成真实闭环。

验证命令工作目录均为隔离工作区。`./scripts/check-contracts.sh domain|integration|gateway --filter 'FullyQualifiedName~类名'`使用现有锁定SDK/独立资源；Console用`./scripts/check-console.sh`，设置现有WEBAPI_NODE/WEBAPI_PNPM路径。每任务测试类以本任务名称分组，选择器须报告实际命中数量。P1–P10的Step4先原样重跑该任务Step2命令至GREEN，再用相同脚本和列出的实际回归类名过滤；多类过滤完整格式为`--filter 'FullyQualifiedName~ClassA|FullyQualifiedName~ClassB'`。测试片段省略using和Fixture生命周期，断言值为要求，不能改成只检查“不抛异常”。片段中的scenario/fixture私有辅助函数与场景种子在拥有测试的任务实现；不得依赖后续任务不存在的HTTP。P1的FixtureDefinition为测试类本地工厂，返回上述Definition/EnvironmentFact，P4以后追加API辅助，P5–P7可用显式持久种子验证单层规则，P8起运行真实测试宿主全链。

## Task 1 (P1): 持久模型、定义规则与权限目录

**Files:** Create 六实体/配置、`PipelineContracts.cs`、`PipelineDefinitionRules.cs`、迁移三件套、测试支持`PipelineScenario.cs`最初的环境/角色构造；Modify 四旧实体/配置、`src/WebApi.Infrastructure/Governance/PermissionCatalog.cs`；Test `tests/WebApi.Domain.Tests/PipelineDefinitionRulesTests.cs`、`tests/WebApi.Integration.Tests/PipelinePersistenceTests.cs`。

**Interfaces:** Produces `PipelineDefinitionRules.Validate(PipelineDefinition definition, IReadOnlyList<PipelineEnvironmentFact> environments)`（void，非法定义抛ArgumentException，基础设施转换为现有ApiException 422）；`PipelineEnvironmentFact(Guid Id, Guid ProjectId, bool Active, bool IsProduction)`；六实体及共享record供P2–P10使用。定义SHA在P2计算，数据库事实不要信任请求中的IsProduction。

- [ ] **Step 1:** 编写参数/链、FK/唯一约束及权限升级测试，包括以下断言与2、8合法边界、重复/跨项目/停用/生产非末尾/源码阶段审批拒绝、1/10080合法、0/10081拒绝：

```csharp
[Fact] void ProductionCannotDropMandatoryChecks() {
    var d = FixtureDefinition.ProductionWithTypes("EntryConnectivity");
    Assert.Throws<ArgumentException>(() => PipelineDefinitionRules.Validate(d.Definition, d.Environments));
}
[Fact] async Task CatalogAddsThreeAdminGrantsAndPreservesExplicitGrants() {
    var before = await fixture.ReadRoleGrantsAsync(); await fixture.SeedCatalogTwiceAsync();
    Assert.Equal(3, await fixture.CountPipelineGrantsAsync("PlatformAdmin"));
    Assert.Equal(0, await fixture.CountNewDefaultPipelineGrantsOutsideAdminAsync());
    Assert.True(await fixture.AllExplicitGrantsStillExistAsync(before));
}
```

- [ ] **Step 2:** 运行`./scripts/check-contracts.sh domain --filter FullyQualifiedName~PipelineDefinitionRulesTests`及`./scripts/check-contracts.sh integration --filter FullyQualifiedName~PipelinePersistenceTests`；先加入可编译类型/空实现，要求实际规则或唯一性断言失败，记录RED；迁移的RED用旧模式数据库断言缺新约束，不能把查不存在表异常当行为失败。测试fixture不依赖尚未实现的Pipeline HTTP端点。
- [ ] **Step 3:** 实现定义规则及迁移。数据库唯一约束含版本号、Run阶段顺序、同Run前置关联、尝试序号、Promotion/Release正式关联、一阶段一个进行中尝试、一项目一个非终态Run；跨组织/项目归属另由服务锁内验证。Recovery用OriginAttemptId，不能修改原Promotion.StageAttemptId。未激活的未来Stage使用内部`Pending`状态，无尝试/截止时间；旧记录上下文字段NULL、GateOrigin默认ProjectConnection。权限目录升级可重复执行，不覆盖旧授权。
- [ ] **Step 4:** 上述两类GREEN，并运行既有`DeliveryPersistenceTests`、`CatalogTests`；验证从旧A+B数据库迁移，旧行字段及关系不变，回退脚本只作隔离验证，原实例不降级。
- [ ] **Step 5:** 暂存本任务Files，提交 `feat(pipeline): persist linear definitions and stage facts`，记录迁移名、命中测试数、0失败0跳过。

## Task 2 (P2): 草稿、不可变版本及显式模式切换

**Files:** Create `Delivery/Pipelines/PipelineDefinitionService.cs`、`PipelineActivationService.cs`、`Releases/PipelineEndpoints.cs`；Modify `Delivery/ProjectDeliveryPolicyService.cs`、`Delivery/DeliveryLockCoordinator.cs`、`Contracts/Releases/DeliveryContracts.cs`、`ControlPlaneApp.cs`；Test `tests/WebApi.Integration.Tests/PipelineDefinitionTests.cs`、`PipelineActivationTests.cs`。基础设施与控制平面路径使用前述src目录。ETag传递及命令回执复用现有模式；PublishVersion与Archive端点要求If-Match。

**Interfaces:** Produces `CreateAsync(Guid projectId, PipelineDefinition request, ActorContext actor, CancellationToken ct)` / `SaveAsync(Guid id, PipelineDefinition request, string? etag, ActorContext actor, CancellationToken ct)`→`Task<PipelineDto>`；`PublishVersionAsync(Guid id, string? etag, ActorContext actor, CancellationToken ct)`→`Task<PipelineVersionDto>`；`ArchiveAsync(Guid id, string? etag, ActorContext actor, CancellationToken ct)`→`Task<PipelineDto>`；Activation `ActivateAsync(Guid projectId, ActivatePipelineRequest request, ActorContext actor, CancellationToken ct)` / `RestoreAsync(Guid projectId, RestoreDeliveryPolicyRequest request, ActorContext actor, CancellationToken ct)`→现有policy回执。P3完善全部共用路径锁序，本任务激活已遵守完整顺序。

- [ ] **Step 1:** 写`PublishDoesNotActivateAndVersionNeverMutates`、`ActivateRequiresProductionEntryFlowAndRealBaseline`、`PausedAndTimedOutRunBlockModeSwitch`、`LegacyPutCannotDisablePipelineMode`；覆盖archive/幂等/ETag/模板修订和切规则并发：

```csharp
[Fact] async Task PublishDoesNotActivateAndVersionNeverMutates() {
    await scenario.InitializeAsync(activate:false);
    Assert.NotEqual("PipelineRequired", (await scenario.ReadPolicyAsync()).Mode);
    var original = await scenario.ReadVersionAsync(); await scenario.EditDraftNameAsync("新版草稿");
    Assert.Equal(original.DefinitionHash, (await scenario.ReadVersionAsync()).DefinitionHash);
}
[Fact] async Task LegacyPutCannotDisablePipelineMode() {
    await scenario.InitializeAsync();
    Assert.Equal(HttpStatusCode.Conflict, (await scenario.PutLegacyPolicyAsync()).StatusCode);
}
```

- [ ] **Step 2:** 运行`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~PipelineDefinitionTests|FullyQualifiedName~PipelineActivationTests'`，新端点404/旧普通PUT绕过等行为断言须RED；不以端点编译失败代替。
- [ ] **Step 3:** 实现上述签名、canonical内容+SHA-256和冻结审批Profile；GET/POST项目列表、GET/PUT定义、POST versions、POST archive（选定`/release-pipelines/{id}/archive`）、activate-pipeline及`/projects/{id}/delivery-policy/restore-connection`。激活核验所有目标写权限、当前模板、生产入口/真实基线、任何非终态Run及未结束发布/旧晋级/恢复；持治理→项目锁重新读，再UUID环境锁。摘要来源/目标为最后非生产→生产。发布和草稿编辑不激活，不自动迁移Legacy项目。
- [ ] **Step 4:** 两个新类GREEN，回归`DeliveryPersistenceTests`、`PromotionSubmissionTests`；并发两次激活只产生一次有效规则修订，错误412/409及幂等异正文符合原规范。
- [ ] **Step 5:** 提交 `feat(pipeline): version definitions and activate project delivery policy`，只包含本任务文件及测试支持扩展。

## Task 3 (P3): 类型化门禁上下文与共用锁序

**Files:** Create `Delivery/DeliveryGateContext.cs`、`DeliveryGateContextResolver.cs`；Modify `DeliveryLockCoordinator.cs`、`ProjectDeliveryPolicyService.cs`、`ReleaseVerificationService.cs`、`TestAcceptanceService.cs`、`ProductionVerificationService.cs`、`PromotionPrecheckService.cs`、`ReleasePromotionService.cs`、`DeliveryReleaseGuard.cs`、`PromotionExecutionService.cs`、`Releases/ApprovalEligibilityService.cs`、`ControlPlaneApp.cs`、`WorkerApp.cs`；Test `PipelineGateContextTests.cs`、`PipelineLockOrderTests.cs`于Integration.Tests。

**Interfaces:** Produces resolver `ResolvePromotionAsync(Guid promotionId, ActorContext actor, CancellationToken ct)` / `ResolveStageAsync(Guid stageId, ActorContext actor, CancellationToken ct)`→`Task<DeliveryGateContext>`；`ResolveConnectionAsync(Guid projectId, ActorContext actor, CancellationToken ct)`只解析原连接；`RequireStageWritableAsync(DeliveryGateContext context, ActorContext actor, CancellationToken ct)`。锁协调器新增`LockProjectAsync(Guid projectId, CancellationToken ct)`及`LockEnvironmentsAsync(IReadOnlyCollection<Guid> environmentIds, CancellationToken ct)`，caller先持治理锁，项目锁后才能进入环境/业务行。Consumes P1实体、P2生效修订。

- [ ] **Step 1:** 用明确`SeedPipelineFactsAsync`持久种子测试两种Origin、伪造FK/项目/阶段、来源有效期30与目标1440不同、ProfileHash和正式/当前恢复尝试分离；增加旧连接完整对照及锁并发测试：

```csharp
[Fact] async Task AdjacentProfilesDoNotCollapseIntoProjectSummary() {
    var seed = await fixture.SeedPipelineFactsAsync(sourceValidity:30, targetValidity:1440);
    var context = await resolver.ResolvePromotionAsync(seed.PromotionId, seed.Actor, default);
    Assert.Equal(seed.PredecessorEnvironmentId, context.SourceEnvironmentId);
    Assert.Equal(30, context.SourceEvidence.ValidityMinutes);
    Assert.Equal(1440, context.TargetEvidence.ValidityMinutes);
    Assert.Equal(seed.CurrentAttemptId, context.Pipeline!.CurrentAttemptId);
    Assert.NotEqual(context.Pipeline.FormalAttemptId, context.Pipeline.CurrentAttemptId);
}
```

- [ ] **Step 2:** 运行`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~PipelineGateContextTests|FullyQualifiedName~PipelineLockOrderTests'`，行为RED须是错误上下文/锁序断言；可编译resolver stub不能冒充实现。
- [ ] **Step 3:** 实现resolver及旧服务适配。直接Stage解析其自身Profile；Promotion解析直接前置SourceProfile与本StageTargetProfile。可空绑定只在旧事实合法，无客户端切Origin。共用路径统一锁序，避免旧验收先锁环境再进入项目；使用caller-owned事务，不通过HTTP或嵌套公开命令调用。P6/P7再补非生产候选及执行约束，本任务只改规则来源，保持旧行为。
- [ ] **Step 4:** 新类GREEN及`PromotionConcurrencyTests`、`PromotionSubmissionTests`、`PromotionExecutionTests`、`ReleaseVerificationTests`、`TestAcceptanceTests`、`ProductionVerificationTests`，确认Legacy/PromotionRequired全部旧断言未改变；多个Worker注册解析器可启动。
- [ ] **Step 5:** 提交 `refactor(delivery): resolve typed pipeline gates with consistent locks`，接口变动和所有消费者同一提交。

## Task 4 (P4): 开始Run、来源事实导入及当前阶段

**Files:** Create `Delivery/Pipelines/PipelineRunService.cs`、`Domain/Delivery/Pipelines/PipelineStateRules.cs`；Modify `PipelineEndpoints.cs`、`ControlPlaneApp.cs`、`PipelineScenario.cs`；Test `tests/WebApi.Integration.Tests/PipelineRunTests.cs`、`tests/WebApi.Domain.Tests/PipelineStateRulesTests.cs`。

**Interfaces:** Produces `StartAsync(CreatePipelineRunRequest request, ActorContext actor, CancellationToken ct)`→`Task<PipelineRunDto>`；`PipelineStateRules.IsTerminal(string status)`及`CanWrite(string runStatus, string stageStatus, DateTimeOffset deadlineAt, DateTimeOffset now)`→bool。Consumes 生效版本、RunningDeploymentReader、既有Artifact核验、P3锁。测试支持新增`StartAsync`/`CurrentStageAsync`，安全完整版读取在P10完善。

- [ ] **Step 1:** 测试当前root制品成功全ACK导入、不重发、2/4环境Pending链、rootHash冻结、前后同Run、同键回执；并发开始及Focus4：

```csharp
[Fact] async Task CancelledRunWithPublishingStillBlocksActivationAndNewRun() {
    await scenario.InitializeAsync();
    await scenario.SeedCancelledRunWithPublishingAsync(); // 显式数据库故障种子
    Assert.Equal(HttpStatusCode.Conflict, (await scenario.TryStartAsync()).StatusCode);
    Assert.Equal(HttpStatusCode.Conflict, (await scenario.TryActivateAsync()).StatusCode);
}
[Fact] async Task StartImportsSourceWithoutAnotherRelease() {
    await scenario.InitializeAsync(environmentCount:4); var before = await scenario.CountReleasesAsync();
    var run = await scenario.StartAsync(); Assert.Equal(before, await scenario.CountReleasesAsync());
    Assert.Equal("AwaitingEvidence", run.Stages[0].Status);
    Assert.All(run.Stages.Skip(1), s => Assert.Equal("Pending", s.Status));
}
```

- [ ] **Step 2:** 运行`./scripts/check-contracts.sh integration --filter FullyQualifiedName~PipelineRunTests`及`./scripts/check-contracts.sh domain --filter FullyQualifiedName~PipelineStateRulesTests`行为RED；过时/未成功/跨环境根制品、缺来源/任意链读取权限分别测试，不能只用管理员成功例。
- [ ] **Step 3:** POST `/release-pipeline-runs`实现Start签名；锁内核验唯一Run、链内所有在途发布/恢复、当前成功实际来源、制品完整/hash/契约可见及启动人权限。一次事务保存版本摘要/规则修订、RootArtifact、来源Release/配置/序列和Stage链；仅来源开启尝试+UTC1440默认窗口。未来Pending不提前启动计时、不生成候选。P2的busy检查共享此在途判断，终态Run也不能掩盖实际Publishing。
- [ ] **Step 4:** 新类GREEN，回归P2激活/切换及`ReleaseArtifactServiceTests`；并发不同幂等键仍至多一个非终态Run，来源发布计数不增加。
- [ ] **Step 5:** 提交 `feat(pipeline): start frozen runs from actual source artifacts`。

## Task 5 (P5): 阶段制品、独立测试证据与验收

**Files:** Create `Delivery/Pipelines/PipelineStageService.cs`；Modify `Delivery/ReleaseArtifactService.cs`、`ReleaseVerificationService.cs`、`TestAcceptanceService.cs`、`PipelineEndpoints.cs`、`PipelineScenario.cs`；Test `tests/WebApi.Integration.Tests/PipelineStageEvidenceTests.cs`。

**Interfaces:** Stage `MaterializeArtifactAsync(Guid stageId, ActorContext actor, CancellationToken ct)`→`Task<ReleaseArtifactDto>`；`RecordVerificationAsync(Guid stageId, PipelineStageVerificationRequest request, ActorContext actor, CancellationToken ct)`→现有verification回执；`RequestAcceptanceAsync(Guid stageId, PipelineAcceptanceRequest request, ActorContext actor, CancellationToken ct)`→现有acceptance回执；`GetVerificationContextAsync(Guid stageId, ActorContext actor, CancellationToken ct)`→`Task<PipelineVerificationContextDto>`。Artifact抽取`CreateWithinTransactionAsync(Guid releaseId, ActorContext actor, CancellationToken ct)`共用内核，caller拥有事务，仍核验原release.create。验收处理/撤销仍走既有ID端点，P8投影后果。

- [ ] **Step 1:** 编写来源/中间阶段不同类型与时限、未绑定制品拒绝、hash不等失效、报告实际归属、不同Profile/过期/早于部署拒绝、独立申请人、旧接口不选择Profile：

```csharp
[Fact] async Task FirstSourceMayRegisterRealTestsBeforeRunStartedButAfterDeployment() {
    await scenario.InitializeAsync(); var run = await scenario.StartAsync();
    var stage = await scenario.CurrentStageAsync(run.Id);
    var response = await scenario.RecordStageAsync(stage.Id, started:scenario.SourceDeploymentCompletedAt, finished:scenario.SourceDeploymentCompletedAt);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(HttpStatusCode.UnprocessableEntity,
        (await scenario.RecordStageAsync(stage.Id, started:scenario.SourceDeploymentCompletedAt.AddTicks(-2), finished:scenario.SourceDeploymentCompletedAt.AddTicks(-1))).StatusCode);
}
[Fact] async Task RunCreatorCannotAcceptDelegatesStageRequest() {
    var acceptance = await scenario.SeedDelegatedAcceptanceAsync();
    Assert.Equal(HttpStatusCode.Forbidden, (await scenario.AcceptAsync(acceptance, scenario.Creator)).StatusCode);
}
```

- [ ] **Step 2:** 运行`./scripts/check-contracts.sh integration --filter FullyQualifiedName~PipelineStageEvidenceTests`，必须在实际请求/授权/时间断言处RED；种子分别说明已部署但非真实Gateway证据。
- [ ] **Step 3:** 实现新Stage上下文/证据/acceptance-requests/materialize-artifact端点，非生产制品生成须全ACK、pipeline.run+release.create及实际可见，当前Artifact hash=rootHash；幂等重试只绑定同一事实。摘要含CurrentAttemptId/实际Release、配置、序列、入口、ProfileHash；缺失422/过旧409；wrapper摘要为唯一输入，若Evidence内原可空ExpectedContextHash另填不同值则422。时间下限第一尝试部署完成、第二次起max(部署完成,ActivatedAt)。非生产验收排除Run创建人/验收请求人/Stage正式发布申请人；现有连接保持原规则。证据/验收存当前尝试，附件Owner不扩展。
- [ ] **Step 4:** 新类GREEN，加`ReleaseVerificationTests`、`TestAcceptanceTests`、`VerificationReportTests`；确认三类测试每个环境独立，人工报告链接不会被访问。
- [ ] **Step 5:** 提交 `feat(pipeline): bind stage artifacts evidence and independent acceptance`。

## Task 6 (P6): 相邻晋级、非生产初始基线及可选审批

**Files:** Modify `PipelineStageService.cs`、`Delivery/ReleasePromotionService.cs`、`PromotionMappingService.cs`、`PromotionCandidateBuilder.cs`、`PromotionPrecheckService.cs`、`PromotionCredentialSelection.cs`、`PromotionOptionsService.cs`、`Releases/ReleaseService.cs`、`PipelineEndpoints.cs`、`PipelineScenario.cs`；Test `tests/WebApi.Integration.Tests/PipelinePromotionTests.cs`、`PipelineNonProductionApprovalTests.cs`。

**Interfaces:** Produces Stage `PreparePromotionAsync(Guid stageId, ActorContext actor, CancellationToken ct)`→`Task<PromotionDto>`；Promotion内部`CreateForStageWithinTransactionAsync(Guid stageId, DeliveryGateContext context, ActorContext actor, CancellationToken ct)`→`Task<ReleasePromotion>`，不对外接受任意关联；`ReleaseService.FreezeApprovalAsync`内部增加可空`PipelineApprovalProfile`参数，旧caller传null保持既有行为。Consumes P3直接前置Profile/P5阶段Artifact，P7补执行时重查。

- [ ] **Step 1:** 测试严格相邻、rootHash等值、一次正式申请、非生产0基线合法/生产0拒绝、非生产无审批/两级模板、共享应用/纯JWT：

```csharp
[Fact] async Task EmptyNonProductionCanInitializeButProductionCannot() {
    await scenario.InitializeAsync(environmentCount:4);
    await scenario.SeedPassedSourceAsync(); var target = await scenario.PrepareCurrentAsync(scenario.RunId);
    Assert.True((await scenario.PrecheckAsync(target.Id)).CanSubmit);
    await scenario.SeedProductionCurrentStageWithoutBaselineAsync();
    Assert.Contains((await scenario.PrecheckCurrentAsync()).Checks, c => c.ReasonCode == "production_baseline_required" && c.Blocking);
}
[Fact] async Task StandaloneCreateCannotSelectNonProductionOrPipelineOrigin() {
    Assert.Equal(HttpStatusCode.Conflict, (await scenario.TryStandalonePromotionAsync()).StatusCode);
}
```

- [ ] **Step 2:** 运行`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~PipelinePromotionTests|FullyQualifiedName~PipelineNonProductionApprovalTests'`行为RED，非生产0基线是真实规则例外而非放宽生产规则；错关联请求不能靠客户端设置GateOrigin通过。
- [ ] **Step 3:** 实现Prepare事务内相邻创建和旧接口门禁。只当前ActiveStage、直接前置Passed/current/有效验收、同根hash；保存唯一Promotion和正式Attempt关联。旧mapping实际URL为`/release-promotions/{id}/mapping`，预检/submit沿用原URL及Revision。非生产无基线允许明确初始化，有基线仍验证；目标凭证/应用显式选择，禁止来源Secret复制。冻结非生产可选2级模板，源Stage不得部署审批；生产必须目标当前模板ID+修订一致，无审批仅免部署审批。冻结后候选编辑须新尝试，不复用批准。
- [ ] **Step 4:** 两新类GREEN，完整`PromotionMappingTests`、`PromotionCredentialTests`、`PromotionSubmissionTests`；测试共享业务保留及JWT零APIKey已批准例外仍成立。
- [ ] **Step 5:** 提交 `feat(pipeline): prepare adjacent environment promotions and stage approvals`。

## Task 7 (P7): 审批、下发与所有旧入口的执行门禁

**Files:** Modify `Delivery/DeliveryReleaseGuard.cs`、`PromotionExecutionService.cs`、`Releases/ApprovalEligibilityService.cs`、`ReleaseService.cs`、`PublishCoordinator.cs`、`Worker/Workers/ReleaseBuildWorker.cs`、`Delivery/ProductionVerificationService.cs`、`Contracts/Releases/DeliveryContracts.cs`；Test `tests/WebApi.Integration.Tests/PipelineExecutionTests.cs`、`PipelineApprovalTests.cs`。只将Worker路径归入src/WebApi.Worker，其余沿前述src目录。

**Interfaces:** Consumes `DeliveryGateContextResolver.ResolvePromotionAsync`和`RequireStageWritableAsync`；Produces guard `RequirePipelineExecutionAsync(Guid releaseId, ActorContext actor, CancellationToken ct)`→`Task<DeliveryGateContext?>`（旧合法记录返回null），由submit、approval、publish、Worker实际构建共用。既有生产verification-context增加可空AttemptId/ProfileHash，只对Pipeline要求完整摘要；普通历史恢复采用专用原快照判定，不调用“新候选来源当前”门禁。

- [ ] **Step 1:** 测试提前Stage/旧HTTP创建提交/普通生产直发/伪造Run、模板变更、队列撤权或来源改变、2级不同人、实际发布人不得验证、Focus3：

```csharp
[Fact] async Task DelegatedRequesterCannotHideRunCreatorForApprovalOrVerification() {
    var p = await scenario.SeedDelegatedProductionCandidateAsync();
    Assert.Equal(HttpStatusCode.Forbidden, (await scenario.ApproveAsync(p, scenario.Creator)).StatusCode);
    await scenario.SeedAllAckProductionAsync(p);
    Assert.Equal(HttpStatusCode.Forbidden, (await scenario.VerifyProductionAsync(p, scenario.Creator)).StatusCode);
}
[Fact] async Task ExactSnapshotRecoverySurvivesLaterSourceReplacement() {
    var failed = await scenario.SeedFailedEmittedReleaseAsync(); await scenario.ReplaceSourceAsync();
    Assert.Equal(HttpStatusCode.OK, (await scenario.RetryExactSnapshotAsync(failed)).StatusCode);
    Assert.Equal(HttpStatusCode.Conflict, (await scenario.TryAdvanceFromReplacedSourceAsync()).StatusCode);
}
```

- [ ] **Step 2:** 运行`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~PipelineExecutionTests|FullyQualifiedName~PipelineApprovalTests'`，行为RED要求能证明绕过/自批或错误恢复阻断；Worker断言检查真实持久构建结果及无新增下发序列，不只HTTP按钮状态。
- [ ] **Step 3:** 实现guard并接入全部既有入口；按真实FK确定Run→Stage→正式申请→当前尝试→唯一Release，当前规则/直接来源验收/当前权限、资源修订及模板重查。生产审批排除Run创建人和正式申请人、跨级独立；生产验证再排除实际发布人，管理员同样适用。非生产可选审批冻结沿P6。PipelineRequired禁止独立晋级与普通生产发布；旧Legacy/PromotionRequired和精确快照恢复/回滚保持原授权批准链，可处理已终态Run的已发快照，不能从无效来源创建新候选或推进。
- [ ] **Step 4:** 新类GREEN，回归`PromotionExecutionTests`、`PromotionConcurrencyTests`、`ProductionVerificationTests`及既有发布/恢复类（使用`rg --files tests/WebApi.Integration.Tests`确定Release相关类，禁止空过滤）；检查恢复配置Hash不变、序列递增、无重复正式Release、旧独立发布的人员规则未收紧。
- [ ] **Step 5:** 提交 `feat(pipeline): enforce stage gates across approval and release execution`。

## Task 8 (P8): 持久状态投影、超时及撤销传播

**Files:** Create `Delivery/Pipelines/PipelineProjectionService.cs`、`src/WebApi.Worker/Workers/PipelineProjectionWorker.cs`；Modify `PipelineStateRules.cs`、`Delivery/TestAcceptanceService.cs`、`PromotionExecutionService.cs`、`Gateway/AckService.cs`、`Gateway/ReleaseTimeoutService.cs`、`WorkerApp.cs`、`ControlPlaneApp.cs`、`PipelineScenario.cs`；Test `tests/WebApi.Integration.Tests/PipelineProjectionTests.cs`、`PipelineTimeoutTests.cs`。

**Interfaces:** Produces `ProjectRunAsync(Guid runId, CancellationToken ct)`→`Task`（事实驱动，不代人命令）；`ScanAsync(int batchSize, CancellationToken ct)`→`Task<int>`（本轮投影数，默认50，SKIP LOCKED或等价持久领取）。使用现有TimeProvider，投影本Run并按锁序追加唯一状态事件。P5/P6/P7事实为输入；测试支持PassSource/DeployCurrent/PassCurrent通过真实API+测试ACK+ProjectRun推进。

- [ ] **Step 1:** 测试非生产ACK后仍等制品/证据/独立验收、生产ACK后仍需3验证、下一Stage激活才计时、两Worker重启/并发幂等；Focus2及独立超时：

```csharp
[Fact] async Task RevokedEarlierAcceptanceStopsQueuedCandidateButPreservesEmittedFacts() {
    var chain = await scenario.SeedEarlierAcceptedWithQueuedAndEmittedFactsAsync();
    await scenario.RevokeAcceptanceAsync(chain.AcceptanceId); await projector.ProjectRunAsync(chain.RunId, default);
    Assert.Equal("Cancelled", await scenario.PromotionStatusAsync(chain.QueuedPromotionId));
    Assert.Equal(chain.EmittedSnapshotHash, await scenario.SnapshotHashAsync(chain.EmittedReleaseId));
    Assert.NotEqual("Completed", (await scenario.ReadRunAsync(chain.RunId)).Status);
}
[Fact] async Task StageTimeoutDoesNotErasePublishingOrExtendAckDeadline() {
    var s = await scenario.SeedPublishingAtStageDeadlineAsync(); await projector.ProjectRunAsync(s.RunId, default);
    Assert.Equal("TimedOut", (await scenario.ReadRunAsync(s.RunId)).Status);
    Assert.Equal(s.OriginalAckDeadline, await scenario.AckDeadlineAsync(s.ReleaseId));
    Assert.Equal(s.AppliedNodeSequence, await scenario.NodeSequenceAsync(s.AppliedNodeId));
}
```

- [ ] **Step 2:** 运行`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~PipelineProjectionTests|FullyQualifiedName~PipelineTimeoutTests'`行为RED；使用可控TimeProvider和明确持久种子，绝不通过长时间sleep掩盖窗口判断。
- [ ] **Step 3:** 实现投影/扫描；Active当前Stage依据实际ACK、Artifact、有效证据、验收资格判定Passed，开下一Pending尝试，最终生产验证才Completed。失败Rejected/DeploymentFailed/VerificationFailed→Paused；超时→TimedOut且阻止新操作，原ACK超时独立运行，迟到ACK保存。显式任一验收撤销在同事务阻止推进/取消尚未下发候选，保留已发节点及事件；不能等扫描间隔期间绕过P7门禁。普通更早环境变更/自然过期不改写Passed；Completed收到撤销只追加事件不改历史完成。多进程同事实只追加一次迁移事件，不持锁做网络调用。
- [ ] **Step 4:** 两新类GREEN，跑P5/P7及`AckTests`及既有发布超时/领域制品状态回归（检查实际类名及命中数量）；测试ControlPlane/Worker新进程从数据库恢复、Paused/TimedOut不激活后续Stage。
- [ ] **Step 5:** 提交 `feat(pipeline): project durable stage facts and independent timeouts`。

## Task 9 (P9): 暂停、重新办理、取消与恢复追溯

**Files:** Create `Delivery/Pipelines/PipelineRecoveryService.cs`；Modify `PipelineStageService.cs`、`PipelineProjectionService.cs`、`DeliveryGateContextResolver.cs`、`PipelineEndpoints.cs`、`Releases/ReleaseService.cs`、`Delivery/ProductionVerificationService.cs`、`TestAcceptanceService.cs`；Test `tests/WebApi.Integration.Tests/PipelineRecoveryTests.cs`。

**Interfaces:** Produces `PauseAsync(Guid runId, PipelineActionRequest request, string? etag, ActorContext actor, CancellationToken ct)` / `ResumeAsync(...)` / `CancelAsync(...)`→`Task<PipelineRunDto>`；`ReopenAsync(Guid stageId, PipelineActionRequest request, string? etag, ActorContext actor, CancellationToken ct)`→`Task<PipelineStageDto>`。每个签名参数相同，Resume/Cancel仅替换方法名；Run级要求全链可见及项目read_write，阶段Reopen只要求真实当前阶段/相邻授权。

- [ ] **Step 1:** 覆盖未发新候选/已发失败原快照retry/已ACK新验证窗口三支、Publishing拒绝、取消不rollback、终态不可恢复；Focus1：

```csharp
[Fact] async Task ReopenSameDeploymentRejectsOldContextAndEarlyEvidence() {
    var s = await scenario.SeedTimedOutSucceededStageAsync(); var old = await scenario.ContextAsync(s.StageId);
    var reopened = await scenario.ReopenAsync(s.StageId); var current = await scenario.ContextAsync(s.StageId);
    Assert.NotEqual(old.AttemptId, current.AttemptId); Assert.NotEqual(old.ContextHash, current.ContextHash);
    Assert.Equal(HttpStatusCode.Conflict, (await scenario.RecordWithContextAsync(s.StageId, old.ContextHash)).StatusCode);
    Assert.Equal(HttpStatusCode.UnprocessableEntity,
        (await scenario.RecordWithContextAsync(s.StageId, current.ContextHash, finished:reopened.ActivatedAt!.Value.AddTicks(-1))).StatusCode);
    Assert.Equal(s.SnapshotHash, await scenario.CurrentSnapshotHashAsync(s.StageId));
}
```

- [ ] **Step 2:** 运行`./scripts/check-contracts.sh integration --filter FullyQualifiedName~PipelineRecoveryTests`，确认RED发生在attempt/context/时间/快照断言；并发reopen只一个新进行中attempt，相同键只同一回执。
- [ ] **Step 3:** 实现方法及对应POST（pause/resume/cancel/reopen都带If-Match）。手动Paused只有未过期且前提合法才能resume；失败/超时须reopen，新窗口后RunActive。未发取消旧候选新mapping/precheck/approval；已发失败沿原目标精确快照恢复链；已ACK保持实际配置重新验证。OriginAttemptId→原正式申请，Promotion.StageAttemptId不重写，证据存CurrentAttempt；已有在途Publishing先等真实结果。取消保留事实、取消未执行候选，不自动回滚，恢复必须合法当前权限和实际目标；来源不合法不阻断已发精确恢复，但阻断后续新候选。定义/行为不可保全则Invalidated，终态只保留原合法恢复/回滚和历史事件。
- [ ] **Step 4:** 新类GREEN，跑P2/P4/P5/P7/P8；验证曾Completed/Passed的普通后续发布仍保留原历史、原人工回滚只影响选定环境且不回滚访问地址。
- [ ] **Step 5:** 提交 `feat(pipeline): reopen stage attempts without rewriting deployment history`。

## Task 10 (P10): 授权读取、委托入口、追溯与总览

**Files:** Create `Delivery/Pipelines/PipelineReadService.cs`；Modify `PipelineContracts.cs`、`PipelineEndpoints.cs`、`Delivery/PromotionReadService.cs`、`DeliveryOverviewService.cs`、`Releases/ReleaseService.cs`、`Delivery/ReleaseArtifactService.cs`、`src/WebApi.Contracts/Releases/ApprovalInboxContracts.cs`、`src/WebApi.Infrastructure/Releases/ApprovalInboxService.cs`；Test `tests/WebApi.Integration.Tests/PipelineReadTests.cs`、`PipelineOverviewTests.cs`。

**Interfaces:** Read `ListPipelinesAsync(Guid projectId, int page, int size, ActorContext actor, CancellationToken ct)`→`Task<PipelinePageDto<PipelineDto>>`；`ListRunsAsync(Guid? projectId, int page, int size, ActorContext actor, CancellationToken ct)`→`Task<PipelinePageDto<PipelineRunDto>>`；`GetRunAsync(Guid id, ActorContext actor, CancellationToken ct)` / `GetStageAsync(Guid id, ActorContext actor, CancellationToken ct)`→对应DTO。`PipelinePageDto<T>(IReadOnlyList<T> Items, int? Total, int Page, int Size, string Coverage)`，默认50最大100；`PipelineTraceDto(Guid? PipelineId, Guid? RunId, Guid StageId, int Order, string Visibility)`用于原页面安全关联，未经授权不返回其他环境正文。`DeliveryOverviewDto`新增可空PipelineRuns/PipelineCounts及PipelineVisibility，`PipelineRunCounts(int Total,int Active,int Paused,int TimedOut,int Completed)`；旧Counts/Promotions仍保留，排除PipelineOrigin后含义为独立晋级。新StageEligibility含CanPrepare/CanMaterialize/CanRecord/CanRequestAcceptance/CanReopen/CanCancelRun和安全ReasonCodes；Run级Cancel资格须全链可见且项目read_write。

- [ ] **Step 1:** 测试授权后计数、Full/Partial/Restricted、10秒503、默认/上限、隐藏名称URL角色凭证和报告下载；Focus5与统计去重：

```csharp
[Fact] async Task StageDelegateCanActWithoutEarlierChainDisclosure() {
    var s = await scenario.SeedFourEnvironmentDelegateAsync();
    var stage = await scenario.ReadStageAsAsync(s.CurrentStageId, s.Delegate);
    Assert.True(stage.Eligibility.CanPrepare); // 委托具当前目标+直接来源范围和原操作权限
    var run = await scenario.ReadRunAsAsync(s.RunId, s.Delegate);
    Assert.Equal("Partial", run.Coverage); Assert.Null(run.RootArtifactId);
    Assert.DoesNotContain(s.DeniedEnvironmentName, await scenario.LastResponseTextAsync());
    Assert.Null((await scenario.ListRunsAsAsync(s.Delegate)).Total);
}
[Fact] async Task OverviewDoesNotCountPipelinePromotionsAsStandaloneDeliveries() {
    await scenario.SeedOneStandaloneAndOnePipelineDeliveryAsync(); var view = await scenario.OverviewAsync();
    Assert.Equal(1, view.StandaloneCount); Assert.Equal(1, view.PipelineRunCount);
}
```

- [ ] **Step 2:** 运行`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~PipelineReadTests|FullyQualifiedName~PipelineOverviewTests'`行为RED，范围组合包含仅项目/仅环境、缺source contract、委托不能全链cancel、HTTP直接访问和附件二次撤权；不能只测试角色名称。
- [ ] **Step 3:** 实现GET列表/Run/Stage、no-store和数据库预算；先授权再分页/计数，Restricted字段null。Stage授权仅当前环境+直接来源（按所需契约/政策可见性），Run全貌需全链权限，Trace安全解析。旧Artifact可跨多个历史Stage使用，追溯分页不假定一对一。总览独立Promotion排除GateOrigin=PipelineRunStage，Pipeline按授权Run计数，不把每跳Release算独立交付；保留真实节点视图。错误只安全reason code，不返回无权资源信息。
- [ ] **Step 4:** 新类GREEN，回归`DeliveryOverviewTests`、`DeliveryConsoleTests`、`ReleaseArtifactServiceTests`、`VerificationReportTests`及`ApprovalInboxTests`。
- [ ] **Step 5:** 提交 `feat(pipeline): expose scoped runs stages and delivery trace`。

## Task 11 (P11): Console流水线列表、编辑和显式激活

**Files:** Create `console/src/delivery/pipeline-state.mjs`、`.d.mts`、`PipelineDefinitionEditor.tsx`、`pages/ReleasePipelines.tsx`、`ReleasePipelineDetail.tsx`、`console/tests/pipeline-definition.test.mjs`；Modify `console/src/main.tsx`、`sidebar-model.mjs`、`.d.mts`、`delivery/delivery-state.mjs`、`DeliveryPolicyEditor.tsx`、`console/tests/sidebar.test.mjs`。

**Interfaces:** Produces `validatePipelineDefinition(definition, environments): string[]`与`pipelineAuthorityKey(actor, resourceId, attemptId, profileHash, epoch): string`纯函数；`PipelineDefinitionEditor` props `{value:PipelineDefinition, environments:EnvironmentChoice[], readOnly:boolean, onChange:(value)=>void}`，实际DTO类型与P1同名TypeScript映射；`EnvironmentChoice`为编辑器只读选项 `{id:string,projectId:string,name:string,active:boolean,isProduction:boolean}`，从实际环境DTO显式映射。列表/详情使用P2/P10端点。旧deliveryPolicyLabel新增PipelineRequired标签；此模式旧连接表单显示只读摘要、生效版本链接及显式退出命令。

- [ ] **Step 1:** 添加Node行为测试（生产三项不能删除、2–8/参数界限、草稿保留、相同域不同阶段上下文key不同）、路由/权限导航回归：

```javascript
test('production requirements cannot be removed', () => {
  const input = productionDefinitionWith(['EntryConnectivity']);
  assert.ok(validatePipelineDefinition(input.definition, input.environments).length > 0);
});
test('authority identity includes stage attempt and profile', () => {
  assert.notEqual(pipelineAuthorityKey(actor, 'stage-1', 'attempt-1', 'hash', 1),
                  pipelineAuthorityKey(actor, 'stage-1', 'attempt-2', 'hash', 1));
});
```

- [ ] **Step 2:** `"$WEBAPI_NODE" --test console/tests/pipeline-definition.test.mjs console/tests/sidebar.test.mjs`实际断言RED；模块尚未存在先写可导入stub，模块错误不计RED。
- [ ] **Step 3:** 实现标准表单：环境排序、必需类型、独立有效期/等待时限、审批模板和只读ACK时限；发布版本与激活明确分开，immutable版本只读、归档前先合法停用。显示当前授权和安全错误、412保留草稿供重新载入，主体/资源/epoch隔离异步响应、撤权清空。生产要求不能删；不展示脚本/分支配置。请求仍需服务端全部鉴权，前端校验仅帮助编辑。
- [ ] **Step 4:** 两文件GREEN及`./scripts/check-console.sh`通过Node测试与TypeScript/build；以P10真实API验证草稿/版本/激活回执，界面外观最终在P13逐张验收。
- [ ] **Step 5:** 提交 `feat(console): configure and activate standard delivery pipelines`。

## Task 12 (P12): Console运行、阶段办理及原页面联动

**Files:** Create `console/src/delivery/PipelineStageActions.tsx`、`pages/ReleasePipelineRuns.tsx`、`ReleasePipelineRunDetail.tsx`、`ReleasePipelineStageDetail.tsx`、`console/tests/pipeline-run.test.mjs`；Modify `main.tsx`、`delivery/pipeline-state.mjs`、`.d.mts`、`pages/Approvals.tsx`、`ReleaseDetail.tsx`、`ReleaseArtifacts.tsx`、`DeliveryOverview.tsx`、`console/src/delivery/PromotionWizard.tsx`及对应Console测试。

**Interfaces:** Consumes P4/P5/P6/P9/P10 DTO和命令；Produces `pipelineStageActions(stage, actor): PipelineAction[]`纯资格投影、`acceptPipelineResponse(expectedKey, currentKey, response): boolean`异步隔离；`PipelineAction`为动作字串联合：prepare-promotion/mapping/precheck/submit/approve/publish/materialize-artifact/record-verification/request-acceptance/verify-production/pause-run/resume-run/cancel-run/reopen-stage；`PipelineStageActions` props `{stage:PipelineStageDto,onChanged:()=>void}`。生产验证复用既有接口及新增Attempt/Profile绑定，非生产用Stage端点，不替操作者隐式生成Artifact。

- [ ] **Step 1:** Node测试暂停/超时/Publishing分别动作、stale上下文结果丢弃、历史Completed与当前节点分离、Restricted无0；Focus5对应用例：

```javascript
test('delegate sees permitted current-stage action without chain details', () => {
  const stage = delegatedStage({canPrepare:true, canCancelRun:false});
  assert.ok(pipelineStageActions(stage, delegate).includes('prepare-promotion'));
  assert.ok(!pipelineStageActions(stage, delegate).includes('cancel-run'));
});
test('old attempt response cannot populate a reopened stage', () => {
  assert.equal(acceptPipelineResponse('actor/stage/attempt-1', 'actor/stage/attempt-2', {}), false);
});
```

- [ ] **Step 2:** 跑`"$WEBAPI_NODE" --test console/tests/pipeline-run.test.mjs`行为RED；保持可导入stub，不把导入错误当行为失败。
- [ ] **Step 3:** 实现线性进度与单Stage委托入口，展示当前阶段、等待原因/办理资格、阶段截止与ACK截止、实际配置/序列/节点、证据/验收、历史attempt/恢复。显式“继续测试”、人工映射/预检/审批/发布/verify、pause/resume/reopen/cancel；只展示服务端允许动作。旧页面以安全Trace链接Run/Stage，返回保留筛选，总览分独立晋级/流水线。阶段摘要更新使旧表单过期，409引导重新读取，412保留可编辑草稿，撤权和主体切换清空。表单label、错误关联、Tab/Enter/焦点遵循现有组件。
- [ ] **Step 4:** 新类GREEN及`./scripts/check-console.sh`全量；P13的真实浏览器用例必须在1440/1280分别证明键盘、冲突、撤权、委托单阶段及旧页面返回。
- [ ] **Step 5:** 提交 `feat(console): operate pipeline stages and trace actual delivery facts`。

## Task 13 (P13): 固定候选真实链、故障、冷备恢复与最终审查

**Files:** Create `scripts/check-pipeline.sh`、`scripts/delivery/pipeline-cli.mjs`、`pipeline-scenario.mjs`、`pipeline-faults.mjs`、`pipeline-evidence.mjs`、`pipeline-browser.mjs`、`tests/delivery/pipeline-evidence.test.mjs`；Modify `scripts/runtime/acceptance-backup.mjs`、`tests/runtime/backup.test.mjs`、`tests/runtime/integration/backup.test.mjs`；Create `docs/evidence/release-pipeline/`下verification、review、coverage文件（证据脱敏，私密运行资料留.runtime）。更新前述测试支持只限本任务真实链需要。

**Interfaces:** Shell `check-pipeline.sh domain|integration|gateway|console|e2e|faults|browser|verify [revision-or-directory]`，前四复用既有脚本；`runPipelineScenario({sourceRevision,directory,chains:[2,4],browser,nodeRuntime,chromiumExecutable})`→Promise证明对象；`validatePipelineProof(proof, files)`→`{passed:boolean,errors:string[]}`。证明schema记录固定源码SHA/镜像/包文件、环境实际部署/每跳hash/证据、故障与截图、报告冷恢复、owner清理、企业验收字段。e2e/faults/browser分清所执行范围，verify只读已有证明，不暗中重跑或把空范围当通过。

- [ ] **Step 1:** 写证明拒绝测试和受控备份扩展测试；固定2/4链的场景断言为：

```javascript
test('proof rejects incomplete actual multi-environment delivery', () => {
  const p = validPipelineProof(); p.chains[1].stages[2].allNodesAcknowledged = false;
  assert.equal(validatePipelineProof(p, proofFiles).passed, false);
});
test('cold backup includes every typed stage group and reports', () => {
  const p = validPipelineProof(); p.backupRestore.reportSha256 = 'wrong';
  assert.equal(validatePipelineProof(p, proofFiles).passed, false);
});
// 真实场景最终断言；不是seed伪装：
assert.equal(chain.runStatus, 'Completed');
assert.ok(chain.stages.every(s => s.behaviorHash === chain.rootArtifactHash));
assert.ok(chain.stages.every(s => s.actualCallsPassed && s.allNodesAcknowledged));
```

- [ ] **Step 2:** 跑`"$WEBAPI_NODE" --test tests/delivery/pipeline-evidence.test.mjs tests/runtime/backup.test.mjs`，先可编译验证器stub，缺ACK/错报告摘要/外来资源owner/漏Stage组必须behavior RED；不在原4192制造故障。
- [ ] **Step 3:** 实现场景/证明脚本和类型化备份扩展。使用获准隔离资源、固定commit源码构建，2链TEST→PROD及4链DEV→TEST→UAT→PROD，各环境两个实际Gateway、独立后端/访问入口/凭证授权，真实调用与ACK；初始来源和生产回滚基线也实际发布。非生产至少覆盖无审批和有两级审批，三类测试人工登记与验收、生产独立3验证；说明自建本机测试业务内容。备份限定`PipelineStageGroup`有序2–8组及受控服务/卷类型，Manifest含环境/Stage、owner及报告；禁止任意卷名/借用外来资源，不改原备份所有权规则。真实故障覆盖跳阶段/旧API绕过、错根hash/映射修订/共享凭证、错Profile/时间/过期/显式撤销、队列撤权/Run申请人、并发Run/attempt、Stage超时+部分应用/迟到ACK、Worker重启、三支reopen及精确retry/人工rollback、受限委托和异步旧表单。浏览器场景包含实际SSO、1440/1280、键盘、412/409、撤权及旧页面联动；每张截图人工查看，不以文件存在算验收。
- [ ] **Step 4:** 固定P1–P12候选SHA后跑Domain/Integration/Gateway/Console全量、runtime/delivery脚本测试及新增完整`check-pipeline.sh e2e "$PIPELINE_REVISION"`，附故障/浏览器/verify、真实冷备恢复（所有Stage Gateway及报告下载hash）和owner清理0残留。Native方法在此请求一次新最终审查者，审查全分支规格/安全/证据；所有Critical/Important修复并重跑受影响测试、生成新固定候选，Minor明确处置。没有绿灯或审查未闭环不安装；真实企业DNS/TLS/LB、企业业务验收仍分别标未执行。
- [ ] **Step 5:** 提交 `test(pipeline): verify fixed multi-environment delivery and recovery`，封存审查rulings与覆盖账本，记录最终产品候选SHA和镜像digest；本提交只封证据时注明应用源码仍为其所验证产品SHA。P14使用此候选，不从主目录工作树构建。

## Task 14 (P14): 原本机实例保全安装及交付封存

**Files:** Create 本期安装验证脚本 `scripts/delivery/pipeline-installation.mjs`、`pipeline-maintenance.mjs`、`tests/delivery/pipeline-installation.test.mjs`及`docs/evidence/release-pipeline/installation.md`、`delivery-index.md`。`pipeline-maintenance.mjs`仅生成原私有目录`.runtime/local/manage-pipeline.sh`及本期摘要清单，不覆盖已有`.runtime/local/manage-delivery.sh`或其固定旧工具包。原.private backup/installation-records保留秘密，不进Git；更新本计划勾选与规格安装状态。

**Interfaces:** `verifyPipelineInstallation(before, after, candidate)`→`{passed:boolean, errors:string[]}`：before/after含数据库旧表按PK规范化行hash、报告文件hash、原入口/端口/卷/身份配置、旧维护工具清单和实际应用文件清单；candidate含P13源码SHA/digest/迁移/权限目录及预期变更允许集。允许集仅新增Pipeline表、旧表新增NULL/default列、明确迁移元数据/权限catalog、应用包工具升级和本次真实命令的新增审计/幂等/登录回执，不授权重写历史业务行。心跳、队列租约等持续变化列按现有保全规则单独记录，不混入静态行hash；新增回执须精确ID/命令关联，不泛化放行整表。部署沿用原.owner验证维护机制，不生成绕过备份的新一键工具。

- [ ] **Step 1:** 编写保全验证器行为测试：旧业务行/报告/Secret配置/第三方工具改变拒绝，新增3权限仅Admin合法、旧显式授权保留、旧A+B发布事实及运行v/seq不得变动：

```javascript
test('installation rejects rewriting prior delivery history', () => {
  const {before, after, candidate} = installationFixture(); after.protectedRows[0].hash = 'changed';
  assert.equal(verifyPipelineInstallation(before, after, candidate).passed, false);
});
test('installation rejects accidental default grants outside admin', () => {
  const {before, after, candidate} = installationFixture(); after.newDefaultPipelineGrantsOutsideAdmin = 1;
  assert.equal(verifyPipelineInstallation(before, after, candidate).passed, false);
});
```

- [ ] **Step 2:** 跑`"$WEBAPI_NODE" --test tests/delivery/pipeline-installation.test.mjs`behavior RED，所有损坏数据只在fixture；维护生成器增加错owner/缺报告卷/旧工具覆盖拒绝断言，原实例仍Ready，不提前停机。
- [ ] **Step 3:** 实现只读保全验证器和受控安装采证：先采原源码dirty状态、数据库旧行/业务关系、全部受保护报告/配置/秘密文件摘要、维护工具及owner容器/卷清单，生成私密含报告冷备；按现有维护流程用P13固定包替换已授权应用，迁移+`dotnet /app/migrator/WebApi.Migrator.dll --seed-catalog`受控刷新目录，再Ready/源包文件摘要核验。原项目不自动激活Pipeline、不新增业务发布；保留4192/4196/4197、4194 IdP及独立4193/4180实例配置。新增维护入口若确有需要使用本期固定摘要包装器，不能覆盖/丢弃旧已登记工具；不碰原源码本地改动。安装失败按已验证私密备份恢复固定旧包及库，先保护事实再报告。
- [ ] **Step 4:** 安装验证器GREEN；实际安装后核验旧A+B业务行/报告/秘密与授权保全、权限仅Admin新增3项、真实应用文件与固定包一致、原实例Ready且原v/seq保持。原4192实际SSO及授权UI、只读定义/运行入口和Viewer受限显示逐张检查；闭环发布仍以P13隔离证据为准，不冒用原业务。再作本期安装后冷备及独立恢复读取证据，按owner清理本期测试资源，确认原源码dirty清单未变。记录本机安装通过/隔离闭环通过/企业验收未执行三种事实。
- [ ] **Step 5:** 提交 `docs(pipeline): seal reviewed local installation and preservation evidence`，只提交脱敏文档/脚本/测试与状态记录；生成最终交付索引和完整任务/审查处置账本。保留用户分支/工作区，不自动合并、推送或删除；清理仅本期明确owner临时资源，私密备份保留。

## 自查与执行交接

|规格要求|责任任务|
|---|---|
|§1–2 标准线性环境/参数/强制生产门禁/范围边界|P1、P6、P7、P11|
|§3 不可变定义、显式激活、模式退出、单Run及旧PUT门禁|P2、P4、P7|
|§4 根制品/来源导入/逐环境制品、验收、生产验证/人员独立|P4–P8|
|§5 共用上下文/事务/锁/审批/Worker不代人发布|P3、P6–P8|
|§6 六表/旧NULL/唯一性/恢复尝试/报告归属|P1、P5、P9、P13–P14|
|§7 状态/截止/ACK/撤销/三支恢复/历史保全|P4、P7–P9、P12–P14|
|§8 范围/权限/委托/Partial/并发/无秘密泄露|P1–P4、P7、P9–P12|
|§9 接口/页面/导航/统计/异步响应与可访问性|P2、P4–P6、P9–P12|
|§10–11 真实2/4环境链、故障、冷备、源码固定、审查与原安装|P13–P14|

自查检查点：各任务有行为RED/GREEN与提交；共享类型名称与既有DTO对齐，直接前置Profile和本StageProfile分开；五项Review Focus均有归属测试；P13隔离闭环和P14原安装分开，防止将准备或测试种子当已部署事实。新增任务中的服务/接口必须在拥有任务实现，后续任务只消费；未完成全链前不能宣称功能完成。

计划批准后按Native逐项执行，使用executing-plans技能维护任务记录；在P13做新最终独立审查，P14完成原实例安装后交付。不因本计划写入/提交更改现有应用或运行数据库。
