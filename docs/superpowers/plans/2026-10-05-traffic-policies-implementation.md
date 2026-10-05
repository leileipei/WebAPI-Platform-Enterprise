# WebAPI Enterprise V2 流量策略 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成原第16、17页真实策略治理、Redis跨网关限流、节点熔断和兼容的审批发布/回滚闭环。

**Architecture:** 复用现有Policy/RoutePolicyBinding和治理事务，编译成自足的2.1快照；新网关兼容2.0。请求在既有generation lease内完成认证、共享令牌桶、节点熔断和代理；观测仅记录实际决策，不参与身份或策略执行。

**Tech Stack:** 现有.NET10、EF Core/PostgreSQL、StackExchange.Redis、YARP、React/TypeScript、OpenTelemetry三源。使用现有锁定依赖与镜像，不升级无关依赖。

**Spec:** [已确认规格](../specs/2026-10-05-traffic-policies-design.md)。用户在正式文档交付后回复“确认”，书面规格已通过；本计划待审阅。此前核心与观测阶段已经选择Native，本批保留该执行方式：当前会话逐任务实施，完成后独立审查；不主动转为逐任务子代理执行。

## Global Constraints

- 沿用已经选定的设计系统、1440px桌面布局、组织/项目/环境边界、审批发布、不可变快照、双网关确认与回滚。
- 保存工作配置不会直接改变在途请求或当前网关配置。
- 本批接入四种类型：`authentication`（ApiKey/Anonymous）、`timeout`、`rate_limit`、`circuit_breaker`。其余类型保留为后续能力。
- 现有真实权限为 `policy.read`、`policy.write`、`route.read`、`route.write`；保留这些权限，不新增manage别名。
- 策略Type和Scope创建后不可修改；跨Scope的详情、引用、分页总数、JSON、导出和错误消息均经过服务端授权。
- Config必须是大小不超过32KiB的JSON对象，按类型严格校验未知字段、数值、枚举与重复属性。
- 单路由最多四种策略各一个；包括停用绑定，同类型不能并存，替换需原子完成。Priority范围0–1000。
- 策略更新使用If-Match/VersionNo，绑定更新使用路由Revision；缺失前置条件返回428，过期返回412，编辑器保留用户输入。
- 认证、限流、熔断和代理均从同一租用generation读取运行配置；ApplicationId使用独立执行上下文，不依赖遥测。
- 执行顺序固定：认证/授权 → 限流 → 熔断准入 → Destination选择及代理 → 熔断完成记录；超时沿用路由机制。
- Gateway数据面不查询PostgreSQL，不逐请求访问控制面。Redis超时后的不确定消耗结果不自动重试。
- 新网关读取2.0和2.1；2.1发布前校验全部已启用目标节点在线且支持目标协议，Worker持锁构建时再次验证。
- 不更新已有历史快照的原始字节、hash或配置版本；回滚使用历史原字节和新的deploymentSequence。
- 故障测试使用随机专用Compose项目、卷、私有秘密和Redis前缀；不停止4192长期演示或复用其数据卷。
- API Key、Cookie、连接密码和令牌桶内部key不得进入日志、Trace、审计、截图或交付证据。
- 现有未提交交付文件保留。任务结束只检查/保存对应文件，不自动Git提交、推送、合并或覆盖旧ZIP。
- 实施前按using-git-worktrees检查现有附属工作区、隔离需求与未提交状态；不能从只包含HEAD的工作区遗漏本批已确认规格。

## Review Focus

以下五项已补入对应任务，最终审查仍需跨调用链复核。

1. 在2.1发布构建之后替换成旧能力实例：能力不能继承，旧实例ACK不能代表新实例成功；Task 6、12。
2. Redis脚本已扣令牌而客户端等待超时：不能自动重试扣两次；Reject/Allow和取消需准确区分；Task 7、9。
3. HalfOpen旧探测迟到成功，但另一个探测已重新打开：旧epoch不能将新Open关闭；Task 8、9。
4. 仅改一个路由的认证或普通路径，原来使用共享认证：不能改其他路由，也不能默默替换共享绑定；Task 3。
5. 升级前已有Draft以及资源后来变更的Draft：现有完整修订草稿继续可提交，过期/早期缺修订的草稿需显式刷新Review，不丢失选择或无提示冻结新配置；Task 5、11。

## 文件职责、命令与执行边界

路径均相对`enterprise/`。新目录按职责分开，不批量格式化现有压缩代码。Public DTO在Contracts；纯配置/状态决策在Domain；授权/EF/冻结在Infrastructure；中间件、Redis限流和运行状态生命周期在Gateway；console复用Shell/UI。

测试入口由Task 1新增 `./scripts/check-policies.sh`。它包装现有`deploy/compose.test.yml`，以`-p`强制随机项目名并使用秘密覆盖配置，绝不采用固定`webapi-enterprise-core-test`。每次结束仅清理其自己验证过Compose标签的容器/卷。所有Docker命令按当前沙箱规则申请执行权限，不将凭证打印到终端。

可用模式：`domain|integration|gateway`透传dotnet test参数；`console`调用现有check-console；`e2e|browser`使用独立策略验收脚本；`verify`只读验证证据/包。Node默认读取WEBAPI_NODE，缺失时采用已经存在的`/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node`；pnpm使用WEBAPI_PNPM或已有PATH，缺失时查找现有运行时，不自动安装依赖。

执行记录保存 `docs/evidence/policies/execution-ledger.md`。每项记录真实RED/GREEN结果、时间、涉及文件和限制；任务检查点不等同于Git提交。源码证据必须对应实际构建的源快照：若代码尚未提交，以脱敏构建目录的文件hash清单和镜像digest固定，不把HEAD冒充全部新源码。

