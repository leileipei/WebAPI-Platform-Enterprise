# WebAPI Enterprise V2 核心闭环实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付真实身份与权限、API配置、两级审批、双网关代理、发布回滚与审计构成的首期核心闭环。

**Architecture:** PostgreSQL保存控制面事实，独立Worker编译不可变Snapshot并通过Redis下发；两个YARP Gateway从Snapshot和独立LKG运行。新建enterprise工程，正式管理端逐页接真实接口，保留prototype作为设计参考。

**Tech Stack:** .NET10 / ASP.NET Core / EF Core、PostgreSQL17、Redis7、YARP2.3.0、React；具体补丁与镜像digest在任务1验证并固定。

**Spec:** `docs/superpowers/specs/2026-10-04-core-loop-design.md`。用户已回复“是，确认”；用户已选择方式1，批准本计划在当前任务内逐项执行。

## Global Constraints

- 工程放在独立 `enterprise/`，保留 `prototype/` 及其用户数据、启动入口和交付证据。
- API Version 属于 API，不能增加环境归属来代替 Route 的 `environment_id`。相同版本可部署多个环境。
- Version 内容首次发布后不可直接修改；新内容建立新版本。参数及 Schema 通过 `api_version_id` 关联。
- 所有关键写入及审计在同一事务；发布通知使用事务 Outbox，不能事务成功却丢失通知。
- 生产申请人不得审批本人申请，同一人不得满足两个审批步骤。
- 部分ACK期间可能存在不同版本，状态保持Publishing并展示逐节点状态。
- Gateway请求路径不访问PostgreSQL、不逐请求调用Control Plane；Redis不是事实主库。
- Secret由密码学安全随机数生成器产生32字节随机值并Base64Url编码；只显示一次，数据库 / Snapshot保存SHA-256摘要；用户密码用ASP.NET安全密码哈希。
- 管理API前缀`/api/v1`，节点API前缀`/internal/v1`；未知身份401、缺权403、不可读单资源404、状态冲突409、并发更新412、结构校验422。
- Compose独立项目 / 网络 / 卷，宿主端口只绑定127.0.0.1。不得停止、重置或复用其他项目服务。
- 首期不提供企业SSO、完整策略、完整YAML / Breaking Change引擎、观测平台、Kubernetes和生产容量验收。
- 中文界面；保留方案1 Shell / Tokens / 密度；正式页面不得出现模拟批准或模拟ACK。

## Review Focus

1. 通知成功但ACK丢失、Worker重启：节点已运行新配置也不能重复创建版本或重复应用写入；任务8、9、10。
2. API Key验证过程中凭证过期：在实际请求准入时检查UTC有效期，不能因缓存验证结果放行；任务5、10。
3. 静态路由与参数路由、大小写不同的Header：路由匹配可预测，参数名规则正确；任务4、5、7。
4. 旧会话在Scope撤销后继续操作：实际提交 / 审批 / ACK / 回滚再次授权，事务前后不能造成跨范围写入；任务2、3、6、8、11。
5. LKG文件写失败或YARP拒绝新配置：保持旧运行generation、不得发送成功ACK，重启不能加载未确认坏配置；任务9、10、15。

## 文件与验证约定

以下文件路径相对`enterprise/`。服务源代码位于`src/<项目名>/`，测试位于`tests/<项目名>/`。公共协议和接口放在各任务指定文件，后续任务不得自行重命名。每个实体及EF配置单独成文件，列表型“Entities/<实体名>.cs”按所列实体逐一建立。

测试项目：`WebApi.Domain.Tests`、`WebApi.Integration.Tests`、`WebApi.Gateway.Tests`、`WebApi.EndToEnd.Tests`。集成测试必须用隔离PostgreSQL / Redis和真实HTTP；不能用EF InMemory或模拟ACK替代对应验收。`scripts/check.sh domain|integration|gateway|e2e`负责容器编译与测试，返回0且失败数0才通过；缺依赖必须明确失败，不能静默跳过。

`ApiFixture`定义于`tests/WebApi.Integration.Tests/Support/ApiFixture.cs`，提供真实HTTP客户端、显式测试身份、可查询隔离DbContext及清理本测试数据库能力。`GatewayFixture`定义于`tests/WebApi.Gateway.Tests/Support/GatewayFixture.cs`，启动真实网关与TestBackend并可观测当前generation；所有故障注入仅在测试构建 / 测试进程使用。

每项遵循写失败检查 → 记录预期失败 → 实现 → 指定检查通过 → 记录增量。若当前目录没有Git，执行开始时仅初始化enterprise独立仓库；已有仓库则先检查状态并遵循其分支规则。提交仅含本任务文件，不提交Secret、缓存和运行证据中的敏感值；不自动推送未知远端。

## Task 1: M1 数据与可编译基础

