# Gateway 索引与管理列表查询优化 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. 沿用用户选择：当前会话实施、测试，最后由新审阅者审查整批变更。

**Goal:** 完成第 2 包，以固定提交、固定数据及隔离环境证明 Gateway 查找和管理列表查询优化，同时保持权限、字段、分页和发布运行语义。

**Architecture:** Gateway 从每代冻结 Snapshot 构建不可变索引，通过原有租约使用该代数据。管理端采用可组合授权查询、数据库分页及页内批量关联；历史引用采用先授权、再按大小批处理的精确聚合。两部分分别测试，由同一个性能夹具与最终回归验收，任一部分缺证据均不关闭工作包。

**Tech Stack:** .NET 10、EF Core/Npgsql、PostgreSQL、Redis、YARP 2.3、xUnit、Node 内置模块、Docker Compose；沿用现有锁文件和镜像 digest。

**Spec:** `docs/superpowers/specs/2026-10-09-gateway-query-performance-design.md`，2026-10-09 用户已确认。

## Global Constraints

- 固定基线 `40759aac50f1a6281223b0ec0d45d58e6fb44cb5`；产品、测量夹具及数据分别记录不可变来源和 SHA256。
- 保留主工作区旧改动；执行时创建独立工作树。本计划及设计草稿不自动提交；Git 只按本包明确授权逐文件暂存，禁止 `git add .`、reset、stash 或覆盖旧文件。
- 不向 4192 或既有数据库施压、迁移、部署；不改既有账号。随机 owner、随机回环端口、专属数据库/volume；禁止全局 prune、FLUSHDB、清理其他项目。
- 保留 Snapshot 2.0/2.1/2.2 原字节、哈希、LKG/ACK/回滚协议、JWT/ApiKey 校验及 Header 信任边界；不缓存认证结果、不更换加密算法。
- 分页页号 1–1,000,000、页大小 1–100，默认 1/50；现有钳制而非新增 422。权限过滤先于 Count/Skip/Take，现有排序主键保留并补 ID 次排序。
- 不新增平台管理员读全部捷径；仅环境 Scope 不授予父项目权限；组织共享策略遵循 PolicyAccess；后续请求看见撤权。
- DTO、详情、写权限及审批规则不变；完整树不改分页接口。策略引用的读可见性不能缩小隐藏资源的写保护。
- SQL 报告包含总数和权限/数据分类；普通列表预算按设计第 5 节，策略引用按批次另报；禁止用分类转移掩盖数据查询。
- 双 Gateway 各 2 CPU/2 GiB，CP 2 CPU/2 GiB，PG 2 CPU/3 GiB，Redis 0.5 CPU/256 MiB，后端 1 CPU/256 MiB，客户端 2 CPU/1 GiB；响应固定 1 KiB。测量时本任务不构建或运行回归。
- 证据不得包含密码、API Key、Bearer、数据库连接密码、私钥或备份。秘密在 owner 私有目录，目录 0700/文件 0600，日志不打印 SQL 参数。
- CORS/IP/Transform、首次改密、Credential 统计、CI/签名、新 API 向导和 4192 升级均不属于本计划。

## Review Focus

1. 同名 JWT claim 出现在不同认证策略、撤销/未来生效凭证与多授权窗口：索引不能串用策略或缓存旧授权；Task 2/3。
2. 仅环境授权、组织限定角色、停用祖先、组织共享策略及下一请求撤权：分页不能泄漏资源或错误统计 Total；Task 4/6。
3. 同名排序、极大页号、最近 10,000 事件截断：稳定分页无重复，边界仍沿用原规则；Task 5/7。
4. 隐藏受保护引用、损坏可见快照、单个合法对象超过 8 MiB：读批处理不放松写保护，不截断 Total，不额外拒绝合法对象；Task 10。
5. 新代构建失败、旧请求仍占租约、负载客户端饱和或测量中断：运行代保留，内存计入双代，未完成矩阵不能冒充完成；Task 1/3/12。

## 文件职责与任务边界

| 任务 | 新建/修改文件 | 职责与测试 |
|---|---|---|
| 1 | 新建 `tools/WebApi.PerformanceHost/{WebApi.PerformanceHost.csproj,Program.cs,PerformanceDataset.cs,RequestSqlProbe.cs,GenerationProbe.cs}`；`scripts/performance/{owner.mjs,matrix.mjs,report.mjs}`；`scripts/check-gateway-query-performance.sh`；`deploy/compose.performance.yml`；修改 `scripts/check-contracts.sh` 的测试源码复制清单和 Integration 测试 csproj | 仅夹具：真实宿主、有效种子、请求 SQL/GC 观测、负载与 owner 清理；新建 `scripts/performance/performance.test.mjs`、`tests/WebApi.Integration.Tests/PerformanceProbeTests.cs`。不修改产品以开启探针 |
| 2–3 | 新建 `src/WebApi.Gateway/Configuration/RuntimeLookupIndex.cs`；修改 `RuntimeGenerationStore.cs`、`src/WebApi.Gateway/Security/ApiKeyMiddleware.cs` | 同代索引及请求使用；新建 `tests/WebApi.Gateway.Tests/RuntimeLookupIndexTests.cs`，补充现有 GatewayRuntime/JwtAuthorizationPipeline 测试 |
| 4 | 新建 `src/WebApi.Infrastructure/Security/CollectionAuthorizationQuery.cs`；修改 `src/WebApi.ControlPlane/ControlPlaneApp.cs` 注册 | 列表可组合 SQL 授权；新建 `tests/WebApi.Integration.Tests/CollectionAuthorizationQueryTests.cs` |
| 5 | 修改 `src/WebApi.Infrastructure/{Catalog/CatalogService.cs,Routing/ClusterService.cs,Routing/RouteService.cs}`；新建 `Routing/RouteListReader.cs` | API/Cluster/Route SQL 分页及有效策略批量加载；新建 `tests/WebApi.Integration.Tests/CoreListQueryTests.cs` |
| 6 | 修改 `src/WebApi.Infrastructure/{Applications/ApplicationService.cs,Policies/PolicyService.cs}` | 应用与共享策略授权分页；新建 `tests/WebApi.Integration.Tests/ScopedListQueryTests.cs` |
| 7 | 修改 `src/WebApi.Infrastructure/{Governance/GovernanceService.cs,Gateway/GatewayReadService.cs}` | 用户角色、节点、事件、Snapshot 查询；新建 `tests/WebApi.Integration.Tests/GovernanceGatewayQueryTests.cs` |
| 8 | 修改 `src/WebApi.Infrastructure/Releases/{ReleaseService.cs,ApprovalEligibilityService.cs}`；新建 `ReleaseListReader.cs` | 完整发布 DTO 批量读取；新建 `tests/WebApi.Integration.Tests/ReleaseListQueryTests.cs` |
| 9 | 修改 `src/WebApi.Infrastructure/Alerts/{AlertRuleService.cs,AlertEventService.cs}`；新建 `AlertListReader.cs` | 规则/事件批量状态和时间；新建 `tests/WebApi.Integration.Tests/AlertListQueryTests.cs` |
| 10 | 修改 `src/WebApi.Infrastructure/Policies/PolicyReferenceService.cs`；新建 `PolicyReferenceReader.cs` | 可见引用按大小批处理，原写保护保留；新建 `tests/WebApi.Integration.Tests/PolicyReferenceQueryTests.cs` |
| 11 | 仅修改有 EXPLAIN 证据的 `src/WebApi.Infrastructure/Persistence/Configurations/*Configuration.cs`；新增 `Migrations/*_GatewayQueryIndexes.cs`、更新 `WebApiDbContextModelSnapshot.cs` | 增量索引，无业务字段迁移；新建 `tests/WebApi.Integration.Tests/GatewayQueryMigrationTests.cs` |
| 12 | 新建 `docs/evidence/gateway-query-performance/` 的脱敏 manifest、原始矩阵、SQL/计划、对比和回执；更新本计划复选框 | 完整比较、回归、审查和交付，不混入旧证据 |