## Task 1：四类配置契约、纯校验与隔离验证入口

**Files:** Create `src/WebApi.Contracts/Policies/PolicyContracts.cs`、`src/WebApi.Domain/Policies/PolicyConfigurationValidator.cs`、`PolicyBindingRules.cs`、`tests/WebApi.Domain.Tests/PolicyConfigurationTests.cs`、`scripts/check-policies.sh`、`deploy/compose.policies-test.yml`、`tests/runtime/policy-runner.test.mjs`。Modify仅相关项目引用（若需要）；不改依赖版本。

**Interfaces:** `PolicyConfigurationValidator.Normalize(string type,string config) : string`返回Canonical JSON或422 ApiException；`PolicyBindingRules.Validate(IReadOnlyList<PolicyBindingConfiguration> bindings,bool defaultRequireApiKey) : EffectiveRoutePolicies`，输入含源ID/Type/Config/Enabled/Priority，输出RequireApiKey、可空TimeoutMs和高级配置。Contracts定义SavePolicyRequest(Name,Type,Config,Enabled)、PolicyDto(Id,OrganizationId,ProjectId,Name,Type,Config,Enabled,VersionNo)、CopyPolicyRequest(Name,TargetProjectId)、PolicyScopeRequest(OrganizationId,ProjectId)、PolicyBindingInput(PolicyId,Priority)、SaveRoutePoliciesRequest(Bindings)、RoutePoliciesDto(RouteId,Revision,Bindings)。高级配置类型RateLimitConfiguration、CircuitBreakerConfiguration的字段/范围逐字采用规格第6、7节。

- [ ] 写 `UnknownFieldDuplicateKeyAndOversizedJsonRejected`、`ExactRateBoundsAndRefillHorizon`、`CircuitRequiresFailureSource`、`AnonymousApplicationRouteRejected`、`DisabledDuplicateTypeStillRejected`；断言 `burst=1500/refillTokens=1000/windowMs=1000` 合法，`windowMs=9`、`failureRatio=0`、`priority=1001`为422，四种默认配置规范化后再次规范化结果相同。runner测试断言随机 `-p`、秘密覆盖、清理标签核验、0600和禁止默认项目。
  关键断言：`Assert.Equal(422, Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("rate_limit", invalidWindowJson)).Status);`；runner采用 `assert.notEqual(projectName, 'webapi-enterprise-core-test')`。
- [ ] 运行Node runner测试确认入口尚缺；实现安全包装入口，再运行 `./scripts/check-policies.sh domain --filter FullyQualifiedName~PolicyConfigurationTests`，确认因配置/规则缺失RED；记录真实失败点，不能以脚本权限失败替代产品RED。
- [ ] 实现上述契约、严格重复属性检测、大小/数值/枚举边界和规范化；拒绝未知类型，不执行JSON模板或动态脚本。Name/保留前缀属于管理校验，不能错误用于生成运行策略。
- [ ] 运行指定Domain测试和Node runner测试，预期全部GREEN；核验容器清理后没有本次项目容器/卷，既有项目未改。
- [ ] 检查任务文件diff与格式，记录检查点；不自动commit。

## Task 2：策略管理、授权、复制与引用分页

**Files:** Create `src/WebApi.Infrastructure/Policies/PolicyService.cs`、`PolicyReferenceService.cs`、`PolicyPreconditions.cs`、`src/WebApi.ControlPlane/Policies/PolicyEndpoints.cs`、`tests/WebApi.Integration.Tests/PolicyManagementTests.cs`。Modify `ControlPlaneApp.cs`注册、`Governance/ScopeResolver.cs`扩展策略Scope查询。使用现有Policy实体，无重建/重置迁移。

**Interfaces:** `PolicyService.ListAsync(PolicyScopeRequest scope,ActorContext actor,int page,int pageSize,string? type,string? search,bool? enabled,CancellationToken ct) : Task<PageResult<PolicyDto>>`；`GetAsync(Guid id,ActorContext actor,CancellationToken ct) : Task<PolicyDto>`；`SaveAsync(PolicyScopeRequest scope,Guid? id,SavePolicyRequest request,string? ifMatch,ActorContext actor,CancellationToken ct) : Task<CommandResult<PolicyDto>>`；`CopyAsync(Guid id,CopyPolicyRequest request,ActorContext actor,CancellationToken ct)`同返回；`DeleteAsync(Guid id,string? ifMatch,ActorContext actor,CancellationToken ct) : Task`。`PolicyReferenceService.ListAsync(Guid id,ActorContext actor,int page,int pageSize,CancellationToken ct) : Task<PageResult<PolicyReferenceDto>>`，DTO含ReferenceKind、EnvironmentId、ApiId、RouteId、ConfigVersion、SourceRevision和可见名称，不含Secret。`PolicyPreconditions.Require(string? ifMatch,long current) : void`缺失/空白为428，其余交由现有RevisionTag.Require处理412；只用于新策略管理/绑定接口，不改变既有全平台接口的缺tag行为。

- [ ] 写 `ProjectCannotEditOrganizationPolicy`、`ForeignScopeHiddenInDetailsAndTotals`、`CopyHasNewIdAndNoBindings`、`Etag428And412`、`IdempotentSaveAuditedOnce`、`ReservedNameAndImmutableTypeScope`。断言跨组织详情404；写权限不足403；缺tag428、旧tag412；复制后绑定数0；回放同幂等键不增VersionNo。
  关键断言：`Assert.Equal(HttpStatusCode.NotFound, foreignDetail.StatusCode); Assert.NotEqual(original.Id, copied.Id); Assert.Empty(copiedBindings);`。