**Files:** `WebApi.Enterprise.sln`、`Directory.Build.props`、`Directory.Packages.props`、`global.json`、`NuGet.config`；各服务及测试`.csproj`；`deploy/compose.test.yml`、`deploy/images.lock.json`、`.gitignore`、`scripts/check.sh`；`src/WebApi.Infrastructure/Persistence/WebApiDbContext.cs`、`Persistence/Entities/<实体名>.cs`、`Persistence/Configurations/<实体名>Configuration.cs`、`Persistence/Migrations/InitialCore.cs`；`src/WebApi.Migrator/Program.cs`；`tests/WebApi.Integration.Tests/PersistenceTests.cs`。

**Interfaces:** Produces `WebApiDbContext`，源文档除alert_rules / alert_events / sso_providers / system_settings外32张首期表。补充`route_methods`、`release_targets`、`gateway_acks`、`outbox_messages`、`idempotency_records`；环境desired pointer与deploymentSequence、可写实体revision、版本原文 / 示例、Snapshot原始payload bytea。迁移 / 初始化为独立Migrator服务。

- [ ] 写`FreshDatabaseMigrates`、`DuplicateApiVersionRejected`、`SnapshotRawBytesRoundTrip`：`Assert.Equal(32, sourceCoreTableCount)`；重复(apiId,version)被PG拒绝；保存后payload字节及SHA-256相等。`MigratorRunsTwiceWithoutDataLoss`第二次无新迁移且原记录不变。
- [ ] 运行`./scripts/check.sh integration --filter PersistenceTests`，记录缺迁移 / 约束导致的预期失败；测试脚本基础建立与该任务一起完成，不用测试环境缺失充当业务失败。
- [ ] 验证官方包 / 镜像版本组合；固定精确补丁和digest及NuGet锁文件；建立无隐式种子 / 无固定密码的工程、隔离Compose与迁移。用复合引用约束和领域检查防跨项目引用；`route_methods`以环境 / 方法 / normalized_path实现并发冲突唯一性，随Route同事务更新。
- [ ] 上述检查通过；`dotnet restore --locked-mode`及Release build通过；生成`docs/database-core.md`和幂等迁移脚本，列明新增表及字段。
- [ ] 提交`feat: establish isolated core persistence`，记录迁移和编译证据，不声称登录或网关已实现。

## Task 2: M1 真实会话与授权公共接口

**Files:** `src/WebApi.Contracts/Common/ApiContracts.cs`、`Security/IdentityContracts.cs`；`src/WebApi.Domain/Security/ScopeMatcher.cs`；`src/WebApi.Infrastructure/Security/AuthorizationService.cs`、`AccountService.cs`；`src/WebApi.ControlPlane/Security/SessionEndpoints.cs`、`ProblemDetailsMapping.cs`；`src/WebApi.Migrator/BootstrapAccounts.cs`；`tests/WebApi.Integration.Tests/SessionTests.cs`及`Support/ApiFixture.cs`。

**Interfaces:** `ScopeRef(Guid OrganizationId, Guid? ProjectId, Guid? EnvironmentId)`；`ActorContext(Guid UserId, string TraceId)`仅由认证服务构建；`ResourceRef(string Type, Guid Id, ScopeRef Scope)`由服务端解析；`IAuthorizationService.RequireAsync(ActorContext,string permission,ResourceRef,CancellationToken)`；`PageResult<T>(IReadOnlyList<T> Items,int Total,int Page,int PageSize)`；`CommandResult<T>(T Value,string ETag)`；API问题扩展code / traceId。

- [ ] 写`CookieLoginRequiresRealPassword`、`MissingCsrfBlocksWrite`、`DisabledUserSessionRejected`：无登录401、错误密码401；有Cookie无CSRF写入403；停用后原会话下一请求401，不重定向HTML登录页。
- [ ] 运行`./scripts/check.sh integration --filter SessionTests`，记录业务预期失败。
- [ ] 实现`/auth/csrf`、`/auth/login`、`/auth/logout`、`/auth/me`、HttpOnly会话、同源防伪及结构化错误；数据库按请求确认用户状态。显式初始化从受限Secret文件读取初始密码，禁止默认密码，日志不输出Secret；测试身份由Fixture独立建立。
- [ ] 检查通过；`LoginAuditContainsNoPassword`断言审计保留actor / 时间 / IP / traceId，密码和Cookie不进入Before / After。
- [ ] 提交`feat: add real sessions and authorization primitives`。

## Task 3: M1 组织 / 项目 / 环境与治理

**Files:** `src/WebApi.Contracts/Governance/GovernanceContracts.cs`；`src/WebApi.Infrastructure/Governance/GovernanceService.cs`；`src/WebApi.ControlPlane/Governance/GovernanceEndpoints.cs`；`src/WebApi.Infrastructure/Persistence/AuditedCommandExecutor.cs`；`tests/WebApi.Integration.Tests/GovernanceTests.cs`。

