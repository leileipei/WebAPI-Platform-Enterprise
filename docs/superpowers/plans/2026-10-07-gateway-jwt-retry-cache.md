# 网关 JWT、重试与缓存 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成第二批JWT、有限安全请求重试和有界共享响应缓存，保持旧快照/权限/审批/业务兼容，并交付固定提交验收后的4192本机安装。

**Architecture:** 延伸现有Policy和RoutePolicyBinding，JWT认证、公钥和应用映射随候选冻结，网关只消费同一租用generation。采用快照2.2能力门禁、YARP2.3单尝试适配、Redis有界缓存；认证/授权/限流在缓存之前，Miss才经过熔断和重试。新功能默认不绑定，软件升级不自动重发布原业务。

**Tech Stack:** .NET10及现有固定SDK镜像，IdentityModel8.19.2、YARP2.3.0、StackExchange.Redis3.3.1、现有EF/Npgsql/PostgreSQL、React19.2/Vite6.4、xUnit和Node原生测试；全部锁定，不新增前端依赖。

**Spec:** [已确认的第二批设计A](../specs/2026-10-07-gateway-jwt-retry-cache-design.md)，原始设计提交`f85313b`；用户2026-10-07回复“a”确认A。产品源码与实际4192基线为`cc880ba`，主目录含独立验收文档提交`fe9b85d`。

状态：计划待用户审阅；全部13任务尚未开始。沿用Native：本会话逐任务实施，最后一次独立整分支审查；不另派逐任务实施/审查代理，不重复选择执行方式。后续SMTP/Webhook、跨环境审批仍属第三批。

## Global Constraints

- 不改现有权限代码、审批人数/顺序、历史数据/快照字节与哈希；不自动向原4192写入QA业务或重新发布4/4。
- 策略总配置≤32KiB；JWT最多8公钥、8audiences、128映射，token≤16KiB；RS256/ES256、公钥RSA2048–4096或P-256，禁止私钥/none/HS*及网络密钥解析。
- JWT clockSkewSeconds 0–120，maxTokenLifetimeSeconds 60–86400；issuer/claim/mapping按Spec精确匹配；forwardBearer默认false，专用API audience。
- 单路由最多六类且各类一个；重试maxAttempts1–3、无body GET/HEAD；有效路由总超时涵盖全部尝试/退避/响应，取消和已经开始响应不得重放。
- Retry perAttemptTimeoutMs10–300000；baseDelayMs0–1000、maxDelayMs0–5000且≥base，jitterPercent0–100；重试状态仅502/503/504。
- 缓存GET200、ttlSeconds1–3600、单条1024–1048576字节；默认条目256KiB，部署总64MiB/2000条，每操作到期清理≤128条；Redis读写默认100ms预算。
- Cache varyHeaders≤8项，单值≤1024、合计≤8KiB；键输入≤16KiB；VerifiedIdentity分区和Redis故障Bypass不可关闭。
- JWT转发Bearer时token-HMAC参与分区；旧ApiKey/Anonymous的业务Authorization、Cookie、Range/条件/流式请求不缓存。
- HMAC32字节私有文件、同一部署双节点共享，目录0700/文件0600；原账号/数据库/SSO凭据及旧固定工具文件不改，不打印token/sub/私钥/HMAC。
- 同一请求全程同一generation；最多5类最终决策、3项attempt，指标不含用户/令牌/query/key等无界标签；遥测关闭仍正确执行。
- 原主目录7项修改和受保护本机文件逐SHA保留；精确暂存任务清单，不`git add .`、不reset/stash覆盖、不push。临时克隆只按UUID与owner校验清理。

## Review Focus

1. 同一JWT用户换scope/role但转发Bearer：不能共用旧后端响应；任务7/8键和真实跨节点测试。
2. 等待重试期间切换快照或熔断转Open：仍用旧generation，停止追加尝试，半开最多1个物理请求；任务6/8。
3. 上游body通过BodyWriter写入或中途失败：缓存副本受限、流式不截断、不留半条成功数据；任务8。
4. 部分发布保留旧JWT绑定，其应用暂时无任何API授权：候选仍冻结映射身份并运行403，不能漏掉应用或用工作配置替换；任务2/3。
5. Redis命令超时但服务器可能已执行、元数据不一致：不重试扣费/缓存写入；拒绝新增缓存并Bypass，不能突破配额或误报Hit；任务7/11。

---

## 文件职责、环境和覆盖

执行时使用`using-git-worktrees`核对并复用`enterprise/.worktrees/gateway-restart-readiness`：当前tracked源码无改动，先检查文档提交合并冲突，再快进同步主目录本轮文档；不新建或清空历史工作区。计划路径以该工作区为根；主目录7修改保持原位。

|文件组|职责|
|---|---|
|Contracts/Policies、Domain/Policies|严格配置、公钥形状、有效认证、六类绑定和组合边界|
|Infrastructure/Policies、Applications、Releases|映射权限/引用保护、应用冻结、修订全集、2.2编译|
|Gateway/Security|有界JWT验证、同generation认证/API授权、私有身份上下文|
|Gateway/Forwarding|锁定YARP适配、公共健康hook、加权选址和attempt生命周期|
|Gateway/Policies|有界重试、缓存资格/键/Redis配额、流式旁路捕获与组合|
|Gateway/Observability、Infrastructure/Observability|受控决策、真实attempt、Scope投影及CSV/Trace|
|console/src/policies、pages|动态表单/映射/绑定、发布Review、Scope/412/键盘、观测呈现|
|scripts/gateway-policies、tests/gateway-policies、deploy/runtime|自有fixture/克隆、保护/材料门禁、Secret挂载、固定提交安装/恢复|

