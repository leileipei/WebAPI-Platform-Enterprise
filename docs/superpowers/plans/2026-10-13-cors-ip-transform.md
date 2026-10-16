# CORS / IP Filter / Transform Implementation Plan

> **历史草稿，禁止据此开始实施：** 用户随后增加JSON请求体 / 响应体字段映射。本A范围草稿保留供追溯；新书面规格为`docs/superpowers/specs/2026-10-13-cors-ip-transform-json-body-design.md`，审阅后重写实施计划。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. 用户已选当前会话逐项实现、测试，完成后统一审查；不改成逐任务子代理执行。Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成第3包三类业务策略的管理、发布、双网关执行、观测、历史兼容、跨环境晋级及回滚闭环。

**Architecture:** 复用既有Policy、源修订、冻结候选和审批发布引擎，扩展快照2.3及目标节点能力门禁。将运行代际租约前置到IP / CORS之前，在原API授权后建立静态Transform计划；首次 / 重试的后端副本和缓存响应使用统一执行规则。JSON正文映射不纳入已选Transform A。

**Tech Stack:** 现有锁定.NET 10、YARP 2.3.0、EF / Npgsql / PostgreSQL、Redis、OpenTelemetry、React 19.2、TypeScript、Vite、xUnit、Node原生测试；不增加产品依赖，不调整锁定版本。

**Spec:** `docs/superpowers/specs/2026-10-13-cors-ip-transform-design.md`。用户已批准书面规格并授权编写本计划；本计划仍待审阅，不代表产品实施已获批准。

## Global Constraints

- 起点固定`293b37f2dbd59427cd2d40f6783dc441345528ef`；第2包保持暂停，原性能窗口、owner资源和证据均不修改或续跑。
- 保留主工作区原有文件内容和Git状态；4192账号、数据库和部署不作为测试夹具。实施使用独立受管工作区，资源按UUID / owner隔离。
- 新三类配置最大32768 UTF-8字节、最大JSON深度16；严格拒绝未知 / 重复字段；规范化幂等；绑定每类型一个、总数最多9、Priority 0–1000。
- Origin最多64项 / 512字符；CORS请求Header最多64项、暴露Header最多32项；maxAgeSeconds 0–86400。IP网络最多256项；仅使用既有可信代理边界解析后的RemoteIpAddress。
- Header变换每方向最多32项；Query变换最多32项；名称限制及所有受保护Header、路径编码、移除前缀匹配、16384字节结果预算严格按规格第3节。
- 保持16 MiB快照、10000业务路由、40000运行策略、节点协议能力最多4项、最多3次物理重试；预检内部条目总量最多2倍业务路由数。
- 支持历史2.0 / 2.1 / 2.2；新三类启用绑定需要2.3；保存工作配置不改变运行配置，全部目标节点门禁和当前实例ACK保持。
- 每请求最多8类受控决策，不含timeout；不将Origin / IP原文、JWT、改写值或完整配置写入策略标签及公开材料。
- 本计划默认不自动commit / push / merge / deploy。每任务产物先保留在工作区并记录限定补丁及测试证据；需要固定Git提交构建时，先提供具体审阅包取得授权，不能把计划审阅当作空白提交授权。

## Review Focus

1. 同路径GET / POST / 业务OPTIONS与预检端点共存：按实际请求方法和Origin选原路由，不遮蔽业务OPTIONS（Task 6）。
2. 发布切换恰逢预检、IP拒绝或认证失败：完整上下文仍属于一次租约，旧代际释放一次且观测保留（Task 5）。
3. 上游Vary: Origin、缓存Hit及不同允许Origin：内容和授权均隔离，未允许的其他Vary仍旁路（Task 8）。
4. 重试、动态Connection声明及缓存短路共同出现：没有重复前缀或信任Header回注，Applied与实际执行一致（Task 7–8）。
5. 部分发布保留已有2.3路由、跨环境空参数、旧实例声明：不能错误降为2.2、默认继承源环境或接受旧实例ACK（Task 4、9）。

## 文件与职责边界

新增业务契约放`src/WebApi.Contracts/Policies/NetworkPolicyContracts.cs`和`TransformPolicyContracts.cs`；新增域校验放`src/WebApi.Domain/Policies/{CorsPolicyConfiguration,IpFilterPolicyConfiguration,TransformPolicyConfiguration}.cs`。校验器仅调度这些小单元，不继续扩展一个巨大switch方法。

Gateway新增`RoutingGenerationMiddleware`只持有代际和原路由上下文；`CompiledIpFilter`只执行已解析IP规则；`CorsPolicyEvaluator`和`CorsPolicyMiddleware`分别负责纯判断、HTTP响应；`TransformPlanner`负责确定性计划，`PolicyRequestTransformProvider`负责后端请求副本，`TransformPolicyMiddleware`负责计划生命周期和最终响应Header。所有数据来自同一RuntimeGeneration，不查询工作配置数据库。