**Interfaces:** `GovernanceService.GetScopeTreeAsync(ActorContext, CancellationToken)`；用户 / 角色 / 权限 / Scope CRUD使用任务2的ETag与授权；`AuditedCommandExecutor.ExecuteAsync<T>(ActorContext,ScopeRef,string action,Func<WebApiDbContext,CancellationToken,Task<T>>,CancellationToken)`将资源写入、Secret脱敏审计纳入同一PG事务，支持已有事务。

- [ ] 写`CrossOrganizationReadIsHidden`、`ReadonlyScopeCannotWrite`、`RevokedScopeBlocksExistingSession`、`StaleETagReturns412`：分别404 / 403 / 403 / 412且PG记录及审计无未经授权变更。`LastActivePlatformAdminProtected`断言最后管理员停用 / 撤权返回409。
- [ ] 运行`./scripts/check.sh integration --filter GovernanceTests`，记录预期失败。
- [ ] 建立规范已有接口与DTO：资源ID / code / name / status / revision；组织级Scope仅扩展本组织项目，项目级Scope仅扩展本项目环境，多条匹配授权取并集后与角色权限取交集；空Scope不给权。首次组织创建用显式平台管理授权，用户角色 / 权限字典按源文档seed，无演示角色。
- [ ] 检查通过；`AuditFailureRollsBackBusinessWrite`注入审计写失败，断言业务记录与audit都未提交；检查项目停用 / 环境有进行中发布不能停用。
- [ ] 提交`feat: enforce scoped governance and transactional audit`；M1验收真实登录和Scope后再进入M2。

## Task 4: M2 API / 版本 / Route与后端工作区

**Files:** `src/WebApi.Contracts/Catalog/CatalogContracts.cs`；`src/WebApi.Domain/Routing/RouteNormalizer.cs`；`src/WebApi.Infrastructure/Catalog/CatalogService.cs`、`Routing/RouteService.cs`、`Routing/ClusterService.cs`；`src/WebApi.ControlPlane/Catalog/CatalogEndpoints.cs`、`Routing/RoutingEndpoints.cs`；`tests/WebApi.Integration.Tests/CatalogTests.cs`。

**Interfaces:** `RouteNormalizer.Normalize(string path)`；`CatalogService.CreateVersionAsync(Guid apiId, CreateVersionRequest,ActorContext,CancellationToken)`；`RouteService.SaveAsync(Guid environmentId, SaveRouteRequest,string? ifMatch,ActorContext,CancellationToken)`返回`CommandResult<RouteDto>`；DTO含id / apiVersionId / environmentId / methods / path / clusterId / priority / timeoutMs / revision。`ClusterDto`含健康配置和Destination集合。`ApiDetailDto`同时返回workingRevision / runningConfigVersion / pendingReleaseId。

- [ ] 写`SameShapeMethodsConflict`：GET `/orders/{id}`与GET `/orders/{code}`冲突409，POST同形可独立存在；并发创建相同GET最终仅一条成功。`StaticRouteHasDeterministicPriority`断言`/orders/search`优于`/orders/{id}`。`PublishedVersionImmutable`修改已发布内容409。
- [ ] 运行`./scripts/check.sh integration --filter CatalogTests`，记录预期失败。
- [ ] 实现API / 分组 / Version / 参数 / Schema / Route / Cluster / Destination CRUD；Version无environment字段。Route工作区可更改，当前Snapshot不改；PG与实际资源归属校验、ETag、有限正超时和HTTP(S)上游地址校验。后端地址不得携带userinfo，网络允许列表配置控制可访问目标，默认只允许验收TestBackend，不能提供任意内网探测能力。
- [ ] 检查通过；`ForeignClusterBindingRejected`、`LastDestinationProtected`断言422 / 409；`EditingWorkingRouteLeavesPublishedSnapshotBytesUnchanged`断言字节完全相同。
- [ ] 提交`feat: persist versioned catalog and working routes`。

## Task 5: M2 JSON导入、应用与凭证

**Files:** `src/WebApi.Contracts/Catalog/OpenApiImportContracts.cs`、`Applications/ApplicationContracts.cs`；`src/WebApi.Infrastructure/Catalog/OpenApiImportService.cs`、`Applications/ApplicationService.cs`、`Security/ApiKeySecret.cs`；`src/WebApi.ControlPlane/Catalog/OpenApiImportEndpoints.cs`、`Applications/ApplicationEndpoints.cs`；`tests/WebApi.Integration.Tests/ImportAndCredentialTests.cs`。