上述测试使用现有 `tests/WebApi.TestSupport/Support/ApiFixture.cs` 和 `tests/WebApi.Gateway.Tests/Support/GatewayFixture.cs`。Integration 测试 csproj 引用 PerformanceHost，复用其中公开 RequestSqlProbe 和种子；不重复链接同名探针类型。check-contracts.sh 的 tar 清单补 tools，不改变测试资源/密钥/清理规则。宿主不加入产品发布产物；独立 csproj 引用现有 CP/Gateway，通过其 `Build(string[], Action<WebApplicationBuilder>?)` 扩展点注入夹具服务，不增加 NuGet 包。生成新工具 `packages.lock.json`、更新 Integration 的 `packages.lock.json`，保留所有既有包版本，再验证 locked restore。Task5/8/9/10 新 reader 均在 ControlPlaneApp.cs 注册 scoped 服务，该共享文件按任务精确 diff 暂存。

## 执行与提交规则

执行前读取 executing-plans、using-git-worktrees 与 test-driven-development；检查最新 HEAD/本任务文件是否被其他会话修改，以原始状态清单保护旧文件。基线始终为固定提交，不因 main 后续更新而漂移。

每任务红测、最小实现、绿测；未定义符号造成的编译错误不能作为行为红测证据，先用最小签名存根让测试运行，再观察明确断言失败。新增测试只提供有效业务数据，权限与状态码有明确期望，不能只比较两个同样错误的实现。

每任务最后的“提交”均是**本包授权后的操作**：先保存精确 diff/测试证据、确认暂存清单，再逐文件 `git add -- <本任务文件>` 和本任务给出的 commit message。未获得授权时只记录候选清单与完成的本地验证，不自行提交。不可变性能测量需要已审阅的夹具提交 H 和产品提交 C；届时提交授权作为准备完成后的具体最终步骤，不用脏目录代替。推送、合并、4192 部署不由本计划审阅自动授权。

所有 `check-contracts.sh` 命令在隔离工作树根目录执行；脚本自行创建和销毁 owner 资源。任务内可用 `--filter FullyQualifiedName~类名` 聚焦；最终执行完整集合。基线测量前完成该版构建，结果失败保留原记录，诊断重测独立编号。

### Task 1：可重现性能夹具、SQL 探针与原版本基线

**Files:** 文件表第 1 行，必要的测试 csproj。产品源码不变。

**Interfaces:**
- `PerformanceDataset.CreateAsync(WebApiDbContext db, DatasetSpec spec, string privateDirectory, CancellationToken ct) -> Task<DatasetManifest>`；`DatasetSpec(int Scale, int Seed, DateTimeOffset WindowStart, DateTimeOffset WindowEnd)`，Scale 仅 100/1000/10000；`DatasetManifest(int Scale,int Seed,IReadOnlyDictionary<string,long> Counts,string BusinessFingerprint,IReadOnlyDictionary<string,string> SnapshotHashes,bool Validated)`，秘密单独私有文件。
- `RequestSqlProbe.Begin(string requestId) -> IDisposable`；`Read(string requestId) -> SqlRequestStats`；`SqlRequestStats(int Total, int Authorization, int Data, int Unclassified)`。探针继承 DbCommandInterceptor，用 AsyncLocal 关联，统计同步/异步 reader/scalar/nonquery 的每次实际执行，按 SQL 标签及固定归类清单分类，未识别记 Unclassified，永不输出参数。
- 夹具入口 `check-gateway-query-performance.sh validate|baseline|compare|explain|migration|cleanup --baseline <B> --candidate <C> --harness <H> --output <dir>`；validate 可在工作树执行仅自检，其余测量必须验证提交存在、来源干净、H 固定；baseline 不要求 C。
- Node 导出 `validateOwner(manifest, labels) -> boolean`、`buildMatrix() -> Cell[]`、`summarize(samples) -> Summary`；`Cell` 固定含 variant/scale/auth/concurrency/repeat/nodeMode，`Summary` 含 expected/unexpected/timeout/incomplete/rps/p50/p95/p99（时间ms，nearest-rank）；Node 内置 test runner。
- `GenerationProbe` 提供 `MeasureBuild(RuntimeSnapshot snapshot, SnapshotEnvelope envelope, byte[] payload) -> GenerationMeasurement`，`GenerationMeasurement(TimeSpan Elapsed,long AllocatedBytes,long HeapBytes)`；旧代租约峰值采用实际 RuntimeGenerationStore。微查找适配器仅在准备阶段用反射绑定候选索引，基线绑定现有扫描表达式，测量循环不做反射，明确标为微基准。