控制台新增三个字段组件和网络 / 变换纯状态模块，复用既有PolicyForm、绑定、Review和晋级导航。独立验收驱动放`scripts/network-policies/`，复用现有运行owner、HTTP客户端、构建和证据解析工具，不修改第2包夹具。

## 验证命令约定

从实施工作区根执行，按需设置`WEBAPI_NODE`与`WEBAPI_PNPM`为实际存在的运行路径；不能假定系统PATH具备Node / pnpm。现有`check-contracts.sh`为每次测试创建owner、独立PostgreSQL / Redis，严格locked-mode构建，限定清理自身资源。

- 域：`./scripts/check-contracts.sh domain --filter 'FullyQualifiedName~测试类'`
- 集成：`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~测试类'`
- Gateway：`./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~测试类'`
- Console：`./scripts/check-console.sh`（真实Node测试加TypeScript / Vite构建）。单个Node测试使用`"$WEBAPI_NODE" --test console/tests/文件.test.mjs`。
- 所有RED / GREEN命令逐次保留退出码、日志和实际计数；编译 / 夹具启动失败不当作预期业务RED。测试失败先诊断，不能登记未经用户批准的例外或改写原预算。

### Task 1：CORS契约、校验与实施起点保护

**Files:** Create `src/WebApi.Contracts/Policies/NetworkPolicyContracts.cs`、`src/WebApi.Domain/Policies/CorsPolicyConfiguration.cs`、`tests/WebApi.Domain.Tests/CorsPolicyConfigurationTests.cs`；Modify `src/WebApi.Domain/Policies/PolicyConfigurationValidator.cs`。

**Interfaces:** `CorsConfiguration(IReadOnlyList<string> AllowedOrigins,bool AllowCredentials,IReadOnlyList<string> AllowedMethods,IReadOnlyList<string> AllowedHeaders,IReadOnlyList<string> ExposedHeaders,int MaxAgeSeconds)`；`PolicyConfigurationValidator.ParseCors(string normalizedConfig) -> CorsConfiguration`；保持`Normalize(string type,string source) -> string`。

- [ ] **Step 1：建立保护记录与RED。** 执行using-git-worktrees，先核对已有附件，创建起点为上述SHA的独立工作区；保存主工作区原文件SHA / Git状态、4192资源只读身份、第2包暂停收据，记录新文档为本轮允许新增。运行原domain / integration / gateway / console基线，再新增`Cors_ExplicitOrigin_NormalizesIdempotently`、`Cors_WildcardCredentials_Rejects`和边界Theory。
  ```csharp
  var normalized = PolicyConfigurationValidator.Normalize("cors", source);
  Assert.Equal(normalized, PolicyConfigurationValidator.Normalize("cors", normalized));
  Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("cors", wildcardWithCredentials));
  ```
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh domain --filter 'FullyQualifiedName~CorsPolicyConfigurationTests'`；要求实际新用例因不支持cors / 缺少解析失败，保留日志。
- [ ] **Step 3：实现契约与校验。** 按规格3.1的完整六字段、数量、token、受保护Header、HTTP origin / 默认端口规范化和通配互斥实现；暴露配置集合为只读副本，禁止输入数组后续修改影响配置。
- [ ] **Step 4：验证GREEN。** 同命令通过，加入64 / 65来源、63 / 64 / 65请求Header、null来源、尾斜线、默认端口重复和32 KiB边界；原PolicyConfigurationTests继续通过。
- [ ] **Step 5：记录限定产物。** 记录文件清单、补丁SHA、基线及红绿结果，不暂存或提交。

### Task 2：IP Filter契约与网络规范化

**Files:** Modify `NetworkPolicyContracts.cs`、`PolicyConfigurationValidator.cs`；Create `src/WebApi.Domain/Policies/IpFilterPolicyConfiguration.cs`、`tests/WebApi.Domain.Tests/IpFilterPolicyConfigurationTests.cs`。

**Interfaces:** `IpFilterMode {AllowList,DenyList}`；`IpFilterConfiguration(IpFilterMode Mode,IReadOnlyList<string> Ranges)`；`ParseIpFilter(string normalizedConfig) -> IpFilterConfiguration`。

- [ ] **Step 1：写RED。** `IpFilter_CidrHostBits_NormalizeToNetwork`断言192.0.2.7/24变192.0.2.0/24；`IpFilter_MappedCidr_NormalizesFamily`断言::ffff:192.0.2.0/120变192.0.2.0/24；覆盖/95拒绝、AllowList空拒绝、DenyList空允许、256 / 257项和重复规范网段拒绝。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh domain --filter 'FullyQualifiedName~IpFilterPolicyConfigurationTests'`，确认失败源于新类型缺口。
- [ ] **Step 3：实现。** 校验mode / ranges、严格地址文本 / CIDR、mapped前缀规则、网络掩码和确定性规范化，不接受DNS、zone ID或非十进制IPv4；IpFilterMode使用类型级JsonStringEnumConverter，拒绝配置中的数字枚举，不更改全局CanonicalJson.Options。
- [ ] **Step 4：验证GREEN。** 同命令通过；组合运行Task 1–2域用例，不能通过放宽原未知字段规则修复。
- [ ] **Step 5：限定补丁与结果入台账。** 不提交；数据保护记录复核。