**Interfaces:** `OpenApiImportService.PreviewAsync(ImportPreviewRequest,ActorContext,CancellationToken)`、`CommitAsync(ImportCommitRequest,ActorContext,CancellationToken)`；请求包含源文 / Scope / OperationIDs / 新建或已有DraftVersion目标，提交独立重验并返回导入ID与警告。`ApiKeySecret.Create()`返回一次性secret及hash；`CredentialCreateResponse`仅创建响应含secret，详情DTO永不含secret / hash。授权含applicationId / apiId / environmentId / validFrom / expiresAt。

- [ ] 写`BatchImportIsAtomic`、`PreviewDoesNotAuthorizeCommit`、`HeaderNamesCaseInsensitiveQueryNamesCaseSensitive`：批量后一项非法时零新增，Preview后撤权Commit403；X-Trace不重复而q与Q区分。`SecretOnlyReturnedOnce`断言详情 / 审计 / 数据库无原文Secret。
- [ ] 运行`./scripts/check.sh integration --filter ImportAndCredentialTests`，记录预期失败。
- [ ] 实现OpenAPI3.x JSON对应Operation、Path / Query / Header / Body / 多响应及本地引用，原文与结构一起保存；循环 / 外部引用或首期不支持结构返回警告或422，不能伪称完整支持；YAML / URL抓取在真实服务暂不开放。应用与凭证 / 授权检查跨组织和环境；生成32字节随机secret，SHA-256摘要和恒定时间比较。
- [ ] 检查通过；`CredentialWindowRejectsInvalidRange`断言expiresAt≤validFrom返回422；凭证 / 应用 / 授权更改只更新工作区，实际生效版本通过发布展示；幂等导入在任务6公共实现后追加重试回归。
- [ ] 提交`feat: import real operation drafts and manage application credentials`。

## Task 6: M3 冻结候选、审批与幂等命令

**Files:** `src/WebApi.Contracts/Releases/ReleaseContracts.cs`；`src/WebApi.Domain/Releases/ReleaseStateMachine.cs`；`src/WebApi.Infrastructure/Releases/ReleaseService.cs`、`Commands/IdempotentCommandExecutor.cs`；`src/WebApi.ControlPlane/Releases/ReleaseEndpoints.cs`；`tests/WebApi.Integration.Tests/ApprovalTests.cs`、`IdempotencyTests.cs`。

**Interfaces:** `ReleaseService.CreateAsync(Guid environmentId,CreateReleaseRequest,ActorContext,CancellationToken)`；`SubmitAsync(Guid releaseId,ActorContext,CancellationToken)`；`ApproveAsync(Guid releaseId,string comment,ActorContext,CancellationToken)`；请求包含baseConfigVersion和resourceRevision集合，返回`ReleaseDto`（state / frozenCandidate / approvalSteps / targets / errors）。`IdempotentCommandExecutor.ExecuteAsync<T>(CommandIdentity, ReadOnlyMemory<byte> normalizedRequest, Func<CancellationToken,Task<T>>,CancellationToken)`；`CommandIdentity`含actorId / Scope / operation / key。

- [ ] 写`ApplicantCannotApprove`、`OnePersonCannotFillTwoSteps`、`CandidateDoesNotChangeAfterSubmit`：前两者403，冻结字节及revision不随草稿变动。`TwoConcurrentSameKeysReturnOneCommittedResult`断言仅一份发布 / 审计 / 幂等记录；相同key不同请求409。
- [ ] 运行`./scripts/check.sh integration --filter 'ApprovalTests|IdempotencyTests'`，记录预期失败。
- [ ] 按源权限code配置两级API审批 / 安全审批模板，审批时检查当前数据库权限与顺序、相同人约束；冻结只含已选择版本与关联资源、基准Snapshot和revision。状态更新、audit、幂等结果同事务；发布 / 审批 / 导入等命令接入统一幂等执行器；Actor来自会话，禁止客户端传审批身份。
- [ ] 检查通过；`RoleRevokedImmediatelyBeforeApprovalBlocksWrite`断言403 / 审批步骤未完成；重复拒绝 / 取消不能恢复Ready；为任务5导入追加持久化幂等检查。
- [ ] 提交`feat: freeze release candidates and enforce independent approval`。

## Task 7: M3 Snapshot编译与精确字节协议

**Files:** `src/WebApi.Contracts/Runtime/SnapshotContracts.cs`；`src/WebApi.Domain/Runtime/SnapshotValidator.cs`；`src/WebApi.Infrastructure/Releases/SnapshotCompiler.cs`；`tests/WebApi.Domain.Tests/SnapshotCompilerTests.cs`；`docs/contracts/runtime-snapshot.md`。