- [ ] **1. 红测：** `performance.test.mjs` 的 `owner_mismatch_never_deletes`、`interrupted_cell_is_incomplete`、`matrix_has_all_required_cells`、`percentiles_use_complete_window`；`PerformanceProbeTests.CountsConcurrentRequestsAndAllCommandKindsWithoutSecrets` 断言下列片段。种子 S/M/L 必须先通过产品 SnapshotValidator，复用同一份秘密/签名/数据时间窗，覆盖 RS256/ES256 合法配置限制。
  ```js
  assert.equal(validateOwner(ownedManifest, foreignLabels), false);
  assert.equal(interrupted.complete, false);
  assert.equal(summarize([10,20,30,40,50]).p95, 50); // nearest-rank ms
  ```
  ```csharp
  Assert.Equal(4, probe.Read("A").Total); Assert.Equal(2, probe.Read("B").Total);
  Assert.DoesNotContain(secret, publicEvidence); Assert.Equal(10000, large.Routes.Count);
  ```
- [ ] **2. 跑红测：** `node --test scripts/performance/performance.test.mjs`；`./scripts/check-contracts.sh integration --filter FullyQualifiedName~PerformanceProbeTests`，预期上述断言失败，保留输出。
- [ ] **3. 实现接口和可运行宿主：** 复用产品 CP/Gateway Build 的配置回调，SQL 探针通过夹具 `ConfigureDbContext<WebApiDbContext>` 选项注入，产品注册保留；请求关联用夹具 IStartupFilter 插入管线，独立受控接口导出聚合计数/GC，不在产品添加路由。用受限私有 seed 文件供两个版本共用；鉴权使用真实散列和签名。2 个真实 GW 转发到 1 KiB 后端，客户端分别直连各节点并汇总，避免额外代理瓶颈。补齐所有资源配额、锁定 image ID、DB pool/GC/遥测配置及 owner 标签。
- [ ] **4. 跑绿测及小规模自检：** 重跑两条红测命令，再 `./scripts/check-gateway-query-performance.sh validate --output .runtime/performance-validation`；验证创建、正确状态码、计数、失败诊断和仅 owner 清理，公开扫描零秘密。validate 不输出“完整基线完成”。
- [ ] **5. 审阅并固定夹具 H：** 提交范围仅 Task 1 文件，message `test: add isolated gateway query performance harness`，按提交规则取得授权后执行。H 是所有 A/B 的同一夹具来源；从 B/C 的 git archive 与 H 的夹具目录组装独立构建输入，记录双方哈希；禁止把 H 的产品源码叠加到 B。新增夹具的依赖引用使用 B/C 同路径公共接口，两版均 locked restore 成功。
- [ ] **6. 原版本完整基线：** `./scripts/check-gateway-query-performance.sh baseline --baseline 40759aac50f1a6281223b0ec0d45d58e6fb44cb5 --harness "$PERF_HARNESS_COMMIT" --output docs/evidence/gateway-query-performance/baseline`；完成固定矩阵、管理查询规模/页型、微查找及代构建/租约内存，保留原始窗口。所有要求完整且 owner 清理成功后才进入产品优化；最终比较仍按交替 A/B 重跑基线，首次基线用于定位成本，不当替代样本。

### Task 2：从同代冻结对象构建不可变查找索引

**Files:** `RuntimeLookupIndex.cs`、`RuntimeGenerationStore.cs`、`RuntimeLookupIndexTests.cs`。

**Interfaces:** 新类型 `RuntimeCredentialEntry(RuntimeApplication Application, RuntimeCredential Credential)`、`RuntimeAuthenticationEntry(AuthenticationConfiguration? Configuration, RuntimePolicy? Policy)`；`RuntimeLookupIndex.Build(RuntimeSnapshot frozen, IReadOnlyDictionary<Guid,AuthenticationConfiguration> authentication) -> RuntimeLookupIndex`。
索引只读属性：`Routes/Clusters/Applications` 为各 `Guid -> Runtime*`；`Credentials` 为 `string -> RuntimeCredentialEntry`（Ordinal）；`RouteAuthentication` 为 `Guid -> RuntimeAuthenticationEntry`；`JwtApplications` 为 `(Guid PolicyId,string ClaimValue) -> Guid`（claim Ordinal）；`PermissionWindows` 为 `(Guid ApplicationId,Guid ApiId) -> IReadOnlyList<RuntimePermission>`。`RuntimeGeneration.Indexes` 暴露该只读实例；构造签名不变，先 Freeze，所有索引/策略解析使用 Frozen Snapshot。

- [ ] **1. 红测：** `BuildRetainsFrozenObjectIdentityAndAllWindows`、`DuplicateKeysAndMissingReferencesReject`、`JwtMappingNamespacesAreDistinctAndOrdinal`、`RevokedAndFutureCredentialsRemainIndexed`：
  ```csharp
  Assert.Same(g.Snapshot.Applications[0], g.Indexes.Credentials[accessKey].Application);
  Assert.Equal(2, g.Indexes.PermissionWindows[(appId,apiId)].Count);
  Assert.NotEqual(g.Indexes.JwtApplications[(policyA,"same")], g.Indexes.JwtApplications[(policyB,"same")]);
  Assert.False(g.Indexes.JwtApplications.ContainsKey((policyA,"Same")));
  Assert.Throws<InvalidOperationException>(() => BuildDuplicateAccessKey());
  ```
  测试类内部 `BuildDuplicateAccessKey() -> RuntimeLookupIndex` 仅用合法 Snapshot 副本改成重复 AccessKey；重复 route/app ID、缺失 cluster/policy/app 引用同样拒绝，不能字典覆盖。
- [ ] **2. 红测命令：** `./scripts/check-contracts.sh gateway --filter FullyQualifiedName~RuntimeLookupIndexTests`，预期索引内容/拒绝语义断言失败。
- [ ] **3. 实现接口：** 使用 FrozenDictionary 或私有只读字典；复用冻结对象，不复制大型 Config/Hash 字符串。生成所有记录，状态和时间留给每次请求；索引构建不注册任何外部资源，失败不形成半代。不修改 Snapshot DTO/序列化。
- [ ] **4. 绿测命令：** 同上，全部通过；追加 `./scripts/check-contracts.sh domain --filter FullyQualifiedName~Snapshot`，历史格式和验证规则通过。
- [ ] **5. 提交候选：** 仅三文件，message `perf: build immutable lookup indexes per runtime generation`；授权后按提交规则执行。