### Task 3：Transform A契约与严格配置校验

**Files:** Create `src/WebApi.Contracts/Policies/TransformPolicyContracts.cs`、`src/WebApi.Domain/Policies/TransformPolicyConfiguration.cs`、`tests/WebApi.Domain.Tests/TransformPolicyConfigurationTests.cs`；Modify `PolicyConfigurationValidator.cs`。

**Interfaces:** `TransformAction {Set,Remove}`；`HeaderTransformOperation(TransformAction Action,string Name,string? Value)`、`QueryTransformOperation(TransformAction Action,string Name,string? Value)`；`PathTransformConfiguration(string? RemovePrefix,string? AddPrefix)`；`TransformConfiguration(IReadOnlyList<HeaderTransformOperation> RequestHeaders,IReadOnlyList<HeaderTransformOperation> ResponseHeaders,PathTransformConfiguration? Path,IReadOnlyList<QueryTransformOperation> Query)`；`ParseTransform(string normalizedConfig) -> TransformConfiguration`。规范化Remove不输出value，path对象的两个字段含明确null。

- [ ] **Step 1：写RED。** `Transform_SetRemove_FieldShapesAreStrict`断言Remove带value拒绝、Set缺value拒绝；`Transform_ProtectedHeader_Rejects`用两方向覆盖规格受保护名称；`Transform_Path_RejectsAmbiguousPrefix`覆盖点段、百分号、占位符、scheme和反斜线；覆盖空操作、大小 / 条数边界和Query大小写。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh domain --filter 'FullyQualifiedName~TransformPolicyConfigurationTests'`。
- [ ] **Step 3：实现。** 共享Header名称保护规则；只保存明确Set / Remove及字面量前缀 / Query；TransformAction使用类型级字符串枚举转换，Remove的Value使用WhenWritingNull忽略，path的两个nullable字段保留。规范化只读集合并保留操作顺序，拒绝JSON正文 / script等未知字段。
- [ ] **Step 4：验证GREEN。** 同命令通过；以规格的三份完整示例断言Normalize / Parse往返幂等，现有6类配置测试保持。
- [ ] **Step 5：限定补丁与结果入台账。** 不更新产品依赖或锁文件，不提交。

### Task 4：9类绑定、快照2.3和全链路节点能力门禁

**Files:** Modify `src/WebApi.Contracts/Policies/{PolicyContracts,PolicyObservationContracts}.cs`、`src/WebApi.Domain/Policies/PolicyBindingRules.cs`、`src/WebApi.Domain/Runtime/SnapshotValidator.cs`、`src/WebApi.Infrastructure/Releases/{SnapshotCompiler,ReleaseCandidateBuilder,ReleaseService,PublishCoordinator,RollbackService}.cs`、`src/WebApi.Infrastructure/Gateway/SnapshotSchemaCapabilities.cs`、`src/WebApi.Gateway/Workers/NodeClient.cs`、`src/WebApi.Gateway/Policies/TrafficExecutionContext.cs`；Create `tests/WebApi.Domain.Tests/NetworkPolicySnapshotTests.cs`、`tests/WebApi.Integration.Tests/NetworkPolicyCapabilityTests.cs`；Extend existing binding / release / node capability tests。

**Interfaces:** `EffectiveRoutePolicies`尾部增加可选`CorsConfiguration? Cors=null,IpFilterConfiguration? IpFilter=null,TransformConfiguration? Transform=null`；`SnapshotSchemaCapabilities.RequireOnlineSchemaAsync(WebApiDbContext db,PublishSettings settings,Guid environmentId,string schema,CancellationToken ct) -> Task`；原RequireOnline22Async保留薄包装。`PolicyDecisionValues.MaximumPerRequest=8`及新三类Allowed / Valid理由集合在此建立，后续运行任务可合法Record，Task 10再完成输出 / 读取。快照类型字段仍采用现有RuntimePolicy / RuntimePolicyBinding，不新建平行序列化协议。

- [ ] **Step 1：写RED。** `AllNinePolicies_BindAndCompile23`断言9绑定成功 / 10失败、最终schema=2.3；`PartialRelease_RetainedNetworkPolicy_Requires23`保留未选择API的新策略；`OldSchemas_RejectNetworkTypes`逐旧协议拒绝；`CurrentInstanceOnly_MayAck23`覆盖旧实例 / 旧节点；冻结候选后修改工作修订不改变内容Hash。
- [ ] **Step 2：验证RED。** Run domain filter NetworkPolicySnapshotTests，然后integration filter NetworkPolicyCapabilityTests；每条负例须精确断言422 / 409及旧门禁，不接受泛异常。
- [ ] **Step 3：实现。** 绑定解析与聚合字段、基线保留的新策略选择2.3、老版本能力集合、四项声明和InstanceId绑定；所有候选 / 提交 / 发布 / 重试 / 回滚 / ACK调用通用实际目标版本门禁，不只覆盖编译器分支。同步建立规格第6节受控决策类型 / 原因，Record容量用MaximumPerRequest，先为Tasks 5–8建立合法观测接口。
- [ ] **Step 4：验证GREEN。** 上述命令及既有PolicyNodeCapabilityTests / PolicyReleaseTests / AdvancedSnapshotTests通过；校验16 MiB与10000 / 40000限额不放宽。
- [ ] **Step 5：限定补丁与结果入台账。** 读取EF模型差异，记录无新增表或字段的实证；不创建猜测性数据库迁移。

### Task 5：统一上下文租约与IP实际执行

**Files:** Create `src/WebApi.Gateway/Policies/{RoutingGenerationMiddleware,CompiledIpFilter,IpFilterPolicyMiddleware}.cs`、`tests/WebApi.Gateway.Tests/NetworkPolicyGenerationTests.cs`、`IpFilterPipelineTests.cs`；Modify `GatewayApp.cs`、`Configuration/RuntimeGenerationStore.cs`、`Security/ApiKeyMiddleware.cs`、`Policies/TrafficExecutionContext.cs`。

**Interfaces:** `CompiledIpFilter(IpFilterConfiguration configuration).Evaluate(IPAddress? address) -> IpFilterDecision`，`IpFilterDecision(bool Allowed,string? Reason)`；代际增加只读Cors、IpFilters、Transforms字典，分别使用Task 1–3类型；`RoutingGenerationMiddleware.InvokeAsync(HttpContext context,RuntimeGenerationStore store) -> Task`持有唯一lease并Attach原route。保留ApiKeyMiddleware.GenerationItem兼容消费者，认证复用已Attach执行上下文。

- [ ] **Step 1：写RED。** `Generation_EarlyReject_AndSwitch_ReleaseExactlyOnce`在旧请求挂起时切换代际，分别预检 / IP拒绝 / 认证拒绝 / 取消确认旧租约有效且最终回收；`IpFilter_UsesResolvedRemote_NotSpoofedHeader`用可信 / 不可信代理、两地址族、mapped、未知地址及三认证模式证明拒绝后后端次数0。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~NetworkPolicyGenerationTests|FullyQualifiedName~IpFilterPipelineTests'`。
- [ ] **Step 3：实现。** 前置路由 / 代际识别和遥测上下文、Admission ticket释放，保持原索引与认证逻辑；IP使用编译的网络位匹配。拒绝受控403和固定reason，不读取原始Header。
- [ ] **Step 4：验证GREEN。** 同命令及既有ApiKey / JWT / TrustedProxy / PlatformHeaders / RuntimeGeneration相关回归通过；观察IP拒绝仍具有正确API / route / sequence元数据。
- [ ] **Step 5：限定补丁与结果入台账。** 记录租约和拒绝观测证据，不触及第2包Index Build / 性能采样夹具。