**Interfaces:** `RuntimeSnapshot`字段schemaVersion / environmentId / configVersion / generatedAt / routes / clusters / policies / applications；`SnapshotEnvelope`字段releaseId / deploymentSequence / configVersion / payloadHash / sizeBytes。`CompiledSnapshot(ReadOnlyMemory<byte> Payload,string Hash,long Size)`；`SnapshotCompiler.Compile(FrozenReleaseCandidate,RuntimeSnapshot baseline,long targetVersion,DateTimeOffset generatedAt)`；`SnapshotValidator.Validate(RuntimeSnapshot,Guid expectedEnvironment)`。

- [ ] 写`PayloadHashMatchesStoredExactBytes`、`ForeignEnvironmentReferenceRejected`、`NoPlaintextSecretOrManagementFields`：UTF-8字节SHA-256一致、跨环境拒绝、输出无审批或Secret字段。`OnlySelectedVersionReplacesItsRuntimeRoutes`断言其他API运行路由保留、未选择草稿不进入Snapshot。
- [ ] 运行`./scripts/check.sh domain --filter SnapshotCompilerTests`，记录预期失败。
- [ ] 从冻结候选与baseline构建完整环境Snapshot；参数路径规范化、引用及上游 / 超时 / 认证策略校验；移除禁用路由但不删无关运行资源；hash位于Envelope，PG bytea为原字节事实，JSONB仅查询副本；输出schemaVersion固定`2.0`。
- [ ] 检查通过；为所有HTTP方法、静态 / 参数路由优先级、未知策略类型、无Destination、非法Schema提供明确失败；生成协议示例与字段约束文档。
- [ ] 提交`feat: compile immutable environment snapshots`。

## Task 8: M3 发布事务、Outbox与Redis协调

**Files:** `src/WebApi.Infrastructure/Releases/PublishCoordinator.cs`、`Messaging/OutboxDispatcher.cs`、`Runtime/RedisSnapshotStore.cs`；`src/WebApi.Worker/Program.cs`、`Workers/ReleaseBuildWorker.cs`、`Workers/OutboxWorker.cs`；`tests/WebApi.Integration.Tests/PublishTests.cs`。

**Interfaces:** `PublishCoordinator.StartAsync(Guid releaseId,ActorContext,CancellationToken)`返回ReleaseDto；`RedisSnapshotStore.PutAsync(Guid environmentId,SnapshotEnvelope,ReadOnlyMemory<byte>,CancellationToken)`；`GetDesiredAsync(Guid environmentId,CancellationToken)`返回Envelope与原字节；PG环境记录是desired pointer事实，Redis是可重建缓存。Outbox含eventId / environmentId / deploymentSequence / releaseId / targetVersion。

- [ ] 写`OnlyOnePublishPerEnvironment`、`StaleBaselineReturns409`、`DatabaseCommitRedisFailureIsRetried`：并发发布仅一条进入Building，旧基线409；Redis故障后PG / Outbox存在且不重复增加configVersion。
- [ ] 运行`./scripts/check.sh integration --filter PublishTests`，记录预期失败。
- [ ] 使用环境事务锁与revision比较，Ready→Building；API命令和Worker实际持久化 / 下发前均重新校验发起用户有效状态及目标环境 / 候选资源的Publish权限，撤权则终止而不移动pointer。调用任务7编译后同事务保存版本 / 原字节 / frozen targets / pointer / sequence / outbox / audit。要求至少两个已注册有效节点，冻结nodeId与instanceId；缺节点409，不静默降低要求。Redis发布完整字节后再推进通知，Lua比较sequence保证迟到Outbox不回退pointer；控制面停机后的缓存补偿由Worker启动重建。
- [ ] 检查通过；`WorkerCrashAfterRedisWriteReusesSameSnapshot`、`OlderOutboxCannotOverwriteNewDesired`断言版本 / hash保持原结果，旧事件不能覆盖更新sequence；`PublishPermissionRevokedWhileQueuedStopsDispatch`断言pointer不变且无新下发；租约过期可重试，不让已完成记录重复改变事实。
- [ ] 提交`feat: coordinate durable publish with outbox recovery`。

## Task 9: M3 节点身份、心跳与ACK汇总

**Files:** `src/WebApi.Contracts/Gateway/NodeContracts.cs`；`src/WebApi.Infrastructure/Gateway/NodeIdentityService.cs`、`NodeRegistry.cs`、`AckService.cs`；`src/WebApi.ControlPlane/Gateway/InternalNodeEndpoints.cs`；`src/WebApi.Worker/Workers/ReleaseTimeoutWorker.cs`；`tests/WebApi.Integration.Tests/AckTests.cs`。