### Task 3：认证热路径使用索引，验证租约和历史兼容

**Files:** `ApiKeyMiddleware.cs`；现有 `GatewayRuntimeTests.cs`、`JwtAuthorizationPipelineTests.cs`；Task 2 索引测试。

**Interfaces:** 保持 `ApiKeyMiddleware.InvokeAsync(HttpContext ctx, RuntimeGenerationStore store, JwtTokenVerifier verifier) -> Task` 和 `Acquire(long) -> GenerationLease`。只读取 `lease.Generation.Indexes`，JWT 映射使用该 route 的有效认证 Policy.Id；权限按 `(app.Id,route.ApiId)` 获取全窗口，保留 `now >= ValidFrom && (ExpiresAt == null || now < ExpiresAt)`。

- [ ] **1. 红测：** `IndexedAuthPreservesStatusAndTimeBoundaries`，表驱动精确断言：合法 key 200；不存在/错误 secret/大小写差异/空/多段/撤销/未来/到期 key 401；停用 JWT 应用、无目标授权/过期窗口 403；无 mapping 401；任一有效窗口允许。`JwtSameClaimDifferentPoliciesDoNotCrossMap` 断言两个路由只分别使用自己的 app。`HeldOldRequestUsesOldIndexesAndRetiredGenerationCollects` 覆盖下列片段：
  ```csharp
  Assert.Equal(HttpStatusCode.OK, oldHeldResponse.StatusCode);
  Assert.Equal(HttpStatusCode.Unauthorized, nextOldKeyResponse.StatusCode);
  Assert.Equal(expectedPayloadHash, restored.Envelope.PayloadHash);
  Assert.Throws<InvalidOperationException>(() => store.Acquire(retiredSequence));
  ```
  新增行为回归可能在旧实现已绿；性能红测由“请求路径不得再调用全代查找”的微查找成本/实现审查共同定位，不能捏造行为失败。补 2.0/2.1/2.2 加载/回滚原字节、失败构建旧 LKG/ACK 不变、重复序列、双 Dispose、修改输入数组不影响冻结代。
- [ ] **2. 跑修改前回归：** `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~GatewayRuntimeTests|FullyQualifiedName~JwtAuthorizationPipelineTests|FullyQualifiedName~RuntimeLookupIndexTests'`；已有通过作为 oracle，未通过先定位，记录实际红/绿而非统一声称红测。
- [ ] **3. 最小修改 InvokeAsync：** route/cluster/app/credential/JWT mapping/permission-group 直接查 Task 2 字典；ApiKeySecret.Verify、JwtTokenVerifier、telemetry/SourcePolicyId/revision、forwardBearer、原拒绝代码及 Header 删除保持原调用。不读取 Current、不复用跨代认证结果、不把秘密加入标签。
- [ ] **4. 跑绿测：** 同命令加 `JwtTokenVerifierTests`、`GatewayPlatformHeaderBoundaryTests`；有效 JWT 签名/issuer/audience/kid/algorithm/nbf/exp 和三认证模式 Header 回归通过，微查找 S/M/L 不再随无关集合增长。
- [ ] **5. 提交候选：** 精确中间件/测试文件，message `perf: use generation indexes in gateway authentication`；授权后执行。

### Task 4：可组合列表授权 SQL，与现有授权 oracle 一致

**Files:** `CollectionAuthorizationQuery.cs`、`ControlPlaneApp.cs`、`CollectionAuthorizationQueryTests.cs`。

**Interfaces:** 新 `public sealed record AuthorizedScopeRow(Guid OrganizationId, Guid? ProjectId, Guid? EnvironmentId)`（基础设施查询类型，不进入 HTTP DTO）；类 `CollectionAuthorizationQuery(WebApiDbContext db)`，`RequireActiveActorAsync(ActorContext actor,CancellationToken ct) -> Task`；`Scopes(ActorContext actor,string permission) -> IQueryable<AuthorizedScopeRow>`。按真实组织/项目/环境层级组成 SQL，不物化全资源 ID；角色组织限制、ScopeMatcher 的 null 匹配、read/write 和治理类权限例外与 AuthorizationService 一致。项目/组织/环境三层分别返回真实 scope，不给环境 grant 增补父级。请求入口调用 Active 检查维持 401，其余以可组合 EXISTS 使用，同请求不做跨请求缓存。

- [ ] **1. 红测：** `ScopesMatchAuthorizationOracleAndExplicitExpectations` 覆盖 10 组织、5 项目、3 环境及所有角色/Scope 组合；每种 scope 同时调用现有 `CanAsync` 作 oracle，并断言明确有权/无权 ID。`EnvironmentGrantDoesNotReadParent`、`RevocationIsVisibleOnNextRequest`、`OrganizationRoleCannotLeakAcrossOrganization`、`InactiveActorStill401`。
  ```csharp
  Assert.DoesNotContain(visible, s=>s.ProjectId==parentProject && s.EnvironmentId==null);
  Assert.Empty(afterRevocation); Assert.Equal(401, inactiveException.Status);
  Assert.Equal(expectedScopeIds.Order(), actualScopeIds.Order());
  ```
- [ ] **2. 红测命令：** `./scripts/check-contracts.sh integration --filter FullyQualifiedName~CollectionAuthorizationQueryTests`，未授权资源/撤权等断言失败。
- [ ] **3. 实现接口与 scoped DI 注册：** EF 可翻译的相关子查询/UNION（必要时使用参数化 SQL）；Count 与 page 使用同一表达式，禁止客户端 Evaluate 或全表 ToArray 后判权。显式资源入口继续使用原 ScopeResolver/PolicyAccess/AuthorizationService，以维持 401/403/404/409；写端完全沿用原授权。
- [ ] **4. 绿测命令：** 同上，并 `./scripts/check-contracts.sh integration --filter FullyQualifiedName~GovernanceTests`；SQL 检查确为服务端条件，无跨组织、父资源越权或写权限变化。
- [ ] **5. 提交候选：** 三文件，message `perf: add composable collection authorization queries`；授权后执行。

### Task 5：API、Cluster、Route 的数据库分页和页内关联

**Files:** CatalogService、ClusterService、RouteService、RouteListReader、CoreListQueryTests（文件表准确路径）。