- [ ] 运行 `./scripts/check-policies.sh integration --filter FullyQualifiedName~PolicyManagementTests` 确认RED。
- [ ] 实现规格第3、4节列表/详情/CRUD/复制/validate/references接口。服务端先限定Scope再分页；共享可读不扩大写权限；启停复用PUT。引用区分工作、冻结、desired和节点应用快照，历史已删除资源用安全ID展示。删除扫描所有保护引用，在治理锁内检查；错误不泄露不可见引用。
- [ ] 重跑该测试组；加实际PG断言保存字段与审计一致。删除完整状态矩阵在Task 5补足，不能提前标记P16全部完成。
- [ ] 检查并保存任务文件；记录P01/P02/P03的已覆盖子项。

## Task 3：原子绑定与共享认证兼容

**Files:** Create `src/WebApi.Infrastructure/Policies/RoutePolicyService.cs`、`tests/WebApi.Integration.Tests/RoutePolicyBindingTests.cs`。Modify `Routing/RouteService.cs`、`ControlPlane/Routing/RoutingEndpoints.cs`；复用Task 1组合规则、Task 2策略Scope/读取。

**Interfaces:** `RoutePolicyService.GetAsync(Guid routeId,ActorContext actor,CancellationToken ct) : Task<RoutePoliciesDto>`；`ReplaceAsync(Guid routeId,SaveRoutePoliciesRequest request,string? ifMatch,ActorContext actor,CancellationToken ct) : Task<CommandResult<RoutePoliciesDto>>`。`PolicyBindingRules`产生的RequireApiKey供DTO和发布共享使用；RouteService不得自行维护不同组合规则。

- [ ] 写 `BindingRequiresBothPermissionsAndBumpsRouteRevision`、`ReplaceDuplicateAndForeignPolicyRollsBack`、`PathEditKeepsSharedAuthentication`、`AuthToggleDetachesOnlyCurrentRoute`、`DisableAnonymousDefaultsToApiKey`、`TimeoutBindingOverridesBaseValue`。两路由共享认证，编辑其中一条开关后另一条绑定、Policy VersionNo与有效认证保持原值；只改path后原绑定ID不变。
  关键断言：`Assert.Equal(beforeSecondRouteBindings, afterSecondRouteBindings); Assert.Equal(sharedRevision, sharedPolicyAfter.VersionNo); Assert.Equal(beforeRevision + 1, replaced.Revision);`。
- [ ] 运行 `./scripts/check-policies.sh integration --filter FullyQualifiedName~RoutePolicyBindingTests` 确认RED。
- [ ] 实现整组替换、Scope与同类型校验、If-Match和事务审计。修改RouteService：普通编辑保留认证绑定；认证开关确实改变时创建/复用仅属于该路由的私有策略，并只替换当前认证绑定；不再以名称直接修改可能共享的Policy。不改原RouteAuth数据，清空认证绑定默认ApiKey。
- [ ] 重跑绑定测试和现有CatalogTests/ImportAndCredentialTests相关路由用例；原匿名、超时、授权行为保持。
- [ ] 保存检查点；记录P04/P05/P06覆盖。

## Task 4：2.1运行身份、部分合并与严格校验

**Files:** Create `src/WebApi.Domain/Policies/RuntimePolicyIdentity.cs`、`tests/WebApi.Domain.Tests/PolicySnapshotCompilerTests.cs`。Modify `Contracts/Runtime/SnapshotContracts.cs`、`Contracts/Gateway/GatewayReadContracts.cs`、`Domain/Runtime/SnapshotValidator.cs`、`Infrastructure/Releases/SnapshotCompiler.cs`、`Gateway/Configuration/RuntimeGenerationStore.cs`。

**Interfaces:** `RuntimePolicyIdentity.Create(Guid sourceId,long revision,string type,string normalizedConfig) : Guid`，SHA-256前128位以network字节序构造。`RuntimePolicyBinding(Guid PolicyId,int Priority)`；RuntimeRoute新增可选PolicyBindings，RuntimePolicy新增可选SourcePolicyId/SourceRevision；2.0序列化省略这些null字段。现有 `SnapshotCompiler.Compile(...)` 和 `SnapshotValidator.Validate(...)` 签名保持，内部复用Task 1校验。

- [ ] 写 `SharedPolicyTwoFrozenRevisionsCoexist`、`UnselectedApiKeepsOldBinding`、`DisabledPolicyPrunedFromSelectedRoute`、`TwoPointZeroFieldsAndBytesRemainCompatible`、`LegacyAnonymousRoutePromotionKeepsFoldedBehavior`、`TwoPointZeroCannotSmuggleAdvancedBinding`、`FoldedAuthTimeoutMustMatchBinding`、`IdentityCanonicalGoldenVector`。固定golden输入/结果由独立SHA计算得到；断言同源不同修订ID不同，未选路由的运行ID/配置不变，未知schema/缺引用/同类型冲突为422。由2.0保留到2.1的路由可以仅有折叠的认证/超时值；新增绑定存在时必须与折叠值一致，不将旧匿名路由错误升级为ApiKey。
  关键断言：`Assert.NotEqual(oldPolicy.Id, newPolicy.Id); Assert.Equal(oldBinding.PolicyId, unselectedRoute.PolicyBindings.Single().PolicyId); Assert.False(promotedLegacyAnonymous.RequireApiKey);`。
- [ ] 运行 `./scripts/check-policies.sh domain --filter FullyQualifiedName~PolicySnapshotCompilerTests` 确认RED。
- [ ] 实现规格第9、10节；最终无高级绑定输出旧2.0格式，有高级绑定输出2.1；保留旧基线路由折叠语义，按最终引用集GC新协议策略。运行ID冲突且内容不同拒绝，不能覆盖；generation Freeze同时冻结新增集合。
- [ ] 重跑新测试及原SnapshotCompilerTests；序列化反序列化与LKG旧fixture比较，不重写原证据快照。
- [ ] 保存检查点；记录P13/P14的编译覆盖。