**Interfaces:** `RegisterNodeRequest(Guid EnvironmentId,string NodeName,Guid InstanceId,string AppVersion)`；`NodeHeartbeat(Guid InstanceId,long ConfigVersion,long DeploymentSequence,string Status)`；`NodeAck(Guid InstanceId,Guid ReleaseId,long ConfigVersion,long DeploymentSequence,string PayloadHash,DateTimeOffset AppliedAt,bool Success,string? ErrorCode)`；`AckService.RecordAsync(Guid nodeId,NodeAck,NodeIdentity,CancellationToken)`；NodeIdentity由专用节点认证得到，环境从注册绑定，不接受任意替换。

- [ ] 写`OneOfTwoAcksKeepsPublishing`、`SecondValidAckCompletesOnce`、`WrongInstanceHashOrSequenceRejected`：状态分别Publishing / Succeeded，重复ACK不重复audit；未知node401或403，非冻结实例 / hash / sequence409。
- [ ] 运行`./scripts/check.sh integration --filter AckTests`，记录预期失败。
- [ ] 节点专用Secret由显式配置读取、存摘要，注册 / 心跳 / desired读取 / ACK均检查节点归属与有效身份；节点身份停用后的旧请求拒绝。ACK只确认所匹配Publishing记录，目标集合不因离线缩水；截止时间配置在Worker数据库设置，过期Failed保留各节点真实状态。
- [ ] 检查通过；`LateAckDoesNotCompleteFailedRelease`、`DuplicateAckAfterWorkerRestartIsHarmless`；正常配置状态更新与API Version不可变标记同事务，版本曾被下发也禁止再次编辑，防部分发布失败后修改已运行内容。
- [ ] 提交`feat: authenticate node lifecycle and validate acknowledgements`。

## Task 10: M3 双网关运行generation、API Key与LKG

**Files:** `src/WebApi.Gateway/Program.cs`、`Configuration/EnterpriseProxyConfigProvider.cs`、`Configuration/RuntimeGenerationStore.cs`、`Configuration/SnapshotActivation.cs`、`Storage/LkgStore.cs`、`Security/ApiKeyMiddleware.cs`、`Workers/ConfigWatcher.cs`、`Workers/HeartbeatWorker.cs`；`src/WebApi.TestBackend/Program.cs`；`tests/WebApi.Gateway.Tests/GatewayRuntimeTests.cs`及`Support/GatewayFixture.cs`。

**Interfaces:** `EnterpriseProxyConfigProvider : IProxyConfigProvider`实现`IProxyConfig GetConfig()`；`SnapshotActivation.ApplyAsync(SnapshotEnvelope,ReadOnlyMemory<byte>,CancellationToken)`返回`ActivationResult(bool Applied,string? ErrorCode)`；`LkgStore.ReadAsync/WriteAtomicAsync`保存完整Envelope与payload；`RuntimeGenerationStore.Acquire(long deploymentSequence)`返回可释放GenerationLease。ConfigWatcher同时订阅Redis及定时核对pointer。

- [ ] 写`ActualRequestUsesPublishedDestination`、`RequestUsesOneRoutingAndAuthGeneration`、`BadHashKeepsOldGenerationAndNoAck`：真实TestBackend返回指定backendId；并发切换时请求所选Route / Cluster / Key授权都属于同一generation；坏hash不更新实际版本且无成功ACK。
- [ ] 运行`./scripts/check.sh gateway --filter GatewayRuntimeTests`，记录预期失败。
- [ ] 验证格式、环境、hash与引用；为YARP提供不可变Route / Cluster集合及可用ChangeToken，注册generation后通知更新。请求根据实际匹配Route的generation获取认证数据，不读取独立最新认证指针；旧请求持有旧lease直至完成。等待确认YARP实际应用目标配置后才发送ACK，触发ChangeToken本身不能代表成功。API Key剥离后端转发Header，按实际准入时间检查validFrom / expiresAt及环境 / API授权。
- [ ] 检查通过；`LkgWriteDeniedKeepsOldTraffic`、`YarpRejectedConfigDoesNotAck`、`CredentialExpiresAtAdmission`、`ControlPlaneAndRedisDownStillProxyFromLkg`、`NoLkgNoRemoteNotReady`。LKG以同目录临时文件、flush、原子替换与前一有效副本实现，激活失败恢复有效LKG；取消或崩溃窗口重启通过sequence与已校验文件协调。
- [ ] 提交`feat: activate real gateway generations and recover from lkg`；两节点各自LKG卷、真实HTTP及认证验证完成后才算M3通过。

## Task 11: M4 回滚、超时恢复与运行事实读取

**Files:** `src/WebApi.Infrastructure/Releases/RollbackService.cs`、`ReleaseRecoveryService.cs`；`src/WebApi.ControlPlane/Releases/RecoveryEndpoints.cs`、`Gateway/GatewayReadEndpoints.cs`；`tests/WebApi.Integration.Tests/RollbackTests.cs`。