验证沿用`./scripts/check-contracts.sh domain|integration|gateway [dotnet参数]`的UUID/owner、只读NuGet seed及RuntimeTool构建；不复制另一个测试运行器。`N`为`/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node`；`WEBAPI_PNPM`使用已安装fallback/pnpm，执行时核实路径。Node使用`N --test ...`；全Console为`./scripts/check-console.sh`。锁文件首次更新明确记录，正式运行必须`--locked-mode`。

新增类型编译失败不是有效行为RED：先补最小签名/抛NotImplementedException入口让目标用例真正运行，再确认断言失败；不计fixture/模块加载/同版本冲突失败为产品RED。每任务结束记录完整命令、失败→通过证据、实际commit和零skip；最终计数来自日志，不沿用第一批1473。

设计章节1–3→任务1/2/10/13；4→1/2/4/5；5→6；6→7/8；7→3/11；8→9/10；9→11/12/13；10→各任务及12全量；11批准/边界→本文和最终报告。

## Task 1：严格配置和六类组合

**Files:** Create `src/WebApi.Contracts/Policies/AdvancedPolicyContracts.cs`、`src/WebApi.Domain/Policies/{JwtPolicyConfiguration,RetryPolicyConfiguration,CachePolicyConfiguration}.cs`、`tests/WebApi.Domain.Tests/AdvancedPolicyConfigurationTests.cs`；Modify `PolicyConfigurationValidator.cs`、`PolicyBindingRules.cs`、`PolicyContracts.cs`。

**Interfaces:** `AuthenticationMode { ApiKey, Anonymous, JWT }`；`JwtApplicationMapping(string ClaimValue,Guid ApplicationId)`；`JwtAuthenticationConfiguration`字段逐一对应Spec§4.1，Jwks为clone后的JsonElement；`AuthenticationConfiguration(AuthenticationMode Mode,JwtAuthenticationConfiguration? Jwt)`。`ParseAuthentication(string) -> AuthenticationConfiguration`、`ParseRetry(string) -> RetryConfiguration`、`ParseCache(string) -> CacheConfiguration`由现有Validator提供；重试/缓存record字段与Spec表完全一致。EffectiveRoutePolicies保留现有构造参数，追加可选Authentication/Retry/Cache；`HasApplicationIdentity`由有效模式推导，不用RequireApiKey推断JWT。

- [ ] **Step 1：写失败用例。** `JwtRejectsPrivateUnknownDuplicateAndMismatchedKeys`断言none/HS/私钥/重复kid/嵌套重复字段422；`RetryAndCacheEnforceExactBudgets`覆盖每个Spec极值±1、NaN/重复/未知字段；`SixBindingsAllowJwtApplicationRateAndRejectAnonymousRate`断言6种可组合、重复/第7类拒绝，Jwt不能折成Anonymous；`LegacyAuthNormalizationBytesUnchanged`比原ApiKey/Anonymous字节。
- [ ] **Step 2：实际RED。** Run `./scripts/check-contracts.sh domain --filter AdvancedPolicyConfigurationTests`，只将成功运行后行为断言失败登记RED。
- [ ] **Step 3：实现。** 按上述签名纯解析/规范化；JWKS只允许Spec公用字段和显式公共元数据白名单，所有嵌套重复键拒绝；签名验证留任务4。相同边界在绑定时校验，retry单次期限不得超过有效timeout。
- [ ] **Step 4：GREEN与旧规则回归。** Run同上及`--filter 'PolicyConfigurationTests|PolicySnapshotCompilerTests|AdvancedPolicyConfigurationTests'`，完整通过/零skip。
- [ ] **Step 5：精确提交。** 只暂存上述实际修改文件，commit `feat(policies): validate JWT retry and cache configurations`。

## Task 2：映射权限、应用引用与候选冻结

**Files:** Create `src/WebApi.Infrastructure/Policies/{JwtApplicationBindingService,GatewayPolicyDeploymentRules}.cs`、`tests/WebApi.Integration.Tests/JwtApplicationBindingTests.cs`；Modify `PolicyService.cs`、`RoutePolicyService.cs`、`ApplicationService.cs`、`ReleaseCandidateBuilder.cs`、`PolicyReferenceService.cs`（均现有层路径）、`src/WebApi.Infrastructure/Routing/RouteService.cs`、`src/WebApi.Contracts/Catalog/CatalogContracts.cs`、`src/WebApi.ControlPlane/Policies/PolicyEndpoints.cs`及DI注册。

**Interfaces:** `JwtApplicationBindingService.ValidateAsync(ScopeRef scope,JwtAuthenticationConfiguration config,ActorContext? actor,CancellationToken ct) -> Task`；`ReferencedApplicationIds(string type,string config) -> IReadOnlySet<Guid>`；`RequireNotReferencedAsync(Guid applicationId,CancellationToken ct) -> Task`。管理调用传actor并要求已有app.read可见，冻结调用传null只做可信Scope检查；API授权仍由现有app.permission.manage维护，映射不新增授权。`GatewayPolicyDeploymentRules.Validate(string type,string normalizedConfig) -> void`校验issuer HTTP例外、部署缓存单条上限及额外Vary头；Domain任务1只检查Vary合法token、数量和敏感头禁止，部署白名单由本规则明确限定。`PolicyLimitsDto(int MaxEntryBytes,IReadOnlyList<string> AllowedVaryHeaders)`经新增只读`GET /api/v1/policies/limits?organizationId=...&projectId=...`返回，使用既有policy.read与Scope，不提供Secret或写配置入口。RouteDto追加null省略的`string? EffectiveAuthenticationMode=null`，只JWT填JWT，旧认证省略保持历史投影字节；SaveRouteRequest追加null省略`string? AuthenticationChange=null`，仅允许ApiKey/Anonymous显式替换，JWT创建走策略绑定。未提供新字段且旧bool未变的完整编辑器回传不得替换JWT。