**Interfaces:** 原三个 `ListAsync` 参数/DTO 不变；新增 `RouteListReader(WebApiDbContext db)`，`ReadEffectiveAsync(IReadOnlyList<ApiRoute> routes,CancellationToken ct) -> Task<IReadOnlyDictionary<Guid,EffectiveRoutePolicies>>`。批量 join RoutePolicyBinding/Policy，继续用 PolicyBindingRules.Validate，缺省 key/timeout/JWT 显示不变。

- [ ] **1. 红测：** `CoreListsRespectPageAndSqlBudgets`，每类 151 条、页大小 1/50/100，首/中/末/越界页，SQL 数据预算 API≤2/Cluster≤3/Route≤4；Destination 和策略关联都非空。`RouteSqlOrderMatchesMatchOrderForLiteralParameterWildcard` 断言 path-kind/priority 的完整序列；`ClampAndDuplicateSortValuesRemainStable`：
  ```csharp
  Assert.Equal(151, page.Total); Assert.Equal(50, page.Items.Count);
  Assert.Equal((1,100), (clamped.Page,clamped.PageSize));
  Assert.Equal(expectedIds, actualIds); Assert.True(sql.Data<=budget);
  ```
  `(page,size)=(-2,1000)` 钳制 1/100；1,000,001 钳制至 1,000,000；记录40000+偏移算术不溢出，空页不加载关联。HTTP 无权/缺失/停用按当前端点明确状态码断言。
- [ ] **2. 红测命令：** `./scripts/check-contracts.sh integration --filter FullyQualifiedName~CoreListQueryTests`，现有全量/N+1 查询预算失败。
- [ ] **3. 实现三个 ListAsync 及批量 reader：** 现有显式 scope 授权→Count→稳定页→页内关联。Route SQL 排序严格使用规范化路径：`(NormalizedPath.Contains("{*}") ? 2 : NormalizedPath.Contains("{}") ? 1 : 0) * 2000000 - Priority`，然后原 Path 再 ID；验证所有合法路径与 RouteNormalizer 相等，不增加业务字段。所有读取 AsNoTracking；不提前过滤绑定策略改变 Validate 语义。
- [ ] **4. 绿测命令：** 同上；`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~RoutePolicyBindingTests|FullyQualifiedName~PolicyReleaseTests'`；增加无关环境数据后页内容和数据 SQL 数不变。
- [ ] **5. 提交候选：** 本任务五文件，message `perf: page catalog and routing lists in database`；授权后执行。

### Task 6：Application 与组织共享 Policy 的授权分页

**Files:** ApplicationService、PolicyService、ScopedListQueryTests；仅按必要补充 Task 4 查询帮助类。

**Interfaces:** 保持 `ApplicationService.ListAsync(ActorContext,int,int,CancellationToken) -> Task<PageResult<ApplicationDto>>` 和 `PolicyService.ListAsync(PolicyScopeRequest,ActorContext,int,int,string?,string?,bool?,CancellationToken) -> Task<PageResult<PolicyDto>>`。消费 `Scopes(actor,"app.read")`/`Scopes(actor,"policy.read")`，沿用现有 permission code。

- [ ] **1. 红测：** `ApplicationVisibleTotalIsFilteredBeforePaging`、`OrganizationPolicySharesOnlyWithAuthorizedActiveDescendants`、`ProjectPolicyIsNotVisibleWithEnvironmentOnlyGrant`，多个组织、inactive/无功能/无 Scope，分页前过滤；同名+ID 顺序及 type/search/enabled 原过滤不变。组织共享策略依次测试组织 grant、项目 grant、环境 grant、无 grant、404 后代、停用后代。
  ```csharp
  Assert.Equal(visibleCount, result.Total); Assert.DoesNotContain(result.Items,x=>hiddenIds.Contains(x.Id));
  Assert.Contains(sharedPolicyId, visiblePolicyIds); Assert.True(sql.Data<=2);
  ```
- [ ] **2. 红测命令：** `./scripts/check-contracts.sh integration --filter FullyQualifiedName~ScopedListQueryTests`，查询数量/分页授权预算失败。
- [ ] **3. 修改原 ListAsync：** Application 按自身 org/project 的精确可见 scope EXISTS；Policy 项目资源匹配精确项目 scope，组织共享资源匹配自己或任一当前真实有权后代，保留原请求范围的 Resolve/CanRead 与筛选校验顺序。Count 与页共用可见查询，Name/Code 与 ID 稳定排序；PolicyAccess 的写授权、Copy/Update/Delete 不改。
- [ ] **4. 绿测命令：** 同上；`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~PolicyManagementTests|FullyQualifiedName~PolicyDeletionTests'`；无权隐资源 Total 不增加，撤权下一请求立即生效。
- [ ] **5. 提交候选：** 精确列表/测试/必要帮助类，message `perf: filter application and shared policy lists before paging`；授权后执行。

### Task 7：User/Role、Gateway Node/Event/Snapshot 列表

**Files:** GovernanceService、GatewayReadService、GovernanceGatewayQueryTests。

**Interfaces:** 原 UsersAsync/NodesAsync/EventsAsync/SnapshotsAsync 签名及 DTO 不变；角色按页内 UserId 批量查询。Snapshot 页只投影 ConfigVersion/Hash/SizeBytes/最新 deployment release 元数据，绝不选择 PayloadBytes。

- [ ] **1. 红测：** `UserRolesLoadedOnlyForPage`（≤3 数据 SQL）、`GatewayNodesRetainEnvironmentStateAndEtag`（≤2）、`EventsCapTotalAt10000AndPageWithinCap`（10003 条种子，总数10000，末3条不能出现，≤2）、`SnapshotListDoesNotLoadPayloadAndUsesLatestSequence`（≤3）；极大页号、空页、同名用户/节点有明确 ID 期望。
  ```csharp
  Assert.Equal(10000, events.Total); Assert.Equal(expectedLastPageIds, events.Items.Select(x=>x.Id));
  Assert.DoesNotContain("payload_bytes", selectedColumns); Assert.Equal(latestSequence, snapshot.LatestDeploymentSequence);
  ```