**Interfaces:** `RollbackService.CreateAsync(Guid originalReleaseId,long targetConfigVersion,ActorContext,CancellationToken)`；`ReleaseRecoveryService.RetryAsync(Guid failedReleaseId,ActorContext,CancellationToken)`；回滚仍经任务6审批及任务8下发，不能直接改Redis。重试建立关联协调记录与新sequence，保留失败记录，不把旧延迟ACK算作新记录ACK；已过时目标409。

- [ ] 写`RollbackReusesExactHistoricalPayloadWithHigherSequence`、`RollbackRequiresIndependentApproval`、`DuplicateRollbackIsIdempotent`：原payload字节 / hash一致但sequence更高；无审批不能发布；同key只有一个回滚记录。
- [ ] 运行`./scripts/check.sh integration --filter RollbackTests`，记录预期失败。
- [ ] 实现回滚记录与原记录关联、历史版本读取权限、全目标确认后原记录RolledBack；重试在环境锁内复核当前desired目标与权限。运行详情返回逐节点版本 / sequence / ACK / 离线情况，失败不能显示“已自动恢复”。
- [ ] 检查通过；`OldNormalPublishEventCannotUndoRollback`、`RevokedScopeBlocksRollback`、`ConcurrentRetryAndNewPublishCannotOverwriteDesired`；查询与Snapshot下载不得泄漏不可读资源或凭证校验材料。
- [ ] 提交`feat: recover failed deployments and audit independent rollback`。

## Task 12: M4 真实管理Shell与治理页面

**Files:** `console/package.json`、`console/src/api/client.ts`、`api/types.ts`、`auth/SessionProvider.tsx`、`Shell.tsx`、`pages/Login.tsx`、`pages/Governance.tsx`、`styles/tokens.css`；`console/tests/session.spec.ts`、`governance.spec.ts`。

**Interfaces:** `apiRequest<T>(path:string,options:RequestOptions):Promise<T>`统一Cookie / CSRF / ETag / traceId / 401 / 412；`useSession()`返回服务端身份、可访问Scope及权限；禁止由客户端角色下拉生成权限。登录和治理页调用任务2、3接口。测试由CUA浏览器在隔离测试环境验证；自动E2E的运行方式须符合当前环境浏览器使用限制。

- [ ] 写真实HTTP集成断言并记录浏览器旧模拟能力与目标差异：登录失败显示错误；只读用户无法新建且直达写接口403；管理员修改Scope后该用户会话下次请求受限；412保留输入与重载选项。
- [ ] 运行`./scripts/check.sh integration --filter 'SessionTests|GovernanceTests'`作为真实API前置检查；浏览器记录尚未接入的失败路径，不能把API既有PASS当作UI红测。
- [ ] 复制已批准的视觉Tokens和组件到新console；实现组织 / 项目 / 环境、用户 / 角色 / 权限 / Scope / 审计页面，统一加载 / 错误 / 空状态与未保存保护；刷新会话真实恢复，退出真实失效。
- [ ] console构建通过；浏览器独立账号验证上述路径，1440×1024无页面级水平溢出和控制台error，截图进入`docs/evidence/m4-console/`并脱敏。
- [ ] 提交`feat: connect real management shell and governance pages`。

## Task 13: M4 API / 后端 / 应用页面真实接入

**Files:** `console/src/pages/ApiCatalog.tsx`、`ApiWizard.tsx`、`ApiDetail.tsx`、`OpenApiImport.tsx`、`SchemaEditor.tsx`、`Routes.tsx`、`Clusters.tsx`、`Applications.tsx`、`Credentials.tsx`、`ApplicationPermissions.tsx`；`console/src/api/catalog.ts`、`applications.ts`；`docs/console-coverage.md`。

**Interfaces:** 页面使用任务4、5 DTO，保留apiVersionId和实际environmentId，不从名称猜关联。Wizard六步Review提交调用任务6，详情明确工作区 / 节点运行版本 / pendingRelease。

- [ ] 定义浏览器失败检查：保存并重登仍保留数据库内容；非法导入整批失败保留原文；已发布版本不可编辑；生产Route保存后真实请求仍是旧Snapshot；Secret弹窗关闭后无法回显。
- [ ] 运行任务4、5集成检查保证API可用；在未实现console页面记录对应预期失败。
- [ ] 接入目录、版本、参数 / Schema、六步向导、JSON导入、Route、Cluster / Destination、应用、凭证、授权；保留现有字段与权限提示，新增版本与路由环境按正式数据模型展示；授权有效期必须真实保存。
- [ ] console构建与浏览器检查通过；原40页manifest映射到已接入 / 后续两种状态，后续页面不显示模拟业务成功。基础差异展示运行 / 候选资源变化，不冒充完整Breaking Change引擎。
- [ ] 提交`feat: connect real catalog and application workflows`。