### Task 6：CORS纯判断、预检真实匹配与响应所有权

**Files:** Create `src/WebApi.Gateway/Policies/{CorsPolicyEvaluator,CorsPolicyMiddleware}.cs`、`tests/WebApi.Gateway.Tests/{CorsPolicyEvaluatorTests,CorsPipelineTests}.cs`；Modify `Configuration/EnterpriseProxyConfigProvider.cs`、`GatewayApp.cs`、`TrafficExecutionContext.cs`。

**Interfaces:** `CorsPolicyEvaluator.Evaluate(HttpRequest request,RuntimeRoute route,CorsConfiguration configuration) -> CorsEvaluation`；`CorsEvaluation(bool Allowed,bool Preflight,string? Origin,string? Reason)`；`TrafficExecutionContext.Cors`为该请求评估结果，只留内存；`CorsPolicyMiddleware.InvokeAsync(HttpContext context) -> Task`。

- [ ] **Step 1：写RED。** GET-only真实OPTIONS含Origin / RequestedMethod，不带凭证，断言204、backendCalls=0；同路径GET / POST / OPTIONS分别断言被选原RouteId，普通OPTIONS仍转发；非法来源 / 多值 / Header返回403；允许来源下401 / 403 / 429 / 503有CORS且上游伪造Access-Control-*被清理。预检内部条目计数≤2×业务路由。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~CorsPolicyEvaluatorTests|FullyQualifiedName~CorsPipelineTests'`。
- [ ] **Step 3：实现。** 纯校验与HTTP输出分开；每CORS绑定路由最多一个带Origin Exists和RequestedMethod精确值约束的YARP预检条目；回用原route / sequence元数据。OnStarting统一清理 / 输出CORS和合并Vary，不能污染无CORS路由。
- [ ] **Step 4：验证GREEN。** 同命令通过；额外GET-only未匹配方法404 / 405、无Origin、wildcard credentials错误、maxAge0、IP先拒绝和业务OPTIONS不带预检Header测试通过。
- [ ] **Step 5：限定补丁与结果入台账。** 保留各真实HTTP响应及后端计数，不用纯配置测试代替路由匹配。