## Task 5：发布预览全集、冻结、旧Draft刷新与删除保护

**Files:** Create `tests/WebApi.Integration.Tests/PolicyReleaseTests.cs`、`PolicyDeletionTests.cs`。Modify `Contracts/Releases/ReleaseContracts.cs`、`ReleaseCandidateBuilder.cs`、`ReleaseService.cs`、`HistoricalSnapshotService.cs`、`ControlPlane/Releases/ReleaseEndpoints.cs`、Task 2引用/删除服务；更新 `tests/WebApi.TestSupport/Support/ApiFixture.cs`、`DeploymentScenario.cs` 和 `tests/WebApi.Gateway.Tests/Support/GatewayFixture.cs`的发布准备助手。

**Interfaces:** `PreviewReleaseRequest(long BaseConfigVersion,IReadOnlyList<Guid> VersionIds)`；`ReleaseCandidateBuilder.PreviewAsync(Guid environmentId,PreviewReleaseRequest request,CancellationToken ct) : Task<FrozenReleaseCandidate>`只构建，不验证客户端全集；现有BuildAsync验证全集且拒绝重复项。`ReleaseService.PreviewSelectionAsync(Guid environmentId,PreviewReleaseRequest request,ActorContext actor,CancellationToken ct) : Task<FrozenCandidateView>`，POST `/environments/{id}/releases/preview`。FrozenCandidateView增Policies/Bindings（安全配置，无凭证hash）及可选PreconditionsCurrent；现有Draft GET preview用当前预览与已存修订比较，标识false时不改CandidateBytes。`RefreshReleasePreconditionsRequest(IReadOnlyList<ResourceRevision> ResourceRevisions)`和 `ReleaseService.RefreshPreconditionsAsync(Guid id,RefreshReleasePreconditionsRequest request,ActorContext actor,CancellationToken ct) : Task<ReleaseDto>`，POST `/releases/{id}/refresh-preconditions`，仅申请人Draft可用。保持既有Draft存储的CreateReleaseRequest格式，不引入版本包装或迁移。

- [ ] 写 `PreviewIncludesEveryResourceRevision`、`MissingPolicyOrRouteRevisionRejected`、`ChangedBindingAfterPreviewReturns412`、`PostSubmitEditDoesNotChangeFrozenHash`、`ExistingFullRevisionDraftSubmitsUnchanged`、`StaleDraftReviewDoesNotSilentlyRefresh`、`ProtectedReferencesBlockDeletionWithoutDisclosure`、`HistoryOnlyDeletionStillAllowsRollback`。当前实现创建Draft时已经将完整修订存入CandidateBytes：升级后无变化的旧Draft正常提交；真正遗漏修订的早期草稿submit为422 missing_revision，过期为412。GET/当前预览/取消可用，PreconditionsCurrent=false时不改存储；申请人确认完整修订后原Draft可提交，原版本选择不变。已WaitingApproval/Ready的旧冻结候选不重新冻结。
  关键断言：`Assert.False(preview.PreconditionsCurrent); Assert.Equal(candidateBytesBefore, candidateBytesAfterPreview); Assert.Equal(frozenHashBeforeEdit, frozenHashAfterEdit);`。
- [ ] 运行 `./scripts/check-policies.sh integration --filter 'FullyQualifiedName~PolicyReleaseTests|FullyQualifiedName~PolicyDeletionTests'` 确认RED。
- [ ] 实现全集预览/验证、Draft显式刷新和旧存储兼容。创建与提交处于既有治理锁，Submit仍是冻结点；完整修订旧Draft不强制走新增刷新步骤。历史重试/回滚依据自足快照，不重新读取当前Policy。保护删除覆盖工作/进行中发布/desired/节点实际应用版本，释放后可删；不可见引用仅泛化错误。fixture改用真实预览API取得完整修订，不直接拼version-only请求绕过新规则。
- [ ] 重跑新测试及原ApprovalTests/PublishTests/RollbackTests；逐个更新旧测试准备代码，并保留“故意缺失修订”的负例。Assert历史hash、审计、两级独立审批不变。
- [ ] 保存检查点；记录P13/P15/P16和Review Focus 5。

## Task 6：当前实例能力、发布门槛与兼容读取

**Files:** Create `src/WebApi.Infrastructure/Gateway/SnapshotSchemaCapabilities.cs`、`tests/WebApi.Integration.Tests/PolicyNodeCapabilityTests.cs`。Modify `Contracts/Gateway/NodeContracts.cs`、`GatewayReadContracts.cs`、`Infrastructure/Gateway/NodeRegistry.cs`、`GatewayReadService.cs`、`Infrastructure/Releases/PublishCoordinator.cs`、`Gateway/Workers/NodeClient.cs`、`Gateway/Configuration/SnapshotActivation.cs`。

**Interfaces:** RegisterNodeRequest/NodeHeartbeat增加可选SupportedSnapshotSchemas；`SnapshotSchemaCapabilities.Read(GatewayNode node) : IReadOnlyList<string>`、`Merge(string? metadata,Guid instanceId,IReadOnlyList<string>? schemas) : string`、`RequireSupported(IReadOnlyList<GatewayNode> nodes,string schema) : void`。新网关声明 `['2.0','2.1']`；缺失仅2.0；管理DTO附能力供界面解释。现有元数据其他字段原样保留。