- [ ] **Step 1：写失败用例。** `ForeignAndUnreadableMappingReturnsUniformError`、`OrgPolicyCannotBindProjectApplication`、`CopyRevalidatesDestinationScope`断言无外部名称泄漏；`MappedZeroGrantApplicationIsFrozenAndRevisionRequired`断言候选含零授权应用及application修订，遗漏422/变更412；`DeleteMappedApplicationIsProtected`、`FrozenMappingUnaffectedByLaterEdits`；`DeploymentLimitsEnforceHttpIssuerExtraVaryAndEntrySize`验证精确HTTP例外、未登记业务Vary拒绝、部署收紧单条上限与只读limits权限；`LegacyRouteEchoPreservesJwtAndExplicitAnonymousChangeRequiresPolicyWrite`断言元数据/原样回传不抹JWT，显式改变须权限，矛盾mode/bool422。
- [ ] **Step 2：RED。** Run `./scripts/check-contracts.sh integration --filter JwtApplicationBindingTests`。
- [ ] **Step 3：实现。** Save/Validate/Copy/Replace绑定重查可见性/Scope，策略列表筛选开放retry/cache。冻结需合并当前绑定及baseline保留JWT映射应用集合，不能只看本次选中策略/现有grants。路由元数据回传保JWT，显式AuthenticationChange与RequireApiKey一致后按既有policy.write创建私有认证策略，未显式改动不创建Anonymous。删除保护覆盖工作、冻结及受保护运行引用，统一409不泄露；Scope迁移延续现有不可移动约定。
- [ ] **Step 4：GREEN。** 同上并跑`PolicyManagementTests|PolicyDeletionTests|PolicyReleaseTests|RoutePolicyBindingTests`；真实事务/治理锁，不能以mock授权替代数据库边界。
- [ ] **Step 5：提交。** commit `feat(policies): freeze scoped JWT application mappings`，只暂存本任务文件。

## Task 3：2.2编译、校验与节点能力

**Files:** Modify `src/WebApi.Contracts/Runtime/SnapshotContracts.cs`、`src/WebApi.Domain/Runtime/SnapshotValidator.cs`、`src/WebApi.Infrastructure/Releases/SnapshotCompiler.cs`、`src/WebApi.Infrastructure/Gateway/SnapshotSchemaCapabilities.cs`、`src/WebApi.Gateway/Workers/NodeClient.cs`、`src/WebApi.Gateway/Configuration/RuntimeGenerationStore.cs`、`src/WebApi.Infrastructure/Runtime/RedisSnapshotStore.cs`；Create `tests/WebApi.Domain.Tests/AdvancedSnapshotTests.cs`、`tests/WebApi.Integration.Tests/AdvancedNodeCapabilityTests.cs`。

**Interfaces:** RuntimeRoute追加`[JsonIgnore(WhenWritingNull)] AuthenticationMode? AuthenticationMode=null`，旧字段位置不变；2.2有效JWT RequireApiKey=false且匹配JWT绑定。RuntimeGeneration增加只读Authentication/Retry/Cache字典；SnapshotValidator显式协议约束。Node支持`["2.0","2.1","2.2"]`；既有能力门禁所有发布/恢复/回滚路径复用。

- [ ] **Step 1：写失败用例。** `JwtRetryOrCacheSelects22`、`JwtWithoutGrantRetainsApplicationAndReturnsNoAnonymousFold`、`PartialPublishPreservesOldJwtApplication`；`Legacy20And21BytesHashesUnchangedAndRejectNewFields`校验原字节/哈希及raw新字段拒绝；`Missing22CapabilityDoesNotAdvanceDesiredOrSequence`覆盖普通发布/回滚/重试、实例替换。
- [ ] **Step 2：RED。** Run Domain过滤AdvancedSnapshotTests及Integration过滤AdvancedNodeCapabilityTests；能力误拒绝和fixture错误不算功能RED。
- [ ] **Step 3：实现。** 未选路由/运行策略保旧身份；最终快照含新语义才2.2。旧协议不接受新字段/模式，即使一般反序列化会忽略字段也须raw验证。冻结映射应用不会因零grant被编译器丢掉。限制仍按现有快照资源预算，不随绑定数增加而无界放宽。
- [ ] **Step 4：GREEN。** 重跑并回归`SnapshotCompilerTests|PolicySnapshotCompilerTests|PolicyNodeCapabilityTests|RollbackTests|PublishTests`。
- [ ] **Step 5：提交。** commit `feat(runtime): gate JWT retry and cache snapshots on schema 2.2`。

## Task 4：有界离线JWT验证

**Files:** Create `src/WebApi.Gateway/Security/{JwtTokenVerifier,VerifiedTrafficIdentity}.cs`、`tests/WebApi.Gateway.Tests/JwtTokenVerifierTests.cs`、`docs/dependencies/gateway-jwt.md`；Modify `Directory.Packages.props`、Gateway.csproj和实际受影响锁文件。