### Task 7：Transform计划、YARP请求副本和最终响应Header

**Files:** Create `src/WebApi.Gateway/Policies/{TransformPlanner,TransformPolicyMiddleware,PolicyRequestTransformProvider}.cs`、`tests/WebApi.Gateway.Tests/{TransformPlannerTests,TransformPipelineTests}.cs`；Modify `GatewayApp.cs`、`TrafficExecutionContext.cs`；必要时给`Forwarding/GatewayForwardingAdapter.cs`仅添加实际请求应用标记，不替换转发器。

**Interfaces:** `TransformPlanner.Plan(HttpRequest request,TransformConfiguration configuration) -> TransformPlanResult`；`TransformPlanResult(TransformPlan? Plan,int? ErrorStatus,string? Reason)`；`TransformPlan`只读包含后端PathString、QueryString、Header操作及response操作；`TrafficExecutionContext.TransformPlan`为该类型可空属性，`RequestTransformApplied` / `ResponseTransformApplied`为实际应用布尔标记。`PolicyRequestTransformProvider.Configure(TransformBuilderContext context) -> void`注册现有YARP请求变换扩展；`TransformPolicyMiddleware.InvokeAsync(HttpContext context) -> Task`计划与最终响应负责，不变更原request。

- [ ] **Step 1：写RED。** 原/gateway/stock示例断言后端/internal/v2/stock与sku双值 / region，原Request.Path未变；gatewayx前缀不匹配422、歧义编码422、结果16384 / 16385边界；两方向动态Connection业务名保护，控制Header不能回注；申请另一API才有授权的credential不能借改写取得原API访问。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~TransformPlannerTests|FullyQualifiedName~TransformPipelineTests'`。
- [ ] **Step 3：实现。** 计划在认证后 / 缓存前生成；只改写后端副本路径、查询和Header。最终输出仅作用于上游响应 / 缓存响应，跳过动态hop-by-hop，Gateway自身错误不伪造上游响应；统一记录实际Applied / Rejected / Bypass。
- [ ] **Step 4：验证GREEN。** 同命令通过；响应上游503仍有配置业务Header，认证拒绝无业务响应改写；保留流式正文及Content-Length，无JSON正文读取。
- [ ] **Step 5：限定补丁与结果入台账。** 检查实际正常代理和ForwardingAdapter均使用同一YARP Transformer，不建立仅普通代理有效的旁路实现。

### Task 8：CORS / Transform与真实缓存、重试、熔断组合

**Files:** Modify `src/WebApi.Gateway/Policies/{CacheKeyBuilder,CacheEligibility,CacheResponseMiddleware,BoundedResponseCapture,TrafficExecutionContext}.cs`；Create `tests/WebApi.Gateway.Tests/{NetworkPolicyCacheTests,NetworkPolicyRetryTests}.cs`；Extend `tests/WebApi.Gateway.Tests/Support/CachePipelineFixture.cs`与现有真实Forwarding回显夹具。

**Interfaces:** 保持`CacheKeyBuilder.Build(TrafficExecutionContext,HttpRequest,CacheConfiguration) -> CacheLookupKey?`，内部加入Task 6的Origin维度；`CacheEligibility.Response`尾部增加可选`bool originPartitioned=false`，只在绑定CORS且键已隔离Origin时启用。Task 7计划用于每次实际发送；其他Vary和身份条件保持。