## Task 14: M4 发布 / 审批 / 网关页面真实接入

**Files:** `console/src/pages/Releases.tsx`、`ReleaseDetail.tsx`、`Approvals.tsx`、`Snapshots.tsx`、`GatewayNodes.tsx`、`GatewayNodeDetail.tsx`；`console/src/api/releases.ts`、`gateway.ts`；`docs/evidence/m4-console/release-flow.json`。

**Interfaces:** 任务6、8、9、11的ReleaseDto与NodeAck读模型。页面定时查询真实进度，不能本地递增ACK计数；明确成功 / 部分节点应用 / 超时失败，下载不含Secret材料。

- [ ] 定义浏览器失败检查：申请人自批被拒绝；两个不同审批账号顺序批准；两个真实节点确认后Succeeded；一个节点停机时保留Publishing / 超时Failed；回滚产生独立审批记录。
- [ ] 运行`./scripts/check.sh integration --filter 'ApprovalTests|AckTests|RollbackTests'`并记录UI尚未完成的失败路径。
- [ ] 接入发布中心 / 详情 / 审批 / Snapshot / 节点列表与详情；使用独立Cookie会话完成不同账号的浏览器验收；API不可达时保留最后读取值但显示更新时间及数据不可用提示，不能显示实时成功。
- [ ] console构建、真实审批 / 两节点 / 回滚浏览器检查通过，保存脱敏证据与运行版本；无“模拟ACK”“模拟批准”入口。
- [ ] 提交`feat: connect deployment approval and node status ui`。

## Task 15: 核心闭环E2E、故障恢复与交付

**Files:** `tests/WebApi.EndToEnd.Tests/CoreLoopTests.cs`、`FailureRecoveryTests.cs`；`scripts/e2e.sh`、`scripts/package.sh`；`docs/acceptance.md`、`docs/operations.md`、`docs/evidence/core-loop/verification.json`；根`README.md`。

**Interfaces:** E2E启动专用Compose、隔离PG / Redis / ControlPlane / Worker / 两Gateway / TestBackend / console。TestBackend响应backendId、method、path及TraceId，不回显认证Header；E2E通过实际HTTP、PG和节点读模型记录一致性。

- [ ] 写`RealApprovedReleaseChangesBothGatewayResponses`：版本1指向backend-A，版本2审批发布后两个网关返回backend-B；部分确认状态不得Succeeded。`RollbackRestoresAWithHigherSequence`断言恢复A且sequence更高；`OldRequestCompletesDuringSwap`断言已进入A的请求完成A、后续新请求B，认证generation相符。
- [ ] 运行`./scripts/check.sh e2e`，记录尚未满足闭环的预期失败，不用health200代替响应断言。
- [ ] 补齐跨服务问题；故障注入覆盖通知丢失、ControlPlane / Redis停机、Gateway重启、坏hash / 环境、LKG不可写、过期Key、跨API拒绝、ACK丢失重试、并发发布 / 回滚；只操作本测试项目容器，保留真实错误状态。
- [ ] 全部领域 / PG / 网关 / E2E检查通过；ARM64实际验证，AMD64构建及运行验证单独记录，未取得目标环境时明确待验收。中文交付含源码 / migrations / 镜像版本清单 / Compose / 配置说明 / 备份恢复 / 日志脱敏 / 操作记录；禁止打包测试Secret和数据库卷。验收写明未实现后续能力及无5kRPS生产性能承诺。
- [ ] 完整代码复核与必要修正、重新运行受影响检查；提交`feat: deliver verified core api management loop`，交付清单逐项对应证据，不把准备工作标为生产验收。

## 计划自查与执行选择

设计§1–4对应任务1及12–15；§5对应1、3–5；§6对应2、3、5、6、10；§7对应2–6、9、11；§8对应6–11；§9对应7–10；§10对应每个阶段验收与15；§11对应12–14；§12外部环境信息通过配置适配，不在未提供时假定已验收；§13依赖兼容性由1实际验证。

推荐Native方式：我在当前任务逐项实现，每个阶段完成真实验证，再进行一次独立完整代码复核。接口、事务和运行generation依赖较强，连续实现可减少交接成本。也可选择Subagent-driven，每个任务分别实现与复核，独立检查更频繁、上下文成本更高。

执行方式须由用户选择；本次只写计划，尚未创建产品工程 / 安装新依赖 / 执行迁移。设计基线已确认，不再重复询问产品方向。实施时遇到真实环境缺失，先完成可独立验证部分，再集中报告阻塞条件。

## 技术参考

[YARP官方配置Provider文档](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/config-providers?view=aspnetcore-10.0)说明配置不可变、重载与新请求之间的关系；任务10仍必须验证真实激活和认证generation的一致性，不能仅凭文档或ChangeToken信号认定已应用。