**Interfaces:** `JwtTokenVerifier.Verify(string token,JwtAuthenticationConfiguration configuration,DateTimeOffset now) -> JwtVerificationResult`；result只公开Success/FailureCode，成功私有值含Issuer/Subject/ApplicationClaimValue/ValidatedBearer，不提供默认序列化日志DTO。`VerifiedTrafficIdentity(AuthenticationMode mode,Guid? applicationId,JwtVerifiedClaims? jwt)`仅请求内存使用。直接固定`Microsoft.IdentityModel.JsonWebTokens`/`Tokens`8.19.2，不改变现有OIDC版本。

- [ ] **Step 1：写失败用例。** 真实RSA/EC签名`Rs256AndEs256VerifyWithExactIssuerAudienceType`；`RejectNoneHsConfusionBadSignatureUnknownKidAndTokenSuppliedUrls`；`RejectDuplicateClaimsExtraSegmentsAndOver16KiB`；`RequireFiniteDatesSubAndBoundedLifetime`用TimeProvider/固定now钉住exp/iat/nbf±skew；`NeverInvokesNetworkOrLogsClaims`。
- [ ] **Step 2：RED。** Run `./scripts/check-contracts.sh gateway --filter JwtTokenVerifierTests`；生成测试钥匙仅内存/私有fixture，公共证据只哈希。
- [ ] **Step 3：实现。** 预检大小/base64url/JSON重复/深度后使用IdentityModel验签；算法/typ/kid/issuer/audience白名单与Spec精确字段；验签成功前不消费映射声明。不实现自己密码学、不配置fetch/discovery或PII日志。
- [ ] **Step 4：GREEN。** 同上；固定SDK恢复新锁文件后`--locked-mode`验证Gateway及其依赖，记录包许可/完整hash。
- [ ] **Step 5：提交。** commit `feat(gateway): verify bounded JWTs with pinned public keys`，锁文件只暂存实际变更。

## Task 5：统一请求认证与同generation授权

**Files:** Modify `src/WebApi.Gateway/Security/ApiKeyMiddleware.cs`、`src/WebApi.Gateway/Policies/TrafficExecutionContext.cs`、`src/WebApi.Gateway/GatewayApp.cs`；Create `tests/WebApi.Gateway.Tests/JwtAuthorizationPipelineTests.cs`；Extend既有`Support/GatewayFixture.cs`。

**Interfaces:** 保留ApiKeyMiddleware.GenerationItem与lease边界；TrafficExecutionContext追加VerifiedIdentity，ApplicationId保原兼容属性。JWT策略由generation.Authentication取得，结果映射Application后复用有效API授权检查；错误401 invalid_jwt/403 api_not_granted，JWT WWW-Authenticate只含通用值。

- [ ] **Step 1：写失败用例。** `JwtValidAppGrantAllowsAndAppRateReceivesIdentity`；`WrongTokenCannotFallbackToApiKeyOrAnonymous`；`UnknownMapping401DisabledOrUnGranted403`；`JwtStripsSpoofedHeadersAndOnlyExplicitlyForwardsBearer`；`OldRequestAcrossActivationUsesOldJwtKeysMappingAndGrant`；旧ApiKey/Anonymous业务Authorization透传保持。
- [ ] **Step 2：RED。** Run `./scripts/check-contracts.sh gateway --filter JwtAuthorizationPipelineTests`，真实两网关/后端计数验证拒绝零网络调用。
- [ ] **Step 3：实现。** 只JWT模式解释Bearer，拒绝多值/空/非Bearer；将私有身份置请求上下文，移除伪造内部头、X-API-Key，按forwardBearer决定Authorization。所有认证/API授权成功才调用next；finally释放generation仍保既有原子激活。
- [ ] **Step 4：GREEN。** 同上及GatewayRuntimeTests/TrafficPolicyPipelineTests；关闭遥测也须实际通过App限流/授权。
- [ ] **Step 5：提交。** commit `feat(gateway): authorize JWT traffic in the leased snapshot`。

## Task 6：YARP2.3兼容适配与有限重试

**Files:** Create `src/WebApi.Gateway/Forwarding/{GatewayForwardingAdapter,RetryResponseTransformer,WeightedDestinationSelector,ForwardAttemptRegistry}.cs`、`src/WebApi.Gateway/Policies/{RetryCoordinator,RetryForwardingMiddleware}.cs`、`tests/WebApi.Gateway.Tests/{GatewayForwardingCompatibilityTests,RetryPipelineTests}.cs`；Modify WeightedDestinationMiddleware、TrafficExecutionContext、GatewayApp、Domain CircuitBreakerState。

**Interfaces:** `GatewayForwardingAdapter.SendAsync(HttpContext context,DestinationState destination,ForwardAttemptControl control,CancellationToken ct) -> Task<ForwardAttemptResult>`；control为`(int Attempt,TimeSpan Timeout,Func<HttpResponseMessage,bool> SuppressResponse)`，result为`(ForwarderError Error,bool Suppressed,int? StatusCode,bool DefinitelyNotSent)`。`RetryCoordinator.ExecuteAsync(HttpContext context,TrafficExecutionContext execution,RetryConfiguration config,CancellationToken ct) -> Task`；`WeightedDestinationSelector.Select(RuntimeGeneration generation,RuntimeCluster cluster,IReadOnlyList<DestinationState> available,IReadOnlySet<string> attempted) -> DestinationState?`。`ForwardAttemptRegistry.Acquire(RuntimeGeneration generation,DestinationState destination) -> IDisposable`及`Count(RuntimeGeneration generation,DestinationState destination) -> int`只记录当前适配器真实并发，最后引用释放即移除entry；既有selector按YARP并发+适配器并发计算LeastRequests，不触碰internal ConcurrencyCounter。