- [ ] **Step 1：写RED。** 两个允许Origin对同业务GET分别Miss→Hit，后端正文不同且各自Vary: Origin / CORS不串用；未允许其他Vary旁路；缓存Hit断言response业务Header正确、请求改写only时Bypass、失败计划不取缓存。3次真实503→成功均断言路径前缀只出现一次、同query / header、rate一次、半开探测一次。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~NetworkPolicyCacheTests|FullyQualifiedName~NetworkPolicyRetryTests'`；使用真实Redis与真实HTTP后端计数。
- [ ] **Step 3：实现。** Origin仅内部Key受HMAC哈希不作标签；保持上游Vary和平台Vary所有权；存储仍使用现有白名单，最终响应Header统一执行。保留原重试副本 / 超时 / 熔断 / 不安全重放判断。
- [ ] **Step 4：验证GREEN。** 同命令及既有AdvancedPolicyCompositionTests、缓存身份分区 / Redis失败 / retry测试通过；配置发布与回滚均冷分区，旧在途请求使用旧计划。
- [ ] **Step 5：限定补丁与结果入台账。** 记录cache read / write与物理尝试，不将部署序列隔离宣称为第2包性能收益。

### Task 9：制品策略模板和目标环境显式映射

**Files:** Modify `src/WebApi.Domain/Delivery/ArtifactPolicyTemplate.cs`及仅有实际缺口的`src/WebApi.Infrastructure/Delivery/{PromotionMappingService,PromotionCandidateBuilder,ReleasePromotionService}.cs`；Create `tests/WebApi.Domain.Tests/NetworkPolicyArtifactTests.cs`、`tests/WebApi.Integration.Tests/NetworkPolicyPromotionTests.cs`。

**Interfaces:** 复用`ArtifactPolicyTemplates.Split / Normalize / Resolve`，cors环境字段为allowedOrigins，ip_filter为ranges，transform为[]；其余字段按规格冻结。保留现有Mapping DTO与目标私有策略创建规则，不新建晋级入口。

- [ ] **Step 1：写RED。** Split / Resolve断言目标Origin / CIDR必须出现且类型正确、冻结字段不可改；空targetParameters不能直接继承源值；Transform规则不变；跨项目目标策略、陈旧修订及冻结策略被共享篡改均阻断。
- [ ] **Step 2：验证RED。** Run domain filter NetworkPolicyArtifactTests，再integration filter NetworkPolicyPromotionTests。
- [ ] **Step 3：实现。** 扩展严格环境字段分支和目标配置校验；现有通用目标策略路径无缺口则不改相关服务。晋级预检引用Task 4实际schema门禁。
- [ ] **Step 4：验证GREEN。** 同命令和现有PromotionMapping / Submission / Execution / Concurrency / Credential回归通过；TEST→PROD新策略独立创建，原共享策略修订不变。
- [ ] **Step 5：限定补丁与结果入台账。** 记录是否确实需要服务修改及理由，不为了计划文件名强行修改无缺口代码。

### Task 10：8类受控观测完整输出与读取

**Files:** Modify `src/WebApi.Contracts/Policies/PolicyObservationContracts.cs`、Gateway `Policies/TrafficExecutionContext.cs`与`Observability/{TelemetryAttributes,GatewayTelemetryRecorder,TelemetryMetricExporter,RequestTelemetryMiddleware}.cs`、Infrastructure `Observability/{PolicyObservationProjection,LokiLogSource,TempoTraceSource,PrometheusMetricSource}.cs`；Create `tests/WebApi.Gateway.Tests/NetworkPolicyTelemetryTests.cs`、`tests/WebApi.Integration.Tests/NetworkPolicyObservationTests.cs`。

**Interfaces:** 使用Task 4的`PolicyDecisionValues.Types / Allowed / Valid / MaximumPerRequest`，保持PolicyDecisionDto字段；理由逐项来自规格第6节，不接受任意reason；TrafficExecutionContext.Record容量8且一类型一次。消除TelemetryAttributes.Take(5)丢失新增类型问题，输出改用MaximumPerRequest，不移除容量上限。

- [ ] **Step 1：写RED。** 构造9类绑定8类实际决策，断言输出 / 投影含cors、ip_filter、transform；非法类型 / 理由组合拒绝；旧5类材料仍读；早期IP拒绝只有已执行阶段；秘密canary不出现在日志 / Trace / 标签，固定原因过滤保持可见作用域。
- [ ] **Step 2：验证RED。** Run gateway filter NetworkPolicyTelemetryTests，再integration filter NetworkPolicyObservationTests。
- [ ] **Step 3：实现。** 注册受控类型及原因，同步收集 / 输出 / 读取 / 查询过滤；不把真实Origin / IP / 改写值转换为新字段或Prometheus标签。
- [ ] **Step 4：验证GREEN。** 同命令及既有PolicyTelemetry / AdvancedPolicyTelemetry / PolicyObservation / AdvancedPolicyObservation回归通过。
- [ ] **Step 5：限定补丁与结果入台账。** 后续Task 12还必须验证真实三源数据，当前测试不代替外部观测验收。