- [ ] 写 `MissingCapabilityMeansOnlyTwoPointZero`、`ReregisterDoesNotInheritOldInstanceCapability`、`OldNodeBlocksAdvancedBeforeDesiredOrSequenceChanges`、`WorkerRechecksCapability`、`MetadataFieldsPreserved`、`RollbackAndRetryUseTargetSchema`；在检查与Build之间换节点实例，断言Failed/gateway_schema_unsupported、desired/sequence未变、无Outbox/快照持久化；未来schema与超长数组422。
  关键断言：`Assert.Equal("gateway_schema_unsupported", release.FailureCode); Assert.Equal(beforeDesired, environment.DesiredConfigVersion); Assert.Equal(beforeSequence, environment.DeploymentSequence);`。
- [ ] 运行 `./scripts/check-policies.sh integration --filter FullyQualifiedName~PolicyNodeCapabilityTests` 确认RED。
- [ ] 注册/心跳在环境锁内更新当前实例能力；StartAsync和BuildNextAsync以最终目标schema验证全部启用节点；保留在线/最少节点规则。新网关解析2.0/2.1，未知协议/类型拒绝且不ACK；不能依AppVersion猜能力或缩减cohort。
- [ ] 重跑能力、AckTests及GatewayRuntimeTests兼容子集；旧2.0 LKG实际重启可用；验证失败保持路由/desired事实。
- [ ] 保存检查点；记录P14和Review Focus 1。

## Task 7：真实Redis令牌桶、等待预算与失败模式

**Files:** Create `src/WebApi.Gateway/Policies/IRateLimitStore.cs`、`RedisTokenBucketStore.cs`、`TokenBucketScript.cs`、`TrafficPolicySettings.cs`、`tests/WebApi.Gateway.Tests/RedisRateLimitTests.cs`。

**Interfaces:** `RateLimitRequest(Guid EnvironmentId,Guid RuntimePolicyId,Guid RouteId,Guid? ApplicationId,RateLimitConfiguration Configuration)`；`RateLimitDecision(RateLimitDecisionKind Kind,int RetryAfterSeconds)`，Kind为Allowed/Exceeded/StoreUnavailable；`IRateLimitStore.TakeAsync(RateLimitRequest request,CancellationToken ct) : ValueTask<RateLimitDecision>`。RedisStore只返回真实存储结果；Reject/Allow由Task 9解释。Settings提供Redis连接、业务前缀、DecisionTimeoutMs（默认100）；TokenBucketScript提供不可变生产Lua文本，时间只用Redis TIME。

- [ ] 写 `TwoClientsShareExactBurstAtFrozenRedisTime`、`ElapsedTimeRefillsAndSetsBoundedTtl`、`DifferentAppsAndRoutesIsolated`、`SameAppCredentialsShareBucket`、`TimeoutAfterDispatchDoesNotRedispatch`、`CancelledCallerNotReportedAsBackendFailure`。并发用例采用burst=3、refillTokens=1、windowMs=600000；测试脚本使用仅测试前置局部redis对象固定TIME，其他调用委托真实Redis，20并发严格3 Allowed/17 Exceeded；生产脚本文本不接受外部时间参数。超时故障适配器断言实际发送次数1。
  关键断言：`Assert.Equal(3, decisions.Count(d => d.Kind == RateLimitDecisionKind.Allowed)); Assert.Equal(17, decisions.Count(d => d.Kind == RateLimitDecisionKind.Exceeded)); Assert.Equal(1, sends);`。
- [ ] 运行 `./scripts/check-policies.sh gateway --filter FullyQualifiedName~RedisRateLimitTests` 确认RED。
- [ ] 单键Lua原子读/补/扣/TTL，数学定义与规格一致；使用独立multiplexer和预算，不占配置缓存连接。限流Key只用经过认证的系统ID，不保存RawKey；TTL范围1–7200秒，Key前缀测试随机。连接/脚本错误返回StoreUnavailable，超时后绝不重试，不将取消转换为熔断故障。
- [ ] 重跑真实Redis测试；对生产TIME的20请求验收按实际经过时间计算上限，不能误把补充当超发；测试不执行FLUSHDB。
- [ ] 保存检查点；记录P07/P09和Review Focus 2。

## Task 8：有限窗口与并发熔断状态机

**Files:** Create `src/WebApi.Domain/Policies/CircuitBreakerState.cs`、`src/WebApi.Gateway/Policies/CircuitStateRegistry.cs`、`tests/WebApi.Domain.Tests/CircuitBreakerTests.cs`。

**Interfaces:** `CircuitBreakerState(CircuitBreakerConfiguration configuration,TimeProvider clock)`；`TryEnter() : CircuitAdmission`含Allowed、Epoch、Probe、RetryAfterSeconds；`Complete(CircuitAdmission admission,CircuitOutcome outcome) : void`，Outcome为Success/Failure/Neutral/Cancelled。`CircuitKey(Guid InstanceId,Guid RuntimePolicyId,Guid RouteId,Guid RuntimeClusterId)`；`CircuitStateRegistry.Acquire(CircuitKey key,CircuitBreakerConfiguration configuration) : CircuitStateLease`，lease持有State并可Dispose；registry按generation引用数清理，不把sequence作为Key。

- [ ] 写 `WindowAndMinimumBeforeRatioOpens`、`OpenWaitThenLimitedHalfOpen`、`EnoughTwoHundredsCloses`、`LateSuccessCannotCloseReopenedEpoch`、`CancelledProbeReleasesSlot`、`NeutralFourHundredDoesNotRecover`、`OnlyOneWindowOfBucketsRetained`。用测试内可推进TimeProvider：minimumRequests=20、ratio=0.5，19样本不打开，第20且10失败打开；30000ms之前拒绝，到边界允许；halfOpenMaxRequests=1只有一探测；3成功关闭。
  关键断言：`Assert.False(state.TryEnter().Allowed); clock.Advance(TimeSpan.FromMilliseconds(30000)); Assert.True(state.TryEnter().Probe); Assert.False(secondConcurrentProbe.Allowed);`；迟到成功之后仍断言拒绝。