- [ ] **Step 1：写失败用例。** 锁定YARP夹具验证默认Host/X-Forwarded/路径基址/查询/HTTP错误与资源释放；`TwoSuppressed503Then200ProducesThreeCallsOneResponse`；`RetryUsesDifferentHealthyWeightedTargets`；`NoRetryUnsafeBodyStartedResponseUncertainSendOrCancel`；`OverallBudgetIncludesDelayAndRetryAfter`；`HalfOpenIsOneAttemptAndConcurrentOpenStopsNext`；`ActivationDuringBackoffKeepsOldGeneration`。
- [ ] **Step 2：RED。** Run `./scripts/check-contracts.sh gateway --filter 'GatewayForwardingCompatibilityTests|RetryPipelineTests'`，真实TCP后端和计数；若固定版本接口不支持规格，不以改要求或跳过掩盖。
- [ ] **Step 3：实现。** 适配器使用IHttpForwarder、租用route.Transformer和feature.Cluster.HttpClient，包装transform在默认响应头复制前决定抑制；每尝试调用公共PassiveHealthCheckMiddleware包围适配器，更新ProxiedDestination/错误特性并只通知实际目标。无重试/不合格请求走原next，保持原流式/负载均衡。显式连接尚未发送证据才DefinitelyNotSent=true，其余false；总期限优先取消，有限抖动退避，最后失败正常发送。执行上下文持有CircuitAdmission，Probe不追加尝试；读取状态判断后续准入不消耗第二个名额。
- [ ] **Step 4：GREEN。** 重跑两套及TrafficPolicyPipelineTests/CircuitRegistryTests；确认默认通路并发计数不双计、所有响应/lease归还、无跳转/Cookie容器。被抑制响应不会因清理导致终态HTTP状态误报。
- [ ] **Step 5：提交。** commit `feat(gateway): add bounded safe retries through YARP 2.3`。

## Task 7：缓存资格、身份键与原子配额

**Files:** Create `src/WebApi.Gateway/Policies/{GatewayCacheSettings,CacheEligibility,CacheKeyBuilder,IResponseCacheStore,RedisResponseCacheStore,ResponseCacheScripts}.cs`、`tests/WebApi.Gateway.Tests/{CacheEligibilityTests,CacheKeyTests,RedisResponseCacheTests}.cs`；Modify `src/WebApi.Gateway/Configuration/SnapshotActivation.cs`及GatewayApp服务注册。

**Interfaces:** `CacheEligibility.Request(HttpContext,VerifiedTrafficIdentity,CacheConfiguration,GatewayCacheSettings) -> CacheEligibilityResult(bool Eligible,string Reason)`；`Response(IHeaderDictionary,int status,DateTimeOffset requestAt,DateTimeOffset responseAt,CacheConfiguration) -> CacheStorageRule(bool Store,TimeSpan FreshFor,double InitialAge,string Reason)`。`CacheKeyBuilder.Build(TrafficExecutionContext,HttpRequest,CacheConfiguration) -> CacheLookupKey?`返回仅opaque HMAC。`IResponseCacheStore.GetAsync(CacheLookupKey,CancellationToken) -> ValueTask<CacheReadResult>`、`PutAsync(CacheLookupKey,CacheEntry,CancellationToken) -> ValueTask<CacheWriteResult>`；Read Kind=Hit/Miss/Unavailable，Write Kind=Stored/Bypass；Entry含有限头、body、ResponseAt/InitialAge/FreshFor，不含token/sub/Trace。`GatewayCacheSettings.Validate(RuntimeSnapshot snapshot) -> void`在写LKG/切换generation前校验本节点单条预算/Vary白名单与必需Secret；不得仅在请求时偷偷旁路错误配置。所有类型定义在本任务文件，私有身份不加入entry。

- [ ] **Step 1：写失败用例。** `IdentityAndTokenForwardingPartitionPrecisely`断言两sub/两转发token不同，false模式刷新相同；`LegacyBusinessAuthorizationAndCookiesBypass`；`AllRawQueryValuesAndVaryAreIncluded`保持重复query顺序；`NoStorePrivateNoCacheUnknownVaryAndSetCookieNeverStore`；`AgeAndOriginLifetimeCapPolicyTtl`；`ConcurrentWritesCannotExceed64MiBOr2000Entries`、`TimeoutIsNotRetriedAndCorruptAccountingBypasses`、`SweepNeverProcessesOver128`；`NodeDeploymentLimitRejectsActivationAndRetainsLkg`验证部署收紧/不支持Vary时不能激活。
- [ ] **Step 2：RED。** Run `./scripts/check-contracts.sh gateway --filter 'CacheEligibilityTests|CacheKeyTests|RedisResponseCacheTests'`；Redis用独立项目真实server，不mock配额原子性。
- [ ] **Step 3：实现。** 配置读部署白名单和secret文件；键包含sequence及Spec全部身份/资源/请求维度。按Spec保守共享HTTP规则和Age，资源预算前置。Redis单部署前缀共享总计配额，以有限sorted expiry/size索引清理；payload自然过期与元数据生命周期均可界定。拒绝不可靠计数的新增写，保持正常代理，不再调用写命令重试。
- [ ] **Step 4：GREEN。** 重跑，并验证过期索引/namespace跨多个sequence仍≤配额、HMAC/身份不在result/错误/Redis明文key/公共日志。
- [ ] **Step 5：提交。** commit `feat(cache): add identity partitioning and bounded Redis storage`。

## Task 8：流式缓存捕获与限流/熔断/重试组合