- [ ] **2. 红测命令：** `./scripts/check-contracts.sh integration --filter FullyQualifiedName~GovernanceGatewayQueryTests`；N+1/全量或 Payload 读取断言定位失败。
- [ ] **3. 实现现有方法：** 节点环境元数据归入实际 scope 读取并复用，仍报告总 SQL；事件在受限最新10000的子查询上 Count/page（不是全表 Count）。Snapshot 可采用 Count+页左关联大小/相关 latest release，或页后两条 bulk 查询，总数据≤3。Users 保留原 Count/page 加一条页内角色查询，不分页树/详情。
- [ ] **4. 绿测命令：** 同上和 GovernanceTests；大规模无关数据不增加查询数，节点启停权限/ETag、历史管理 Snapshot 凭证脱敏回归通过。
- [ ] **5. 提交候选：** 三文件，message `perf: batch governance and gateway management lists`；授权后执行。

### Task 8：完整 Release DTO 批量读取和审批资格

**Files:** ReleaseService、ApprovalEligibilityService、ReleaseListReader、ReleaseListQueryTests。

**Interfaces:** `ReleaseListReader(WebApiDbContext db,CollectionAuthorizationQuery visibility,ApprovalEligibilityService eligibility)`；`ReadAsync(IReadOnlyList<ReleaseRecord> releases,ActorContext actor,CancellationToken ct) -> Task<IReadOnlyList<ReleaseDto>>`；`ApprovalEligibilityService.EvaluateBatchAsync(IReadOnlyList<ReleaseRecord> releases,ScopeRef scope,ActorContext actor,CancellationToken ct) -> Task<IReadOnlyDictionary<Guid,ApprovalEligibility>>`，同页为同环境。批量获取任务/target/ACK、当前步骤/已占席位/actionable IDs，复用现有 QueryActionableReleaseIds；写入 RequireTaskAsync 不变。读取 view 用 Task 4 的三个 api.* 可见 scope 与页内实际 ApiId 关联脱敏，不逐风险引用 auth.CanAsync。

- [ ] **1. 红测：** `ReleaseListMatchesDetailFieldsWithConstantSql` 对151条不同冻结规则/候选、两个网关 ACK、风险批注、rollback/recovery构造有效种子；每页1/50/100数据SQL≤12，字段逐项同现有详情；`EligibilityPreservesErrorPrecedenceAndIndependence`：无 approval.act、无write Scope、申请人、占过席位、错误角色、非WaitingApproval 的 ReasonCode/CurrentStepOrder 相同，停用用户401；`RiskCommentRequiresAllThreePermissions`：
  ```csharp
  Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(detail),JsonSerializer.SerializeToElement(listed)));
  Assert.Null(hiddenRisk.Comment); Assert.Equal("self_approval", applicantEligibility.ReasonCode);
  Assert.True(sql.Data<=12); Assert.Equal(page1Sql.Data,page100Sql.Data);
  ```
  ApprovalEligibility 保持 CanAct/CurrentStepOrder/ReasonCode；禁止遗漏 task/ACK/candidate/hash/failure/recovery 或改成 summary DTO。
- [ ] **2. 红测命令：** `./scripts/check-contracts.sh integration --filter FullyQualifiedName~ReleaseListQueryTests`，线性SQL预算失败，旧详情作为兼容 oracle。
- [ ] **3. 实现批量 reader、EvaluateBatchAsync 与 ListAsync SQL page：** 单次当前环境显式授权，组装字典而非逐项DB；审批返回原因顺序沿用 EvaluateAsync。纯读列表 ReadCommitted 保持普通可见性，写命令治理锁与幂等流程不改；每次新请求重新授权。可共享纯 DTO 投影，不能让详情或写命令误用过期页缓存。
- [ ] **4. 绿测命令：** 同上；`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~ApprovalInboxTests|FullyQualifiedName~PolicyReleaseTests'`，收件箱 RepeatableRead/10s 边界继续保留，审批/发布/回滚完整行为通过。
- [ ] **5. 提交候选：** 四文件及明确必要注册，message `perf: batch full release list projections and eligibility`；授权后执行。

### Task 9：告警规则和事件的状态批量聚合

**Files:** AlertRuleService、AlertEventService、AlertListReader、AlertListQueryTests。

**Interfaces:** `AlertListReader(WebApiDbContext db,AlertEvaluationSettings settings)`；`ReadRulesAsync(IReadOnlyList<AlertRule> rules,CancellationToken ct) -> Task<IReadOnlyList<AlertRuleDto>>`；`ReadEventsAsync(IReadOnlyList<AlertEvent> events,CancellationToken ct) -> Task<IReadOnlyList<AlertEventDto>>`。仅列表调用，Detail/Act 继续保留原事务、ChangeTracker Added transition 与通知行为。每请求 DatabaseTimeAsync 一次，预期环境按规则实际 org/project/env 批量解析，不按操作者可见环境截断系统覆盖。

- [ ] **1. 红测：** `RuleStateRequiresEveryExpectedActiveEnvironment`（Disabled/ScopeInactive/Known/Unknown、缺状态、未来slot、过期slot、旧LogicRevision/ResourceKey）、`RetiredTargetStillManageableByFrozenOwnership`、`EventUnknownRedactsValueAndConditionAndRetainsTransitions`、`AlertsSqlDoesNotGrowPerPageItem`。规则数据≤10、事件≤6，1/50/100等预算；规则目标已删除但原范围有权仍可见。
  ```csharp
  Assert.Equal("Unknown", incompleteRule.EvaluationState); Assert.Null(unknownEvent.LastValue);
  Assert.Equal(expectedTransitionIds, eventDto.Transitions.Select(t=>t.Id));
  Assert.Equal(1, databaseTimeCommands); Assert.True(sql.Data<=budget);
  ```
  状态字段为 EvaluationState；未来/缺失slot使未Resolved Known转Unknown，Resolved原语义不改。
- [ ] **2. 红测命令：** `./scripts/check-contracts.sh integration --filter FullyQualifiedName~AlertListQueryTests`；逐规则/事件时间及状态SQL预算失败。
- [ ] **3. 实现：** 保留 ObservationScopeResolver 的原请求校验，再结合 Task4 rule管理授权表达式 Count/page；不得 ValidateReferences 重新拒绝已退休目标。批量按页内 RuleId/LogicRevision/ResourceKey/Environment 查询预期环境与state，记录实际读取行数。事件原severity/source/status 422与环境过滤不变，批量transition/evaluation及页面原有通知读取；当前无通知字段不凭计划新增 DTO 字段或SQL。
- [ ] **4. 绿测命令：** 同上与现有 Alert/Notification 名称过滤回归，`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~Alert|FullyQualifiedName~Notification'`；系统评估/外部通知 worker 与失败重试不修改，范围过滤和时效判断通过。
- [ ] **5. 提交候选：** 四文件及明确必要注册，message `perf: batch alert list state projections`；授权后执行。