### Task 11：三类表单、Review、晋级映射及可操作导航

**Files:** Create `console/src/policies/{CorsPolicyFields,IpFilterPolicyFields,TransformPolicyFields}.tsx`、`console/src/policies/network-policy-state.mjs` / `.d.mts`、`console/tests/network-policy-state.test.mjs`；Modify该policies目录`policy-state.mjs` / `.d.mts`、`PolicyForm.tsx`、`RoutePolicyEditor.tsx`、`PolicyReleaseReview.tsx`、`advanced-policy-state.mjs` / `.d.mts`、`console/src/pages/{Policies,PolicyEditor,AccessLogs,Traces,MetricsOverview}.tsx`、`console/src/delivery/{PromotionWizard,PromotionDiff}.tsx`、`console/src/delivery/delivery-state.mjs` / `.d.mts`；Extend现有policy / promotion / review / observability Node测试。

**Interfaces:** `initialNetworkPolicyDraft(type:'cors'|'ip_filter'|'transform') -> object`；`validateNetworkPolicyDraft(draft) -> string|null`；`networkPolicyReview(type,config) -> object`；三个组件均接收`{config:any,change:(config:any)=>void,disabled:boolean}`，与既有CachePolicyFields受控字段一致。服务端规范化仍为最终权威，不新增重复管理API。

- [ ] **Step 1：写RED。** 新类型可初始化 / 校验 / 保存，Header受保护名称和范围错误就地提示，9绑定与schemaReview可读；初始不填通配来源或继承晋级Origin / CIDR；作用域late-response、readonly、412、dirty导航保持；响应预览只标静态示例。
- [ ] **Step 2：验证RED。** Run `"$WEBAPI_NODE" --test console/tests/network-policy-state.test.mjs`及相邻现有测试，失败须源于新状态缺口。
- [ ] **Step 3：实现。** 小字段组件接入既有表单，networkPolicyReview接入发布Review；PromotionWizard按环境字段提供明确数组输入 / 目标策略选择。保护ETag / 幂等 / scope epoch，不将保存展示为运行生效；观测过滤增加受控决策展示。
- [ ] **Step 4：验证GREEN。** Run `./scripts/check-console.sh`全部Node用例与TS / Vite通过；当前test计数在日志实读，不预填旧计数。
- [ ] **Step 5：1440px交互QA准备。** 使用独立Console与服务owner，按新增 / 编辑 / 校验 / 复制 / 9绑定 / 发布Review / 晋级 / 日志Trace逐动作保留截图，无法运行的动作标未执行，不编造成功状态。

### Task 12：独立双网关正式场景驱动与严格证据门禁

**Files:** Create `scripts/network-policies/{scenario,evidence,acceptance}.mjs`、`tests/network-policies/{scenario,evidence}.test.mjs`、`tests/WebApi.EndToEnd.Tests/NetworkPolicyLoopTests.cs`；Modify现有`scripts/gateway-policies/fixtures/Program.cs`仅加回显 / 次数 / Origin Vary路径（现有行为不改）；Create `scripts/network-policies/fixtures.mjs`负责调用现有owner / runtime helpers，不复制清理逻辑。

**Interfaces:** `runNetworkPolicyScenario({revision,imageId,cloneDirectory,fixtureDirectory}) -> Promise<ScenarioResult>`；`validateNetworkPolicyEvidence(proof,files,{revision,imageId,privateValues}) -> AcceptanceResult`；`runNetworkPolicyAcceptance({revision,imageId,directory,cloneDirectory,privateValues}) -> Promise<AcceptanceResult>`。E2E trait `Scenario=NetworkPolicies`，私有材料目录环境变量`WEBAPI_NETWORK_POLICY_RESULT_DIRECTORY`。