**Files:** Create `src/WebApi.Gateway/Policies/{CacheResponseMiddleware,BoundedResponseCapture,CircuitPolicyMiddleware}.cs`、`tests/WebApi.Gateway.Tests/{ResponseCachePipelineTests,AdvancedPolicyCompositionTests}.cs`；Modify TrafficPolicyMiddleware（保留限流）、RetryForwardingMiddleware、GatewayApp、TrafficExecutionContext。

**Interfaces:** `BoundedResponseCapture.Install(HttpContext context,int maxEntryBytes) -> CaptureLease`；lease提供`TryComplete(CacheStorageRule rule) -> CacheEntry?`、Dispose恢复原IHttpResponseBodyFeature；Stream和PipeWriter都只保有限副本，最终成功完整才返回Entry。CircuitPolicyMiddleware持有旧admission/epoch并发布至execution，最终仍按一次入站上游结果Complete。流水线：认证→TrafficPolicyMiddleware限流→CacheResponseMiddleware→CircuitPolicyMiddleware→RetryForwardingMiddleware→旧Weighted/LoadBalance/PassiveHealth默认路径。

- [ ] **Step 1：写失败用例。** `NodeAFillsNodeBHitsWithFreshTraceAndAge`；`HitReauthsAndConsumesOneRateTokenWithoutCircuitSample`；`OpenAllowsFreshHitRejectsMissAndHitNeverClosesHalfOpen`；`BodyWriterOversizeContinuesStreamingWithoutCacheEntry`；`TruncatedBodyOrClientAbortNeverStores`；`RetriedFinal200OnlyStoresFinalResponse`；`PublishRollbackSequenceNeverReusesHistoricalCache`；任务7的不同JWT用户/token在真实两网关验证。
- [ ] **Step 2：RED。** Run `./scripts/check-contracts.sh gateway --filter 'ResponseCachePipelineTests|AdvancedPolicyCompositionTests'`，真实body写入/取消/实际Redis/后端计数。
- [ ] **Step 3：实现。** 缓存100ms预算失败Bypass，不返回假Hit；不等待完整body后才发客户端，不无界MemoryStream；各feature恢复finally执行。Hit不调用next，也不产生Destination/上游样本。只有最终完整合格200存，实例取消/transport error/trailer/缓存副本超限弃存且业务响应保持。
- [ ] **Step 4：GREEN。** 重跑及整个Gateway suite，记录真实调用次数、样本数、token次数与跨节点内容隔离，无遥测也通过。
- [ ] **Step 5：提交。** commit `feat(cache): integrate streaming cache with traffic protection`。

## Task 9：受控五类决策和真实attempt观测

**Files:** Modify `src/WebApi.Contracts/Policies/PolicyObservationContracts.cs`、Gateway Observability的`RequestTelemetryContext.cs`/`TelemetryAttributes.cs`/`GatewayTelemetryRecorder.cs`/`RequestTelemetryMiddleware.cs`/`TelemetryMetricExporter.cs`、Infrastructure Observability的`PolicyObservationProjection.cs`/`TraceProjection.cs`/`LokiLogSource.cs`/`AccessLogQueryService.cs`/`CsvLogExporter.cs`/`ObservationQueryService.cs`、`src/WebApi.Contracts/Observability/{LogContracts,TraceContracts}.cs`；Create `tests/WebApi.Gateway.Tests/AdvancedPolicyTelemetryTests.cs`、`tests/WebApi.Integration.Tests/AdvancedPolicyObservationTests.cs`。

**Interfaces:** PolicyDecisionDto保持旧字段和null省略，新增受控可选CacheRead/CacheWrite与AttemptCount，Cache只有一条最终decision；`ForwardAttemptObservation(int Number,Guid? DestinationId,int? Status,string Outcome,double DurationSeconds)`最多3条，公开Outcome白名单。request上下文有AttemptCount/CacheDisposition（可选）；Hit=0attempt/None destination。JSON/CSV/Trace/过滤一同投影，每次受TrustedObservationScope验证。

- [ ] **Step 1：写失败用例。** `ThreeAttemptsAreThreeClientSpansOneRequestMetric`；`CacheHitNoClientSpanAndFiveDecisionsSurvive`；`RetryFinalSuccessDoesNotMaskAttemptFailure`；`ForeignPoliciesAndUnknownOutcomeArePartialNotLeaked`；`NoJwtSubTokenCacheKeyOrSecretInExport`用私密canary验证全部DTO/CSV/attribute；旧2类无新字段仍可读。
- [ ] **Step 2：RED。** Gateway过滤AdvancedPolicyTelemetryTests、Integration过滤AdvancedPolicyObservationTests。
- [ ] **Step 3：实现。** 任务6适配器承担真实attempt client span，原Weighted只为旧通路创建client span，不重复包一个伪attempt；每个分支消费真实decision。更新所有Take(2)/白名单/过滤投影和metric有限标签；源缺失/采样仍Partial/Unavailable，不填补零。
- [ ] **Step 4：GREEN。** 重跑及GatewayTelemetryTests/PolicyTelemetryTests/PolicyObservationTests/ObservationTraceTests/ObservationLogsTests，确认敏感值从未导出。
- [ ] **Step 5：提交。** commit `feat(observability): report JWT retry and cache decisions`。

## Task 10：企业控制台动态表单与发布Review

**Files:** Create `console/src/policies/{JwtPolicyFields,RetryPolicyFields,CachePolicyFields}.tsx`、`advanced-policy-state.mjs`及`.d.mts`；Modify `PolicyForm.tsx`/`RoutePolicyEditor.tsx`/`PolicyReleaseReview.tsx`/`policy-state.mjs`/声明、`pages/Policies.tsx`/`PolicyEditor.tsx`/`Routes.tsx`、`api/policies.ts`及观测DTO/ObservationDrawer/TraceWaterfall；Create `console/tests/{advanced-policy-state,advanced-policy-view}.test.mjs`。缓存字段消费任务2的只读limits，上限与Vary选项不凭浏览器常量放宽。