### Task 10：策略引用精确聚合，读授权与写保护分离

**Files:** PolicyReferenceService、PolicyReferenceReader、PolicyReferenceQueryTests。

**Interfaces:** `PolicyReferenceReader(WebApiDbContext db,CollectionAuthorizationQuery visibility)`；`ReadVisibleAsync(Guid policyId,ActorContext actor,CancellationToken ct) -> Task<IReadOnlyList<PolicyReferenceDto>>`；内部 `ReferencePayloadDescriptor(Guid Id,string Kind,Guid EnvironmentId,long SizeBytes)`、`PlanBatches(IReadOnlyList<ReferencePayloadDescriptor> sources) -> IReadOnlyList<IReadOnlyList<ReferencePayloadDescriptor>>`。每批最多200 ID、目标8*1024*1024 bytes；单个更大对象独占一批。公开 ListAsync 继续返回原 PageResult，最终完整merge/sort/精确Total；FindProtectedAsync/ProtectedConfigurationsAsync 的全保护来源不消费可见查询。

- [ ] **1. 红测：** `ReferenceBatchesRespectIdsAndBytesAndOversizeSingleton`、`VisibleTotalIncludesAllThreeSourcesAndRuntimeIsDistinct`、`HiddenProtectedReferenceStillBlocksWrite`、`VisibleCorruptSnapshotFailsClosed`；各类授权独立 route.read/release.read/gateway.config.read。可见损坏409；不可读来源不解析/不计Total；写保护面对隐藏损坏仍拒绝，保留409。
  ```csharp
  Assert.All(batches,b=>Assert.InRange(b.Count,1,200));
  Assert.Single(batches.Single(b=>b.Any(x=>x.Id==oversizedId)));
  Assert.Equal(expectedVisibleTotal, page.Total); Assert.Equal(409, corrupt.Status);
  Assert.Equal(409, hiddenProtectedWrite.StatusCode);
  ```
  构造201小对象、多对象越8MiB和一个合法>8MiB对象；对象要能被产品正常解析，不使用无效填充绕过大小校验。排序重复键用ReferenceKind/Env/Name之后增加来源标识的确定性次序，保持原主序。
- [ ] **2. 红测命令：** `./scripts/check-contracts.sh integration --filter FullyQualifiedName~PolicyReferenceQueryTests`；批预算/逐引用SQL失败，保护测试可能原已绿，分别保留状态。
- [ ] **3. 实现 reader：** 先确认policy可读，再按Task4授权环境构建三个来源；先查 descriptor（payload SQL字节长度而非取数组），按Id/size计划批次，再按批读取并释放JSON。desired/applied `(env,version)`去重，缺失/空/损坏可见快照沿用Unavailable，读取2.0/2.1/2.2 source语义。只保留摘要用于最终分页；授权采用请求内SQL，不引入跨请求缓存，普通ReadCommitted语义沿用，不承诺跨请求快照分页。
- [ ] **4. 绿测命令：** 同上与 `./scripts/check-contracts.sh integration --filter FullyQualifiedName~PolicyDeletionTests`；记录来源数、解析byte总量、批峰值、SQL=`固定描述/授权查询+来源批数`，不存在逐reference查询，Total不靠来源提前截页计算。
- [ ] **5. 提交候选：** 三文件及必要注册，message `perf: batch visible policy references without weakening protection`；授权后执行。

### Task 11：EXPLAIN 驱动的索引和增量迁移

**Files:** 只修改被诊断证明需要的 Configuration；`*_GatewayQueryIndexes.cs`、生成 Designer/ModelSnapshot、GatewayQueryMigrationTests。

**Interfaces:** EF Migration `GatewayQueryIndexes.Up(MigrationBuilder)`/`Down(MigrationBuilder)`，只创建/删除本批新增索引；不改任何业务列。索引清单记录表/列/方向/现有等价索引/关联查询/小大档计划/空间，未达收益或重复的候选不加入 migration。

- [ ] **1. 先采样再红测：** `./scripts/check-gateway-query-performance.sh explain --baseline "$PERF_BASELINE_COMMIT" --candidate "$PERF_CANDIDATE_COMMIT" --harness "$PERF_HARNESS_COMMIT" --output docs/evidence/gateway-query-performance/query-plans-before-indexes`，独立诊断窗 `EXPLAIN (ANALYZE, BUFFERS)`，涵盖关键授权/排序/关联及小大档。用确定的缺索引查询编写 `MigrationAddsOnlyApprovedIndexesAndPreservesData`、`UpgradeFailureRollsBackNewIndexesAndKeepsRecords`、`EmptyDatabaseMatchesEfModel`：
  ```csharp
  Assert.Equal(beforeBusinessFingerprint, afterBusinessFingerprint);
  Assert.Equal(approvedIndexNames.Order(), actualNewIndexNames.Order());
  Assert.Equal(beforeIndexCatalog, afterInjectedFailureIndexCatalog);
  ```
  故障使用夹具事务内在第一新增索引后插入明确失败DDL，验证实际PG回滚；测试不得改产品 migration 来模拟失败。
- [ ] **2. 红测命令：** `./scripts/check-contracts.sh integration --filter FullyQualifiedName~GatewayQueryMigrationTests`；目标索引不存在导致明确失败。若诊断无需新增任何索引，保留报告并将本任务标“无需迁移”，不能空造索引来让红测成立。
- [ ] **3. 生成最小迁移：** 在已锁SDK的隔离容器运行仓库固定 dotnet-ef 10.0.12，`dotnet ef migrations add GatewayQueryIndexes --project src/WebApi.Infrastructure --startup-project src/WebApi.Migrator`；审查Up/Down及模型无业务字段diff。现有Release Env/CreatedAt/Id索引复用；Route表达式索引仅在实际排序计划有收益时加，记录表达式与EF模型的管理方式。
- [ ] **4. 绿测与迁移演练：** 同红测命令；`./scripts/check-gateway-query-performance.sh migration --baseline "$PERF_BASELINE_COMMIT" --candidate "$PERF_CANDIDATE_COMMIT" --harness "$PERF_HARNESS_COMMIT" --output docs/evidence/gateway-query-performance/migration`；空库、B真实数据升级、失败阻断三场景通过，ANALYZE后业务指纹不变，空间和前后计划可对照。索引未必小档使用，不把planner差异自动当失败。
- [ ] **5. 提交候选：** 精确需要的EF配置/生成文件/测试，message `perf: add measured gateway management query indexes`；授权后执行，随后固定最终C。