- [ ] **Step 1：写RED。** 证据测试删除任一P3-01–12材料、伪造actual、错误source / image / owner、仅1节点ACK、旧Hash被改写、失败 / skip统计、秘密canary、错误HTTP / 后端次数均拒绝；fixture新增路径先做实请求红绿验证，不以假executor判通过。
- [ ] **Step 2：验证RED。** Run `"$WEBAPI_NODE" --test tests/network-policies/*.test.mjs`；E2E未提供材料必须失败，不默认skip。
- [ ] **Step 3：实现。** 复用现有ContractClient、runtime owner / fixed构建 / UUID夹具和三源观测。由initializeRuntime在全新独立owner内初始化空数据库并创建本轮业务数据，configureRuntime绑定本owner环境；cloneDirectory仅沿用驱动参数名，不读取或恢复4192数据库。场景包括全9类、两网关运行、旧协议阻断、冻结修订、三认证、预检0后端调用、3次重试、不同Origin缓存、在途代际、2.0 / 2.1 / 2.2与2.3历史回滚、TEST / PROD审批晋级、当前实例ACK和实际观测。公开材料读取内部身份 / 数量 / Hash验证，不信任wrapper标签；不复用旧验收器中要求4192冷恢复的保护流程。
- [ ] **Step 4：跑工作区完整回归与初步真实验收。** Run所有domain / integration / gateway、check-console、现有runtime / gateway-policies与新增network-policies Node测试；逐suite失败 / 跳过0。GatewayFixture实际双进程 / PG / Redis回归可直接进行，保留为功能回归证据，不能冒充固定镜像正式验收。
- [ ] **Step 5：准备固定版本审阅包。** 汇总精确产品 / 测试 / 驱动 / 文档文件、补丁SHA、父SHA、秘密扫描与所有日志。按Global Constraints取得该具体快照的本地提交授权后，使用现有fixed-source构建工具构建同一imageId的控制面、Worker、Console与双Gateway；未获授权则保留审阅包，不自建提交或绕过固定构建门禁。
- [ ] **Step 6：运行固定镜像正式验收。** 本地快照获授权且固定构建成功后，在独立owner执行scenario / acceptance；调用`node scripts/network-policies/acceptance.mjs <revision> <imageId> <directory> <cloneDirectory>`。E2E在已挂载证据目录的测试SDK中运行`dotnet test tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj -c Release --filter 'Scenario=NetworkPolicies'`，环境变量指向该目录。要求两节点同版本 / 序列 / Hash和ACK、P3-01–12实际证据齐全、真实日志 / Trace / 指标可读；失败保留材料，按新固定快照完整复验。
- [ ] **Step 7：1440px实际页面QA。** 使用CUA访问仅本owner独立端口，动作与Task 11一一对照；截图与动作记录绑定revision / imageId，无法确认的源或页面不得标actual。仅清理本owner诊断外可删除资源，保留失败与已承诺数据，不操作4192和第2包owner。

### Task 13：最终审查、覆盖台账与交付

**Files:** Create `docs/evidence/network-policies/<run-id>/`及`docs/deployment/network-policies-runbook.md`；Modify独立工作区`docs/{acceptance,console-coverage}.md`，回主工作区时只合入审阅过的对应补丁，不覆盖其原改动。

**Interfaces:** 台账每行使用`requirementId,sourceIdentity,testLog,evidencePaths,status,remainingGate`；运行手册覆盖三类录入、绑定、审批、节点2.3能力、观测、历史回滚和目标环境参数。sourceIdentity为已授权固定SHA或尚未固定的父SHA+补丁SHA，二者明确区分。

- [ ] **Step 1：复核最新源与材料。** 确认最终实现文件与固定验收来源一致，真实计数、失败及复验保留；P3-01–12不缺项，所有承诺均可追溯。
- [ ] **Step 2：完整分支独立审查。** 使用requesting-code-review技能派出一个只读最终审查，范围仅本包精确差异与证据，检查规格遵循、Header信任、原API授权、缓存 / 重试、能力门禁、历史兼容和作用域；这不是逐任务子代理开发。遇到反馈用receiving-code-review核实后修复。
- [ ] **Step 3：必要复验。** 修复后的有关红绿 / 回归再跑；若改变已固定镜像源码，则另提供精确提交包、重新固定构建并复验正式场景，不能复用旧镜像通过证明新代码。
- [ ] **Step 4：保护与交付。** 逐文件复核主工作区原内容 / 状态、4192数据身份及第2包暂停态，报告最终功能 / 验收 / 本地快照 / 未执行部署边界。提交、main合并、远端推送、4192升级各按当时明确有效授权执行，不从“继续”推断部署许可。

## 规格覆盖与计划自检

| 规格章节 / 验收 | 实施任务 |
| --- | --- |
| 范围、组织 / 权限、原数据保护 | 1、4、9、11–13 |
| CORS配置 / 执行 / 预检 | 1、4、6、8、10–12 |
| IP配置 / 可信代理 / 实际拒绝 | 2、4、5、10–12 |
| Transform A配置 / 后端副本 / 响应 | 3、4、7–8、10–12 |
| 快照2.3 / 旧历史 / ACK / 回滚 | 4–5、12–13 |
| 缓存 / 重试 / 熔断 / 代际 | 5、7–8、12 |
| 显式目标环境参数 / 冻结制品 | 9、11–12 |
| 8类观测 / 真三源 / UI / QA | 10–13 |
| P3-01–P3-12逐项证据与最终审查 | 12–13 |

接口类型来自Task 1–3并在后续任务复用；可选扩展保持已有调用兼容。每项RED、实现和GREEN均有具体对象 / 命令，任务产物不自动提交。计划批准后先完成基线和Tasks 1–11，再推进Task 12驱动、固定快照授权与正式验收，最后统一审查交付。