**Interfaces:** `validateAdvancedPolicyDraft(draft,scope) -> string|null`；`changeAuthenticationMode(state,mode,confirmed) -> state`保留模式草稿，切换丢弃必须confirmed；`acceptApplicationOptions(state,{scope,generation,items}) -> state`拒绝旧scope/旧访问。三个Fields props=`{config,change,disabled,applications?}`；不在本地验证假签名，不把结构校验写成token有效。

- [ ] **Step 1：写失败用例。** 动态严格字段/default/budget，公钥文件仅公开JSON，JWT↔ApiKey脏草稿确认，旧scope应用选项迟到不覆盖，只读无保存，JWT可按App限流，6类绑定、412保输入/显式最新修订、Review显示公钥/映射/尝试/缓存冷启动；Routes列表显示JWT而非“匿名”，旧路由编辑器保存metadata不带AuthenticationChange，只有明确切认证才提交新字段。SSR断言无raw token输入持久化/假已发布提示。
- [ ] **Step 2：RED。** Run `N --test console/tests/advanced-policy-state.test.mjs console/tests/advanced-policy-view.test.mjs`；确认真实断言失败，现有SSR fixture遵循既有guard。
- [ ] **Step 3：实现。** 复用Shell/ui/脏状态/focus和现有app.read API分页，仅当前Scope可见选项；登录信息不放LocalStorage。操作只保存工作定义或跳既有发布页；观测显示实际attempt与Hit，不将capability或结构验证当运行成功。
- [ ] **Step 4：GREEN与构建。** 上述测试及`./scripts/check-console.sh`全部Node/tsc/Vite通过；CUA真键盘及双宽度留任务12，不用SSR代替。
- [ ] **Step 5：提交。** commit `feat(console): expose JWT retry and cache workflows`。

## Task 11：私有Secret部署、固定工具兼容和恢复门禁

**Files:** Create `scripts/gateway-policies/{runtime-secrets,protection,recovery}.mjs`、`tests/gateway-policies/runtime.test.mjs`；Modify `scripts/runtime/lifecycle.mjs`/`context.mjs`、`deploy/compose.runtime.yml`/`compose.runtime.gateways.yml`的新源码版本、`tests/runtime/deployment.test.mjs`/`backup.test.mjs`；Create本批runbook。

**Interfaces:** `ensureCacheSecret(directory,state,{readRandom,writePrivate}={}) -> Promise<{file,receipt}>`首次为旧部署新增32字节文件；receipt绑定owner与hash，已有receipt缺file须拒绝，不能重建使双节点密钥不同。`assertProtectedBaseline(before,after,{allowedRuntimeArtifacts}) -> void`；`canRestoreSoftware({targetSchema,currentSnapshots,newWorkData}) -> RestoreDecision`读取不可用时fail closed。新secret独立shared volume只给两Gateway只读挂载，既有各节点Secret和数据库隔离不变。

- [ ] **Step 1：写失败用例。** `LegacyInitializeCreatesOneSharedSecretWithoutRotatingAccounts`；`ExistingReceiptMissingKeyFailsClosed`；`NewAndOldToolsReadOwnedArchivedDeploySafely`；`CacheSecretInBackupButNeverPublicArtifact`；`Runtime22BlocksOldSoftwareDowngradeUntilCompatibleRollback`；`FailedReadDoesNotAuthorizeDestructiveRestore`。验证原固定目录未被写、原账号/SSO Secret逐SHA保持。
- [ ] **Step 2：RED。** Run `N --test tests/gateway-policies/runtime.test.mjs tests/runtime/deployment.test.mjs tests/runtime/backup.test.mjs`；只用自有fixture/private temp，不操作原4192。
- [ ] **Step 3：实现。** 新secret私有创建/receipt后seed卷，镜像启动前校验部署预算与secret；旧原固定工具不重写，新工具固定Git归档目录并另给入口。原release/state/runtime-config允许变化明示记录，密码/SSO/旧固定字节保持；禁凭旧备覆盖业务。把所有新增Secret卷纳入冷备/克隆/cleanup归属与SHA回读。
- [ ] **Step 4：GREEN。** 同上及全runtime Node suite；测试可区分Secret缺失与旧快照正常软件升级，不新增自动发布。
- [ ] **Step 5：提交。** commit `feat(runtime): provision private cache secrets and safe upgrades`。

## Task 12：固定提交全回归、真实克隆E2E和唯一整分支审查

**Files:** Create `scripts/gateway-policies/{fixture,scenario,evidence,acceptance}.mjs`、`tests/gateway-policies/evidence.test.mjs`、本批QA checklist；公共报告至`docs/evidence/gateway-jwt-retry-cache/`，实际密码/token/转储只存ignored私有目录。

**Interfaces:** `runGatewayPolicyScenario({revision,imageId,cloneDirectory,fixtureDirectory}) -> Promise<ScenarioResult>`；`validateGatewayPolicyEvidence(proof,files,{revision,imageId}) -> AcceptanceResult`须读取各材料内部identity/count/outcome及SHA，不信任wrapper通过标签。克隆owner/name/ports/private目录严格校验；只有UUID自有平台/issuer fixture可写。