### Task 12：完整 A/B 比较、回归、独立审查和交付

**Files:** 性能证据目录及本计划；发现真实缺陷按所属任务回修，保留首次失败。

**Interfaces:** 消费 B/H/C；证据 `verification.json` 固定含 source/image/harness/dataset hashes、required/completed/failed cells、SQL budgets、regression/migration/cleanup receipts、limitations、`complete`。report.mjs 拒绝不同数据/H/环境混比、缺单元和把预期401计作异常。

- [ ] **1. 报告器红测：** `different_harness_or_dataset_rejects_comparison`、`missing_cell_prevents_complete`、`expected_401_counts_completed_not_failure`；`index_memory_growth_requires_assessment`。精确阈值测试小档中位throughput或P95退化>10%，新增稳态index内存>25%时必须有原因/复验或结构评估，不能删除原样本。
  ```js
  assert.equal(reportWithOneMissingCell.complete, false);
  assert.equal(expected401.unexpected, 0);
  assert.equal(unassessed26PercentMemoryGrowth.complete, false);
  ```
- [ ] **2. 红测命令：** `node --test scripts/performance/performance.test.mjs`，缺证据错误关闭包的断言失败；实现 Task1 `report.mjs` 的校验后重跑全部绿测。
- [ ] **3. 固定 C 后完整比较：** `./scripts/check-gateway-query-performance.sh compare --baseline 40759aac50f1a6281223b0ec0d45d58e6fb44cb5 --candidate "$PERF_CANDIDATE_COMMIT" --harness "$PERF_HARNESS_COMMIT" --output docs/evidence/gateway-query-performance/comparison`。H/B/C 必须实际存在且无product overlay。按单元交替 A/B、B/A；下面矩阵每单元10s预热+20s测量，3次独立重复，保存完整直方图/样本、expected/异常/timeout/incomplete及每节点数据。

  | 必测组 | scale/auth/concurrency/node | 两版合计独立窗口 |
  |---|---|---:|
  | 主组 | S/M/L × Anonymous/合法ApiKey/不存在AccessKey/合法RS256 × 1/32/128 × dual | 216 |
  | 补充组 | L × ES256/错误secret/无mapping/无grant × 32 × dual | 24 |
  | 单节点组 | L × 四主认证场景 × 32 × GW-A/GW-B | 48 |

  总288窗口，最少纯窗口144分钟，不含切换/构建/管理查询。ApiKey/路径均匀分布及固定首/末资源由不同 cell 子配置记录，不能只取最佳路由；Snapshot数据/harness同源。窗口中token/credential合法到期时间足够且A/B一致，不能在运行中刷新数据指纹。
- [ ] **4. 管理端完整比较：** 每类S/M/L各100/1000/10000有效资源，页大小1/50/100、首/中/末/越界，报告Total、ID、实际可见量、SQL总数/权限/数据/未分类、读取行量/响应bytes/P95/内存。API≤2、Cluster≤3、Route≤4、Application≤2、Policy≤2、Users≤3、Nodes≤2、Events≤2、Snapshot≤3、Release≤12、AlertEvent≤6、AlertRule≤10；PolicyReferences按200IDs/8MiB批预算另列。权限负面身份独立验证状态码，不混入吞吐。
- [ ] **5. 内存/瓶颈与计划复验：** 保存S/M/L代构建elapsed/allocated、稳态和挂起旧请求双代heap/RSS峰值、租约释放后退休回收；记录客户端CPU/queue/scheduling、后端吞吐、GW/CP CPU/GC pause/alloc。小档>10%退化、大档无可重复成本收益、稳态额外>25%内存均列问题并回修/复验；客户端/后端先饱和时保留原窗口并加能区分成本的场景，不宣称端到端提升。索引最终 EXPLAIN/空间另窗输出。
- [ ] **6. 完整回归：** 顺序执行 `./scripts/check-contracts.sh domain`、`./scripts/check-contracts.sh integration`、`./scripts/check-contracts.sh gateway`、`./scripts/check-local-runtime.sh unit`，后者已包含Console Node测试；再 `"$WEBAPI_PNPM" --dir console build` 仅构建，避免重复Console测试。执行前 WEBAPI_NODE/WEBAPI_PNPM 设为现有可执行文件并校验版本，不下载依赖。安全加固、JWT/Header、旧代/历史/回滚、审批/风险、告警通知、分页/撤权、Migration全部通过。测试总数以本次日志为准，不套用旧包数字。
- [ ] **7. 新审阅者整批审查：** 按 requesting-code-review 技能执行，明确B到C、设计/计划/证据路径和风险点，独立检查授权表达式、hidden保护、批预算、排序、索引生命周期与指标真实性。修复重要问题后重跑受影响回归/矩阵；源提交变更重新记录C，不能沿用旧image测量当新版本证据。
- [ ] **8. 交付门禁与提交候选：** owner清单校验清理完成、零遗留；秘密扫描通过；主工作区原有文件hash/status不变（其他会话新增文件不误删）。候选message `docs: record gateway query performance verification`，仅本包证据/明确交付文档按授权提交。交付简短比较表、原始日志链接、已完成/未完成项和4192未升级声明；没有完整矩阵、未解决回归/饱和或缺独立审查时 `complete=false`，不关闭第2包。

## 计划自审与待审阅状态

已逐项核对设计：Gateway索引/认证/生命周期→Task2–3；权限和核心列表→Task4–7；Release/Alert/Reference→Task8–10；Migration→Task11；固定夹具/环境/矩阵/SQL/内存/安全回归/清理→Task1/12。Review Focus五类均有具名测试。没有执行产品修改、Docker测量或4192升级。

实施时允许调整私有实现，但 public DTO/业务语义和上述预算不可自行减项；遇到无法满足产品预算的L档种子先修正种子并记录，不放宽产品上限。若实际基线/可测场景与设计发生重大矛盾，应带诊断证据说明偏差并重新确认范围。

状态：用户已确认实现计划，Task1实施中。沿用当前会话逐项实现、测试、最终独立审查方式。L档按用户明确选择调整为10,000路由/5,000应用/10,000凭证/每应用8窗口，保留原超限失败证据。