- [ ] 运行 `./scripts/check-policies.sh domain --filter FullyQualifiedName~CircuitBreakerTests` 确认RED。
- [ ] 以单调时间、1秒环形桶、锁保护epoch/探测名额实现状态；窗口桶上限300。Neutral不累计HalfOpen成功，Cancelled不计故障。Registry只共享同一运行身份/路由/cluster状态，实例或配置变化新建；最后引用释放后删除退役状态。
- [ ] 重跑状态机测试；补100并发准入和迟到结果断言，验证总探测数不超过配置、取消后无永久占位。
- [ ] 保存检查点；记录P10和Review Focus 3。

## Task 9：同generation中间件、请求归属与故障分类

**Files:** Create `src/WebApi.Gateway/Policies/TrafficExecutionContext.cs`、`TrafficPolicyMiddleware.cs`、`CircuitOutcomeClassifier.cs`、`tests/WebApi.Gateway.Tests/TrafficPolicyPipelineTests.cs`。Modify `GatewayApp.cs`、`Security/ApiKeyMiddleware.cs`、`Configuration/RuntimeGenerationStore.cs`、`SnapshotActivation.cs`。

**Interfaces:** `TrafficExecutionContext`保留RuntimeGeneration、RuntimeRoute、已授权Guid? ApplicationId、最多两条PolicyDecision；`From(HttpContext)`获取，与遥测开关无关。`TrafficPolicyMiddleware.InvokeAsync(HttpContext ctx,IRateLimitStore limiter,CircuitStateRegistry circuits)`消费Task 7/8。`CircuitOutcomeClassifier.Classify(HttpContext ctx,CircuitBreakerConfiguration config,bool forwarded) : CircuitOutcome`根据上游响应、Forwarder错误、RequestTimeout和ClientAborted判定。

- [ ] 写 `TelemetryOffStillPartitionsByAuthenticatedApp`、`AuthRejectNeverConsumesOrTripsCircuit`、`LimitedRequestNeverTouchesBackend`、`ConsumedTokenNotRefundedAfterCircuitReject`、`RedisRejectAndAllowProduceDifferentFacts`、`LateOldRequestUsesOldPolicyGeneration`、`ClientAbortAndNoDestinationNotCountedAsUpstreamFailure`。断言认证401/403和限流429后BackendProbe调用数0；StoreUnavailable→Reject503/Allow实际200；旧在途请求保持旧PolicyId/sequence。
  关键断言：`Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.Equal(0, backendCalls); Assert.Equal(oldSequence, lateRequest.DeploymentSequence);`。
- [ ] 运行 `./scripts/check-policies.sh gateway --filter FullyQualifiedName~TrafficPolicyPipelineTests` 确认RED。
- [ ] ApiKeyMiddleware认证/授权成功后写独立上下文，在同lease内执行新中间件；顺序按规格固定。登记generation所需CircuitStateLease，注册/激活/回滚失败/退役各路径正确释放。拒绝JSON只给安全code/trace标识；429、circuit_open含Retry-After；故障503不上游。依据真实代理尝试分类，不将健康检查无可用Destination的主动拒绝伪造成上游HTTP响应。
- [ ] 重跑管线测试和完整GatewayRuntimeTests；确认AdmissionGate不延长到Redis/后端I/O，遥测关闭仍工作，异常/取消不泄漏lease或探测名额。
- [ ] 保存检查点；记录P08/P11/P12和P15的generation边界。

## Task 10：策略计数、脱敏日志/Trace和监控查询

**Files:** Create `src/WebApi.Contracts/Policies/PolicyObservationContracts.cs`、`tests/WebApi.Gateway.Tests/PolicyTelemetryTests.cs`、`tests/WebApi.Integration.Tests/PolicyObservationTests.cs`。Modify `Gateway/Observability/RequestTelemetryContext.cs`、`TelemetryAttributes.cs`、`GatewayTelemetryRecorder.cs`、`Infrastructure/Observability/LokiLogSource.cs`、`TraceProjection.cs`、`CsvLogExporter.cs`、`PrometheusMetricSource.cs`、`ObservationQueryService.cs`、`Contracts/Observability/LogContracts.cs`、`TraceContracts.cs`。

**Interfaces:** `PolicyDecisionDto(Guid PolicyId,string PolicyType,long PolicyRevision,string Decision,string? RejectionReason)`；AccessLogDto与Trace投影增加最多两条可选决策，旧记录为空集合。新增计数器 `webapi_gateway_policy_decisions_total`；标签仅environment/node/sourcePolicyId/type/decision，现有Outcome保持。新增监控值 `circuit_rejected_count`、`rate_limit_store_unavailable_count` 和对应趋势，单位requests，SourceState沿用现有包络；降级放行单独Decision查询，不冒充成功限流。

- [ ] 写 `PolicyDecisionsVisibleWithoutSecrets`、`TwoDecisionsBoundedAndOldLogsReadable`、`PartialSourceKeepsNullPolicyKpi`、`Gateway503HasNoFakeDestination`、`NoRevisionOrAppDimensionOnPolicyCounter`。捕获信号断言RawKey/redisSecret/内部key均不存在，旧Log仍读取，503 Destination为空；关闭遥测不影响Task 9行为，开启时记录准确两条决策。
  关键断言：`Assert.DoesNotContain(secretMarker, capturedSignals); Assert.Null(gatewayRejectedLog.DestinationId); Assert.Null(partialPolicyKpi.Value);`。