- [ ] **Step 1：写材料门禁RED。** 篡改suite SHA/内部revision、缺scope隔离/不确定送出负向用例、空UI动作/旧截图改标、新镜像仅wrapper伪造、克隆指向4192、材料包含rawJWT/HMAC等必须失败。Run `N --test tests/gateway-policies/evidence.test.mjs`确认真实行为RED。
- [ ] **Step 2：实现证据门禁并GREEN。** 固定镜像5应用inspect及静态hash、五套日志真实Passed/零skip/exit0、保护/恢复/E2E/UI内部字段和逐SHA都绑定提交；不生成假的browser动作或源指标。
- [ ] **Step 3：实际完整执行。** 精确提交driver后Git archive固定产品SHA，构建锁定7应用/Console镜像。Domain/Integration/Gateway/Console/Runtime五套完整，记录实际计数；冷备原运行实例finally恢复，仅暂停已有授权服务，不在原环境造QA。真实恢复UUID克隆后候选→旧cc880ba→候选（未发2.2时）、专用issuer公钥/token、JWT正负、双节点RedisHit/identity、重试实际calls、限流一次/半开一次、源故障、2.2两级发布双ACK与回滚。再验证运行2.2时旧镜像被门禁拒绝。
- [ ] **Step 4：真实CUA和唯一审查。** 1440/1280实际DOM及新提交实际镜像：新建/编辑/公钥文件/映射、六类绑定、dirty/Escape/焦点/Enter、Scope迟到、412、只读、Review、日志Trace；保存截图/动作身份。然后使用requesting-code-review做一次独立整分支审查（沿用Native方法），所有Important/Critical同一修复批解决，修复变源码后重新固定构建/受影响测试及E2E/UI归属，不改标旧材料、不追加逐任务二审。
- [ ] **Step 5：封存提交。** 门禁通过后精确提交公开材料/中文结果/规则边界，私有凭据/转储不提交；commit `test(policies): verify fixed JWT retry and cache delivery`。Minor若延期逐条记录；安装4192尚未执行，pre-install不能写“已部署”。

## Task 13：合并、冷备、4192升级和交付

**Files:** Create `scripts/gateway-policies/delivery.mjs`、`docs/deployment/gateway-jwt-retry-cache-upgrade-result.md`、公开`installed-status.json`/`package-status.json`/`cleanup-status.json`；本地ZIP到`deliverables/`，不包含private目录。

**Interfaces:** `runGatewayPolicyDelivery({approvedRevision,baseline,verification,originalDirectory}) -> Promise<DeliveryReceipt>`分明确check/merge/backup/upgrade/final/package/cleanup阶段；失败finally恢复兼容候选/原服务，不删除备份或原卷。升级凭真实软件/静态身份，结果文档后续独立commit与软件SHA分开。

- [ ] **Step 1：最终只读基线。** 读取主目录branch/HEAD与原modified/untracked文件逐SHA、所有现存业务表（含第一批三表）、账号/Secret/SSO/旧维护工具/全部历史ZIP、原release/业务desired/sequence/网关/遥测；数量取实时盘点，不机械沿用旧645/28。再次确认任务12无open Important/Critical、真正verification SHA、候选镜像和克隆恢复通过。
- [ ] **Step 2：精确合并与冷备。** 在用户已授权范围快进或明确处理本批冲突，不夹带原7修改，不push；停止所需服务做全部平台/Keycloak/新增Secret卷冷备，逐SHA回读、finally恢复原服务，保留所有备份与新旧镜像。
- [ ] **Step 3：软件升级与实际验收。** 新HMAC文件/卷只增加本批明确项；按正确工具/候选镜像升级原4192，原业务配置4/4不重发。核对5实际app镜像、双Ready/业务双200、四源Available、原admin/两审批账号/Keycloak只读及退出、原业务表/Secret/旧文件保护；新页面可操作且无测试导入/策略/issuer映射。失败按恢复门禁操作，不使用旧库覆盖原数据。
- [ ] **Step 4：交付包与清理。** 报告明确固定公钥/安全请求范围/TTL/HTTP子集/性能未验收/第三批未实施。ZIP逐entry读回/CRC/CONTENTS.sha256/wholeSHA，实际私密值扫描；仅清理本轮owner吻合的fixture/驱动/克隆容器卷Secret，保留原服务、冷备、旧新镜像；浏览器仅关闭本轮临时标签。
- [ ] **Step 5：结果文档提交和最终核验。** 只暂存本批结果文件，commit `docs(policies): record verified 4192 upgrade and delivery`，不重新把文档SHA写成运行镜像。最终核验原文件保护、实际Ready/image/双200/四源、ZIP SHA后报告；给4192、中文报告、包和原部署截图链接。第三批另行设计，不声称所有六项已完成。

## 自查和实施交接

已逐节检查Spec覆盖、13任务依赖/接口、五类Review Focus及各任务真实测试。JWT验证结果的私有身份未进入观测DTO；缓存从不依据删掉Authorization后的请求判断匿名；零授权/部分发布的映射应用包含在冻结/编译；旧协议raw新字段拒绝、YARP private counters不直接调用、默认transforms/健康hook纳入固定版本实测；新工具允许新增运行receipt，但旧账户/SSO及固定工具字节保持。

Task6适配器接口依据锁定[YARP2.3 Forwarder](https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/ForwarderMiddleware.cs)、[HttpTransformer](https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/HttpTransformer.cs)和[公开被动健康中间件](https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Health/PassiveHealthCheckMiddleware.cs)核查；实际可编译/资源释放/HTTP行为仍需任务6测试，不以文档阅读当实现通过。

用户只需审阅本计划是否覆盖已确认目标；Native执行方式与本地提交/合并/冷备范围已确定，保持。计划审阅通过后用executing-plans逐任务建立进度账本，不再重新问方案A/B或执行方式。