- [ ] 运行新Gateway/Integration组确认RED。
- [ ] 从独立执行上下文安全复制决策到遥测；SDK/显式导出与采集查询白名单同时适配。CSV增加安全策略列，旧列顺序保留；数组长度/字符串值严格限定。策略计数每个阶段仅一次；熔断状态转移事件按源PolicyId记，不能将版本放入指标维度。查询权限与环境选择复用TrustedObservationScope。
- [ ] 重跑新组及现有GatewayTelemetryTests、ObservationLogsTests/TraceTests/MetricsTests；真实三源验证留到Task 12，不以RecordingSink替代源证据。
- [ ] 保存检查点；记录P17组件覆盖。

## Task 11：策略中心、动态编辑器、绑定与发布Review

**Files:** Create `console/src/api/policies.ts`、`console/src/policies/policy-state.mjs`及声明文件、`PolicyForm.tsx`、`PolicyReferences.tsx`、`RoutePolicyEditor.tsx`、`console/src/pages/Policies.tsx`、`PolicyEditor.tsx`、`console/tests/policy-state.test.mjs`。Modify `main.tsx`、`Shell.tsx`、`pages/Routes.tsx`、`Releases.tsx`、`ReleaseDetail.tsx`、`Snapshots.tsx`、`GatewayNodes.tsx`、`GatewayNodeDetail.tsx`、`Coverage.tsx`、`MetricsOverview.tsx`、`ApiMetrics.tsx`、`AccessLogs.tsx`及已有Trace详情组件；扩展对应TS DTO，不改无关视觉tokens。

**Interfaces:** `policyRequest`方法封装Task 2/3端点；`validatePolicyDraft(draft) : string|null`、`policyEditorReducer(state,event) : state`、`initialPolicyDraft(type)`复用规格默认值，保存412仅标记冲突不覆盖输入。PolicyForm只产生draft/字段错误；页面负责Scope、请求和导航。发布创建先POST selection preview取得全集，再提交；旧Draft明确“刷新Review”调用Task 5端点，申请人确认后刷新，不静默改修订。

- [ ] 写 `AllFourDefaultsAndValidation`、`ConflictRetainsDirtyInput`、`ScopeSwitchIgnoresLateResponse`、`CopyDoesNotReuseBindingOrEtag`、`LegacyDraftRefreshIsExplicit`、`DisabledTypesCannotSave`，Node断言412前后draft内容相等、老Scope响应不写新Scope、复制无旧ID/ETag；组件浏览器交互由Task 13验证。
  关键断言：`assert.deepEqual(afterConflict.draft, beforeConflict.draft); assert.equal(afterConflict.conflict, true); assert.equal(initialPolicyDraft('rate_limit').burst, 1500);`。
- [ ] 运行 `./scripts/check-policies.sh console` 确认状态测试RED，记录构建缺类型而非凭证问题。
- [ ] 实现规格第4、5节两页面和绑定弹窗；类型不可变、共享Scope只读、未发布修订/运行影响、引用分页、删除原因、JSON只读预览。“提交发布”按目标环境预填API版本，列出未选影响及状态重置提示；二级页可返回列表。观测展示实际新增值与安全决策；Coverage只在Task 13完整验收后更新16/17完成状态，先纠正告警过期文案。
- [ ] Node状态测试与TypeScript/Vite构建GREEN；重跑原console测试，检查客户端业务校验不能代替服务端。截图/键盘验收尚未通过不得宣称UI完成。
- [ ] 保存检查点；记录P18的自动验证覆盖。

## Task 12：隔离双网关真实验收、升级/回滚与故障恢复

**Files:** Create `scripts/policies/acceptance.mjs`、`scenario.mjs`、`evidence.mjs`、`build-source.mjs`、`tests/runtime/policy-source.test.mjs`、`tests/WebApi.EndToEnd.Tests/TrafficPolicyLoopTests.cs`、`docs/evidence/policies/verification.json`。Modify必要的既有可观测测试部署覆盖配置、TestBackend的受控测试探测（仅测试部署开启）、fixture准备代码。使用已存在随机runtime/observability机制，不把新脚本固定在4192。

**Interfaces:** `buildPolicySource({root,outputDirectory}) : Promise<{sourceDirectory,sourceManifestHash,baseCommit}>`只复制构建需要的源文件，排除秘密/构建缓存/symlink escape，复制后固定逐文件hash，再从该副本构建验收镜像；image另记录真实digest和sourceManifestHash，不把baseCommit当成新源码。`runTrafficPolicyScenario(context) : Promise<PolicyAcceptanceResult>`调用真实管理/审批/API Key/节点接口；context持随机Compose owner、独立目录、端点和私有Secret路径。result只含资源ID、source清单hash、image digest、HTTP统计、节点实例ACK、脱敏三源证据、失败/恢复/回滚事实和限制。证据文件状态只有实际完成才能写complete=true。

- [ ] 写真实E2E断言：两节点SharedRateLimit、CurrentInstanceCapabilityGate、ActualCircuitRecovery、FrozenPolicyPartialRelease、TwoPointZeroAndTwoPointOneRollback、OldGenerationInFlight、RedisFailureModes、ObservationScopeAndSecretExclusion；开始时完整scenario应因高级行为缺证据RED，测试清理只匹配本项目owner。
  核心代码断言：`Assert.Equal(2, successfulCurrentInstanceAcks.Count); Assert.Equal(HttpStatusCode.OK, finalTraffic.StatusCode); Assert.Equal(originalPayload, rollbackPayload);`；源码测试 `assert.equal(snapshotFileHash, actualCompiledSourceHash)`，新增未提交文件必须出现在清单，秘密文件不能出现。E2E类标记 `[Trait("Scenario", "Policies")]`，runner仅运行 `--filter Scenario=Policies`。
- [ ] `./scripts/check-policies.sh e2e`运行；隔离镜像从包含本批实际源文件的脱敏构建快照生成，保存manifest，不从旧HEAD单独构建后声称新代码已运行。
- [ ] 经正常接口创建专用组织/项目/生产演示环境与两独立审核人、应用与凭证；限流分别压到429，验证两节点数学额度；只在隔离上游制造真实错误、观察节点独立打开/半开/恢复；测试RedisReject和Allow、旧节点能力、未知快照拒绝、新旧快照回滚及共享策略部分发布。三源查询实际找到对应事件和根Trace，时窗覆盖不足如实Partial；验收采样设置仅限隔离项目，长期默认保持。
- [ ] 完整E2E及 `./scripts/check-policies.sh domain`、`integration`、`gateway`、`console`回归通过。复核当前两实例ACK、真实最终HTTP200、实例替换无旧ACK冒用、原数据/旧ZIP hash保全；清理后无测试项目容器/卷、秘密未入证据。失败场景证据保留且解释，不能删失败记录伪造首轮全通过。
- [ ] 保存检查点；将P01–P17、P19事实逐项映射到verification，任何缺证据项保持未完成。

## Task 13：1440px设计QA、独立审查与最终交付

**Files:** Create `docs/deployment/traffic-policies-runbook.md`、`docs/traffic-policy-data-dictionary.md`、`docs/evidence/policies/ui/qa.json`及截图、`scripts/verify-policies-delivery.py`、`scripts/package-policies.py`。Modify `docs/console-coverage.md`、`console/src/coverage.json`、`README.md`、`docs/acceptance.md`；只更新本批状态，不重写原交付证据。

**Interfaces:** `verify-policies-delivery.py`验证矩阵complete、公开文件hash、PNG真实尺寸/编码、业务证据交叉ID和实际源码清单对应；`package-policies.py --output <fresh-path>`拒绝已存在输出，脱敏过滤 `.runtime/.secrets/bin/obj/node_modules`，外部manifest保存ZIP hash/原文件hash；默认新名 `WebAPI_Enterprise_流量策略源码及验收_20261005.zip`，不覆盖旧包。

- [ ] 在 `./scripts/check-policies.sh browser`隔离环境用真实账号检查1440px策略列表/编辑/引用、路由绑定、发布Review/旧Draft刷新、节点能力及观测决策；逐项覆盖空/加载/错误/412/只读/跨Scope、长名称/JSON、键盘和label，保存实际像素截图，不拼接模拟成功状态。写qa.json真实尺寸/编码/hash和缺陷列表。
  交付校验断言：`assert screenshot_width == 1440; assert manifest_hash == sha256(actual_packaged_source_manifest); assert archive_path != existing_delivery_path`。这些断言不能代替逐页视觉检查。
- [ ] 按已保留Native方式，完成后做一次独立全变更审查，重点检查Review Focus 1–5、权限查询、Lua时间/超时、epoch、兼容快照和secret证据。若有缺陷，修复对应任务并仅重跑受影响验证和必要回归；不把独立审查意见直接当事实，先核对代码。
- [ ] 编写中文运行手册/字典，明确可配置默认值、RedisAllow风险、状态重置、升级顺序和瞬时计数不等于持久配额。现有长期runtime构建入口必须使用明确不可变Git revision；本计划不自动commit，也不绕过其sourceRevision校验。先把完整可运行源码及证据交付供审阅，未得到源码提交授权时不替换4192镜像，明确“新功能在隔离验收环境，4192仍为旧版本”，保留可启动的独立评审入口。若之后用户授权固定源码并升级，再走原owner锁/构建入口执行，记录影响和回退步骤；冷备份维护窗口仍按既有操作者流程，不并入本批故障测试。
- [ ] 运行 `./scripts/check-policies.sh verify`和包read-back：CRC、逐文件hash、secret扫描、旧ZIP hash均通过；真实覆盖状态按P18/P19结果更新。产品代码/文档/验收源对应，未运行的生产性能验收仍明确未执行。
- [ ] 保存最终账本与新的源码/证据包，打开交付索引；最终报告给出入口、文件、已验证结果和实质限制。不自动Git提交、推送或合并。

## 规格覆盖与计划自检

|规格章节/验收|任务|
|---|---|
|1–3目标、范围、字段、权限和保护|1、2、3、5；P01–P06、P16|
|4–5页面/接口、认证/超时兼容|2、3、5、11、13；P01、P05、P18|
|6共享限流|7、9、12；P07–P09|
|7熔断状态机|8、9、12；P10–P12|
|8执行顺序和归属|9、12；P04、P12、P15|
|9冻结、部分发布与运行身份|4、5、12；P13、P15、P16|
|10能力、2.0/2.1兼容及升级|4、6、12、13；P14、P15|
|11真实观测与脱敏|10、11、12、13；P17|
|12–14运行边界、验收、交付|1、12、13；P18、P19|

2026-10-05自检：13任务均有明确文件、输入输出、RED/GREEN和检查点；五项Review Focus均有责任任务及断言。查实ReleaseService.CreateAsync已保存完整修订，因此删除了不必要的Draft版本包装方案，完整旧草稿正常提交，只有真实缺失/过期条件需要显式刷新。未要求重建现有数据，未把旧HEAD冒充新源码，未把Native改成逐任务子代理，未自动承诺长期环境维护窗口或生产SLA。接口名、Runtime绑定ID和Policy源ID分别固定，后续不得混用。部署动作、测试、QA、打包均未执行；所有复选框保持未完成。

本计划审阅通过后使用 `superpowers:executing-plans` 实施，并在执行时读取 `superpowers:using-git-worktrees`、TDD及验证技能。不能把计划文件保存当作产品实现完成。
