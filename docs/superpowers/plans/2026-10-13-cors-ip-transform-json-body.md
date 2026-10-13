# CORS / IP Filter / Transform with JSON Body Mapping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. 用户已选当前会话逐项实现、测试，完成后统一审查；不改成逐任务子代理执行。Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成第3包三类业务策略的管理、发布、双网关执行、观测、历史兼容、跨环境晋级及回滚闭环。

**Architecture:** 复用既有Policy、源修订、冻结候选和审批发布引擎，扩展快照2.3及目标节点能力门禁。将运行代际租约前置到IP / CORS之前，在原API授权后建立静态Transform计划；首次 / 重试的后端副本和缓存响应使用统一执行规则。JSON请求 / 最终响应使用域层同一映射器；网关有界编码处理与租约、管理端预览复用规则，缓存只存最终表示。

**Tech Stack:** 现有锁定.NET 10、YARP 2.3.0、EF / Npgsql / PostgreSQL、Redis、OpenTelemetry、React 19.2、TypeScript、Vite、xUnit、Node原生测试；不增加产品依赖，不调整锁定版本。

**Spec:** `docs/superpowers/specs/2026-10-13-cors-ip-transform-json-body-design.md`。用户已批准含JSON正文映射的扩展书面规格，授权编写本计划；本计划待审阅。执行方法已选当前会话逐项实现 / 测试，完成后统一审查。

## Global Constraints

- 起点固定`293b37f2dbd59427cd2d40f6783dc441345528ef`；第2包保持暂停，原性能窗口、owner资源和证据均不修改或续跑。
- 保留主工作区原有文件内容和Git状态；4192账号、数据库和部署不作为测试夹具。实施使用独立受管工作区，资源按UUID / owner隔离。
- 新三类配置最大32768 UTF-8字节、最大JSON深度16；严格拒绝未知 / 重复字段；规范化幂等；绑定每类型一个、总数最多9、Priority 0–1000。
- Origin最多64项 / 512字符；CORS请求Header最多64项、暴露Header最多32项；maxAgeSeconds 0–86400。IP网络最多256项；仅使用既有可信代理边界解析后的RemoteIpAddress。
- Header变换每方向最多32项；Query变换最多32项；名称限制及所有受保护Header、路径编码、移除前缀匹配、16384字节结果预算严格按规格第3节。
- JSON每方向1–32操作，路径 / from≤512字符 / 32层，value≤8192 UTF-8字节；正文默认262144、1024–1048576字节、深度32，input编码 / decoded / 每步及输出均限额；单层identity / gzip / br。部署默认最大1048576、并发16（1–64），不能使用无界任务队列。
- 请求正文仅选所配方法；最终响应仅选所配状态。HEAD / 条件 / Range / gRPC / SSE / trailers / 签名及失败状态按规格3.4；POST等请求不会因正文缓冲变为可自动重试，缓存Hit不再次执行JSON映射。
- 保持16 MiB快照、10000业务路由、40000运行策略、节点协议能力最多4项、最多3次物理重试；预检内部条目总量最多2倍业务路由数。
- 支持历史2.0 / 2.1 / 2.2；新三类启用绑定需要2.3；保存工作配置不改变运行配置，全部目标节点门禁和当前实例ACK保持。
- 每请求最多8类受控决策，不含timeout；不将Origin / IP原文、JWT、改写值或完整配置写入策略标签及公开材料。
- 本计划默认不自动commit / push / merge / deploy。每任务产物先保留在工作区并记录限定补丁及测试证据；需要固定Git提交构建时，先提供具体审阅包取得授权，不能把计划审阅当作空白提交授权。

## Review Focus

1. 同路径GET / POST / 普通OPTIONS与预检、代际切换共存：原API不变、租约在早期拒绝也释放一次（Task 7–8）。
2. JSON空值 / 大数 / 转义字段 / 数组索引及第二步失败：不丢精度、不混淆null和缺失、不输出部分结果（Task 3–4）。
3. 未知长度、解压膨胀、慢读取 / 取消及并发租约：每级限额且配额回收，预览也不形成无界CPU队列（Task 5、10–11）。
4. 不同Origin缓存、重试中间响应与最终JSON：授权 / 表示隔离，只映射一次，失败不会成为上游200成功（Task 9–12）。
5. 部分发布保留2.3、目标Origin / CIDR空参数及旧InstanceId：不降协议、不默认继承、不接受旧实例ACK（Task 6、13）。

## 文件与职责边界

新增业务契约放`src/WebApi.Contracts/Policies/NetworkPolicyContracts.cs`和`TransformPolicyContracts.cs`；新增域校验放`src/WebApi.Domain/Policies/{CorsPolicyConfiguration,IpFilterPolicyConfiguration,TransformPolicyConfiguration}.cs`。校验器仅调度这些小单元，不继续扩展一个巨大switch方法。

Gateway新增`RoutingGenerationMiddleware`只持有代际和原路由上下文；`CompiledIpFilter`只执行已解析IP规则；`CorsPolicyEvaluator`和`CorsPolicyMiddleware`分别负责纯判断、HTTP响应；`TransformPlanner`负责确定性计划，`PolicyRequestTransformProvider`负责后端请求副本，`TransformPolicyMiddleware`负责计划生命周期和最终响应Header。所有数据来自同一RuntimeGeneration，不查询工作配置数据库。JSON读取 / 编码与域层映射分离，request / response使用同一BodyTransformBudget.Lease；ControlPlane预览在独立配额内复用同一域引擎。

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

### Task 3：Transform完整契约、JSON操作配置与严格校验

**Files:** Create `src/WebApi.Contracts/Policies/TransformPolicyContracts.cs`、`src/WebApi.Domain/Policies/{TransformPolicyConfiguration,JsonBodyMappingConfiguration,JsonPointerPath}.cs`、`tests/WebApi.Domain.Tests/{TransformPolicyConfigurationTests,JsonBodyConfigurationTests}.cs`；Modify `PolicyConfigurationValidator.cs`。

**Interfaces:** `TransformAction {Set,Remove}`；`HeaderTransformOperation(TransformAction Action,string Name,string? Value)`、`QueryTransformOperation(TransformAction Action,string Name,string? Value)`；`PathTransformConfiguration(string? RemovePrefix,string? AddPrefix)`；`TransformConfiguration(IReadOnlyList<HeaderTransformOperation> RequestHeaders,IReadOnlyList<HeaderTransformOperation> ResponseHeaders,PathTransformConfiguration? Path,IReadOnlyList<QueryTransformOperation> Query,RequestJsonBodyConfiguration? RequestBody,ResponseJsonBodyConfiguration? ResponseBody)`；`ParseTransform(string normalizedConfig) -> TransformConfiguration`。规范化Remove不输出value，path对象的两个字段含明确null；顶层六字段均必填，关闭正文方向必须显式null。JsonBodyOperation / RequestJsonBodyConfiguration / ResponseJsonBodyConfiguration和JsonPointerPath接口见下方附加契约。

- [ ] **Step 1：写RED。** `Transform_SetRemove_FieldShapesAreStrict`断言Remove带value拒绝、Set缺value拒绝；`Transform_ProtectedHeader_Rejects`用两方向覆盖规格受保护名称；`Transform_Path_RejectsAmbiguousPrefix`覆盖点段、百分号、占位符、scheme和反斜线；覆盖空操作、大小 / 条数边界和Query大小写。JsonBodyConfigurationTests断言32操作接受 / 33拒绝、value缺失拒绝 / 明确null接受、正文1024与1048576接受 / 1023与1048577拒绝；拒绝缺失六字段、错误op字段组合、move到自己的子路径、非法Pointer、204 / 205 / 206 / 304、请求方向方法HEAD和value超过8192字节。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh domain --filter 'FullyQualifiedName~TransformPolicyConfigurationTests|FullyQualifiedName~JsonBodyConfigurationTests'`。
- [ ] **Step 3：实现。** 共享Header名称保护规则；只保存明确Set / Remove及字面量前缀 / Query；TransformAction使用类型级字符串枚举转换，Remove的Value使用WhenWritingNull忽略，path的两个nullable字段保留。规范化只读集合并保留操作顺序；解析requestBody / responseBody并深复制JsonElement常量，区分缺失value与JsonElement.Null；拒绝script / 未支持op和未知字段。
- [ ] **Step 4：验证GREEN。** 同命令通过；以规格的四份完整示例断言Normalize / Parse往返幂等，现有6类配置测试保持。
- [ ] **Step 5：限定补丁与结果入台账。** 不更新产品依赖或锁文件，不提交。

**Task 3附加契约：** `JsonBodyOperation(string Op,string Path,string? From,JsonElement? Value)`，From / Value仅对应op允许时输出，JsonElement.Null与不存在的nullable Value区别保留；`RequestJsonBodyConfiguration(IReadOnlyList<string> Methods,int MaxBodyBytes,IReadOnlyList<JsonBodyOperation> Operations)`；`ResponseJsonBodyConfiguration(IReadOnlyList<int> StatusCodes,int MaxBodyBytes,IReadOnlyList<JsonBodyOperation> Operations)`；`JsonPointerPath.Parse(string pointer) -> IReadOnlyList<string>`。Task 3配置校验只确认语法与限额，Task 4负责实际数据存在 / 类型语义。

### Task 4：共享JSON Pointer / 字段映射执行器

**Files:** Create `src/WebApi.Domain/Policies/{JsonBodyMapper,CompiledJsonBodyMapping}.cs`、`tests/WebApi.Domain.Tests/JsonBodyMapperTests.cs`；Consumes Task 3的JsonPointerPath与JSON操作契约。

**Interfaces:** `JsonBodyMapper.Compile(IReadOnlyList<JsonBodyOperation> operations) -> CompiledJsonBodyMapping`；`JsonBodyMapper.Apply(ReadOnlyMemory<byte> utf8,CompiledJsonBodyMapping mapping,int maxBytes,CancellationToken ct) -> JsonBodyMappingResult`；`JsonBodyMappingResult(byte[]? Utf8,string? Reason,int? OperationIndex)`，成功Utf8非null，失败Reason固定、OperationIndex为1–32或null。CompiledJsonBodyMapping不可变，常量复制，执行不修改共享配置。

- [ ] **Step 1：写RED。** `JsonBody_MoveAndRemove_ProducesExpectedFields`使用规格输入，断言customer_id=123 / source=webapi与result.stock=10且debug不存在；`JsonBody_CopyDoesNotAliasSource`复制后修改副本不污染原节点；覆盖根、数组插入 / 移动 / -追加、~01、百分号字面属性、null、9007199254740993及精确小数，确保数字未转double。
  ```csharp
  Assert.Equal("9007199254740993", numberElement.GetRawText());
  Assert.Null(failed.Utf8);
  Assert.Equal("transform_json_operation_failed", failed.Reason);
  Assert.Equal(2, failed.OperationIndex);
  ```
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh domain --filter 'FullyQualifiedName~JsonBodyMapperTests'`，记录新执行器缺失的实际失败。
- [ ] **Step 3：实现。** 使用现有System.Text.Json完成有界UTF-8 / 重复属性 / 代理字符 / 32深度校验与值保真，按五种操作的精确语义执行于私有文档。每步及最终输出均有maxBytes检查，取消在读取、操作循环和输出前检查；错误不返回原文、JsonException.Path / Message或动态Pointer。
- [ ] **Step 4：验证GREEN。** 同命令通过；新增第二条操作失败原输入字节仍相同、UTF-8 BOM、尾随数据、非法Unicode、深度32 / 33、复制导致中间超过预算、32操作后取消和并行应用同一CompiledMapping互不污染。
- [ ] **Step 5：记录限定补丁和结果。** 保留失败样本为私有材料，只公开受控错误与用例ID，不新增依赖、不提交。

### Task 5：部署正文限额、策略管理和同引擎预览接口

**Files:** Create `src/WebApi.Infrastructure/Policies/{BodyTransformBudget,TransformPreviewService}.cs`、`src/WebApi.ControlPlane/Policies/TransformPreviewRequestReader.cs`、`tests/WebApi.Integration.Tests/{TransformPreviewTests,TransformPolicyManagementTests}.cs`；Modify `src/WebApi.Contracts/Policies/{TransformPolicyContracts,AdvancedPolicyContracts}.cs`、`src/WebApi.Infrastructure/Policies/{GatewayPolicyDeploymentRules,PolicyService}.cs`、`src/WebApi.ControlPlane/{ControlPlaneApp.cs,Policies/PolicyEndpoints.cs}`、`src/WebApi.Infrastructure/Policies/RoutePolicyService.cs`（仅部署限额校验缺口）。

**Interfaces:** `PolicyLimitsDto`尾部增加默认值1048576的`int TransformMaxBodyBytes`，保留原两字段调用；`TransformPreviewRequest(PolicyScopeRequest Scope,string Config,string Direction,string SampleJson)`；`TransformPreviewDto(string OutputJson,int OperationCount,string Kind="JsonMappingPreview",bool ActualForwarding=false)`；`TransformPreviewService.PreviewAsync(TransformPreviewRequest request,ActorContext actor,CancellationToken ct) -> Task<TransformPreviewDto>`；`TransformPreviewRequestReader.ReadAsync(HttpRequest request,int maxBodyBytes,CancellationToken ct) -> Task<TransformPreviewRequest>`。`BodyTransformBudget(int maxConcurrent=16).TryAcquire(out Lease? lease) -> bool`，Lease幂等Dispose；1–64合法，不建等待队列。GatewayPolicyDeploymentRules增加可选正文max参数，在Read中读取GatewayPolicies:Transform:MaxBodyBytes，Validate检查两个方向策略限额。

- [ ] **Step 1：写RED。** PreviewTest断言policy.read只读授权可预览、跨组织404 / 403、CSRF拒绝、未知direction422、数据不写Policy / Audit详情、no-store；回传OutputJson为字符串且大数准确；Config / Sample中秘密canary不进入日志。管理用例覆盖新三类筛选 / 复制 / ETag / 9绑定，超过本部署正文上限拒绝；Budget第17租约拒绝且释放后恢复。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~TransformPreviewTests|FullyQualifiedName~TransformPolicyManagementTests'`，精确断言接口缺口与限额错误。
- [ ] **Step 3：实现。** preview放现有RequireAuthorization / CSRF管理路由组，调用PolicyAccess.ResolveAsync / CanReadAsync并复用Task 4；同一作用域规则不因预览变更。端点采用HttpContext入参，由RequestReader在DTO反序列化前有界读取：envelope预算为6×部署MaxBodyBytes+65536，同时不放宽现有请求大小限制；严格检查scope / 必填字符串 / direction / 重复属性，缺字段和空Guid返回原invalid_request契约，不能因绕过DTO参数绑定出现500。租约在读取前取得并覆盖预览执行与异步JSON输出，SampleJson解码后再受方向预算。输出设置no-store，有限错误、操作序号；不使用AuditedCommandExecutor存入样例。扩展PolicyService.ListAsync原六类过滤，保存 / 绑定 / 校验 / 复制 / 发布保留既有作用域与部署限额。
- [ ] **Step 4：验证GREEN。** 同命令及PolicyManagementTests / RoutePolicyBindingTests通过；预览无转发、无变更、配额耗尽503、超wire预算413、方向关闭拒绝，调用Task 4所得结果一致；读取恢复和取消不泄漏租约。
- [ ] **Step 5：记录限定补丁和结果。** 验证旧CacheLimits字段与配置仍有效、原6类行为不变，更新精确文件清单，不提交。

### Task 6：9类绑定、快照2.3和全链路节点能力门禁

**Files:** Modify `src/WebApi.Contracts/Policies/{PolicyContracts,PolicyObservationContracts}.cs`、`src/WebApi.Domain/Policies/PolicyBindingRules.cs`、`src/WebApi.Domain/Runtime/SnapshotValidator.cs`、`src/WebApi.Infrastructure/Releases/{SnapshotCompiler,ReleaseCandidateBuilder,ReleaseService,PublishCoordinator,RollbackService}.cs`、`src/WebApi.Infrastructure/Gateway/SnapshotSchemaCapabilities.cs`、`src/WebApi.Gateway/Workers/NodeClient.cs`、`src/WebApi.Gateway/Policies/TrafficExecutionContext.cs`；Create `tests/WebApi.Domain.Tests/NetworkPolicySnapshotTests.cs`、`tests/WebApi.Integration.Tests/NetworkPolicyCapabilityTests.cs`；Extend existing binding / release / node capability tests。

**Interfaces:** `EffectiveRoutePolicies`尾部增加可选`CorsConfiguration? Cors=null,IpFilterConfiguration? IpFilter=null,TransformConfiguration? Transform=null`；`SnapshotSchemaCapabilities.RequireOnlineSchemaAsync(WebApiDbContext db,PublishSettings settings,Guid environmentId,string schema,CancellationToken ct) -> Task`；原RequireOnline22Async保留薄包装。`PolicyDecisionValues.MaximumPerRequest=8`、新三类Allowed / Valid全部正文理由及retry的Bypass理由response_transform_rejected在此建立，后续运行任务可合法Record，Task 14再完成输出 / 读取。快照类型字段仍采用现有RuntimePolicy / RuntimePolicyBinding，不新建平行序列化协议。

- [ ] **Step 1：写RED。** `AllNinePolicies_BindAndCompile23`断言9绑定成功 / 10失败、最终schema=2.3；`PartialRelease_RetainedNetworkPolicy_Requires23`保留未选择API的新策略；`OldSchemas_RejectNetworkTypes`逐旧协议拒绝；`CurrentInstanceOnly_MayAck23`覆盖旧实例 / 旧节点；冻结候选后修改工作修订不改变内容Hash。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh domain --filter 'FullyQualifiedName~NetworkPolicySnapshotTests'`，然后`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~NetworkPolicyCapabilityTests'`；每条负例须精确断言422 / 409及旧门禁，不接受泛异常。
- [ ] **Step 3：实现。** 绑定解析与聚合字段、基线保留的新策略选择2.3、老版本能力集合、四项声明和InstanceId绑定；所有候选 / 提交 / 发布 / 重试 / 回滚 / ACK调用通用实际目标版本门禁，不只覆盖编译器分支。同步建立规格第6节受控决策类型 / 原因，Record容量用MaximumPerRequest，先为Tasks 7–12建立合法观测接口。
- [ ] **Step 4：验证GREEN。** 上述命令及既有PolicyNodeCapabilityTests / PolicyReleaseTests / AdvancedSnapshotTests通过；校验16 MiB与10000 / 40000限额不放宽。
- [ ] **Step 5：限定补丁与结果入台账。** 读取EF模型差异，记录无新增表或字段的实证；不创建猜测性数据库迁移。

### Task 7：统一上下文租约与IP实际执行

**Files:** Create `src/WebApi.Gateway/Policies/{RoutingGenerationMiddleware,CompiledIpFilter,IpFilterPolicyMiddleware}.cs`、`tests/WebApi.Gateway.Tests/NetworkPolicyGenerationTests.cs`、`IpFilterPipelineTests.cs`；Modify `GatewayApp.cs`、`Configuration/RuntimeGenerationStore.cs`、`Security/ApiKeyMiddleware.cs`、`Policies/TrafficExecutionContext.cs`。

**Interfaces:** `CompiledIpFilter(IpFilterConfiguration configuration).Evaluate(IPAddress? address) -> IpFilterDecision`，`IpFilterDecision(bool Allowed,string? Reason)`；代际增加只读Cors、IpFilters、Transforms字典，分别使用Task 1–3类型，以及`IReadOnlyDictionary<Guid,CompiledJsonBodyMapping> RequestBodyMappings / ResponseBodyMappings`，使用Task 4在代际创建时编译各启用正文配置；`RoutingGenerationMiddleware.InvokeAsync(HttpContext context,RuntimeGenerationStore store) -> Task`持有唯一lease并Attach原route。保留ApiKeyMiddleware.GenerationItem兼容消费者，认证复用已Attach执行上下文。

- [ ] **Step 1：写RED。** `Generation_EarlyReject_AndSwitch_ReleaseExactlyOnce`在旧请求挂起时切换代际，用通用next短路204、IP拒绝、认证拒绝、取消确认旧租约有效且最终回收；204仅为本任务的租约单元边界，真实CORS预检由Task 8验证。`IpFilter_UsesResolvedRemote_NotSpoofedHeader`用可信 / 不可信代理、两地址族、mapped、未知地址及三认证模式证明拒绝后后端次数0。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~NetworkPolicyGenerationTests|FullyQualifiedName~IpFilterPipelineTests'`。
- [ ] **Step 3：实现。** 前置路由 / 代际识别和遥测上下文、Admission ticket释放，保持原索引与认证逻辑；IP使用编译的网络位匹配；JSON映射按运行PolicyId预编译到两个代际字典，请求阶段不反复解析策略。拒绝受控403和固定reason，不读取原始Header。
- [ ] **Step 4：验证GREEN。** 同命令及既有ApiKey / JWT / TrustedProxy / PlatformHeaders / RuntimeGeneration相关回归通过；观察IP拒绝仍具有正确API / route / sequence元数据。
- [ ] **Step 5：限定补丁与结果入台账。** 记录租约和拒绝观测证据，不触及第2包Index Build / 性能采样夹具。

### Task 8：CORS纯判断、预检真实匹配与响应所有权

**Files:** Create `src/WebApi.Gateway/Policies/{CorsPolicyEvaluator,CorsPolicyMiddleware}.cs`、`tests/WebApi.Gateway.Tests/{CorsPolicyEvaluatorTests,CorsPipelineTests}.cs`；Modify `Configuration/EnterpriseProxyConfigProvider.cs`、`GatewayApp.cs`、`TrafficExecutionContext.cs`。

**Interfaces:** `CorsPolicyEvaluator.Evaluate(HttpRequest request,RuntimeRoute route,CorsConfiguration configuration) -> CorsEvaluation`；`CorsEvaluation(bool Allowed,bool Preflight,string? Origin,string? Reason)`；`TrafficExecutionContext.Cors`为该请求评估结果，只留内存；`CorsPolicyMiddleware.InvokeAsync(HttpContext context) -> Task`。

- [ ] **Step 1：写RED。** GET-only真实OPTIONS含Origin / RequestedMethod，不带凭证，断言204、backendCalls=0；同路径GET / POST / OPTIONS分别断言被选原RouteId，普通OPTIONS仍转发；非法来源 / 多值 / Header返回403；允许来源下401 / 403 / 429 / 503有CORS且上游伪造Access-Control-*被清理。预检内部条目计数≤2×业务路由。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~CorsPolicyEvaluatorTests|FullyQualifiedName~CorsPipelineTests'`。
- [ ] **Step 3：实现。** 纯校验与HTTP输出分开；每CORS绑定路由最多一个带Origin Exists和RequestedMethod精确值约束的YARP预检条目；回用原route / sequence元数据。OnStarting统一清理 / 输出CORS和合并Vary，不能污染无CORS路由。
- [ ] **Step 4：验证GREEN。** 同命令通过；额外GET-only未匹配方法404 / 405、无Origin、wildcard credentials错误、maxAge0、IP先拒绝和业务OPTIONS不带预检Header测试通过；真实预检挂起与发布切换证明Task 7租约边界在实际CORS短路中有效。
- [ ] **Step 5：限定补丁与结果入台账。** 保留各真实HTTP响应及后端计数，不用纯配置测试代替路由匹配。

### Task 9：Transform计划、YARP请求副本和最终响应Header

**Files:** Create `src/WebApi.Gateway/Policies/{TransformPlanner,TransformPolicyMiddleware,PolicyRequestTransformProvider}.cs`、`tests/WebApi.Gateway.Tests/{TransformPlannerTests,TransformPipelineTests}.cs`；Modify `GatewayApp.cs`、`TrafficExecutionContext.cs`；必要时给`Forwarding/GatewayForwardingAdapter.cs`仅添加实际请求应用标记，不替换转发器。

**Interfaces:** `TransformPlanner.Plan(HttpRequest request,TransformConfiguration configuration) -> TransformPlanResult`；`TransformPlanResult(TransformPlan? Plan,int? ErrorStatus,string? Reason)`；`TransformPlan`只读包含后端PathString、QueryString、Header操作及response操作；`TrafficExecutionContext.TransformPlan`为该类型可空属性，`RequestTransformApplied` / `ResponseTransformApplied`为实际应用布尔标记。`PolicyRequestTransformProvider.Configure(TransformBuilderContext context) -> void`注册现有YARP请求变换扩展；`TransformPolicyMiddleware.InvokeAsync(HttpContext context) -> Task`计划与最终响应负责，不变更原request。

- [ ] **Step 1：写RED。** 原/gateway/stock示例断言后端/internal/v2/stock与sku双值 / region，原Request.Path未变；gatewayx前缀不匹配422、歧义编码422、结果16384 / 16385边界；两方向动态Connection业务名保护，控制Header不能回注；申请另一API才有授权的credential不能借改写取得原API访问。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~TransformPlannerTests|FullyQualifiedName~TransformPipelineTests'`。
- [ ] **Step 3：实现。** 计划在认证后 / 缓存前生成；只改写后端副本路径、查询和Header。最终输出仅作用于上游响应 / 缓存响应，跳过动态hop-by-hop，Gateway自身错误不伪造上游响应；统一记录实际Applied / Rejected / Bypass。
- [ ] **Step 4：验证GREEN。** 同命令通过；响应上游503仍有配置业务Header，认证拒绝无业务响应改写；保留流式正文及Content-Length，无JSON正文读取。
- [ ] **Step 5：限定补丁与结果入台账。** 检查实际正常代理和ForwardingAdapter均使用同一YARP Transformer，不建立仅普通代理有效的旁路实现。

### Task 10：有界请求正文、解压、配额与后端请求副本

**Files:** Create `src/WebApi.Gateway/Policies/{GatewayBodyTransformSettings,BoundedJsonBodyCodec,JsonBodyRequestMiddleware}.cs`、`tests/WebApi.Gateway.Tests/{JsonBodyCodecTests,JsonRequestBodyPipelineTests,BodyTransformBudgetTests}.cs`；Modify `GatewayApp.cs`、`Policies/{TrafficExecutionContext,PolicyRequestTransformProvider}.cs`、`Configuration/SnapshotActivation.cs`。

**Interfaces:** `GatewayBodyTransformSettings.Read(IConfiguration) -> GatewayBodyTransformSettings`与`Validate(RuntimeSnapshot snapshot) -> void`；`BoundedJsonBodyCodec.ReadAsync(Stream source,string? contentEncoding,int maxBytes,CancellationToken ct) -> ValueTask<JsonBodyReadResult>`；`JsonBodyReadResult(byte[]? Utf8,string? Reason)`，input / decoded分别限额。`BodyTransformFailure(int Status,string Reason,int? OperationIndex)`；Execution增加`byte[]? TransformedRequestBody`、`BodyTransformBudget.Lease? BodyLease`、`BodyTransformFailure? TransformFailure`、`bool RequestBodyApplied`；由实际请求Provider使用映射内容时置true，不把只在内存准备但未转发当外部应用。`JsonBodyRequestMiddleware.InvokeAsync(HttpContext context,BodyTransformBudget budget,GatewayBodyTransformSettings settings) -> Task`置于TrafficPolicyMiddleware之后、Cache之前，即使请求方向未选也负责整个下游正文租约finally释放。

- [ ] **Step 1：写RED。** 实际POST gzip / br JSON断言回显customer_id=123、原路径 / API授权不变、原字段移除、正确Content-Type / Length且旧摘要被清理。Codec对chunked未知长度、压缩 / 解码 / 输出超限413、损坏编码、UTF-8 / 空JSON400、未支持媒体 / 多层编码415、操作缺路径422均拒绝且backendCalls=0；两个方向共用租约、默认16许可的第17请求503、取消后配额恢复。覆盖Range / If-*、SSE / gRPC / Upgrade、请求Signature / Signature-Input拒绝，未选择方法不读取正文。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~JsonBodyCodecTests|FullyQualifiedName~JsonRequestBodyPipelineTests|FullyQualifiedName~BodyTransformBudgetTests'`。
- [ ] **Step 3：实现。** 在认证 / 限流后，仅请求正文方向被当前方法选择时取得租约；响应方向由Task 11在实际映射前获取或复用，缓存Hit不无谓占正文配额。有界保留原正文可读语义，映射结果仅替换后端HttpRequestMessage.Content，原方法 / BodyDetection / ContentLength语义保持，避免让有正文请求被识别为无正文。使用GZipStream / BrotliStream、单层编码和UTF-8校验，绑定请求与路由超时；未支持流式 / 条件请求在转发前拒绝。SnapshotActivation（包括LKG）调用settings.Validate，拒绝超过本节点限额的2.3而保留旧运行代际。
- [ ] **Step 4：验证GREEN。** 同命令及原RetryPipeline / ApiKey / JWT / Header信任回归通过；带正文GET、POST / PUT / PATCH仍不自动重试，认证401 / 限流429 / CORS预检204不分配正文预算或读取正文；超限NACK和旧LKG载荷恢复正确。
- [ ] **Step 5：记录限定补丁和实际字节结果。** 所有原文 / 映射正文缓冲只在本请求使用，finally清理、归还或清零私有缓冲；日志只固定reason，不输出原正文，不提交。

### Task 11：最终上游响应正文映射与失败原子性

**Files:** Create `src/WebApi.Gateway/Policies/PolicyResponseTransformProvider.cs`、`tests/WebApi.Gateway.Tests/JsonResponseBodyPipelineTests.cs`；Modify `GatewayApp.cs`、`Policies/{TrafficExecutionContext,TransformPolicyMiddleware,RetryForwardingMiddleware,CircuitOutcomeClassifier}.cs`、`Forwarding/{GatewayForwardingAdapter,RetryResponseTransformer}.cs`（RetryResponseTransformer仅维持/验证先抑制后inner的顺序，无缺口不改）。

**Interfaces:** `PolicyResponseTransformProvider.Configure(TransformBuilderContext context) -> void`，与Task 9请求Provider注册进同一YARP Transformer；复用Task 4 / 10。Execution增加`bool ResponseBodyApplied`；最终响应映射成功才置true，TransformFailure先于旧Applied决策汇总。

- [ ] **Step 1：写RED。** 上游200 gzip / br JSON实际输出result.stock=10且无debug、无旧ETag / Last-Modified / Digest / Encoding、正确UTF-8长度；缺字段 / 非法JSON / 非JSON / 多层编码 / trailer / 超限均完整502，未输出原文或部分JSON且缓存写0；响应处理预算不足503，不显示上游200成功；HEAD选中状态无body / 旧长度，非选中状态原样透传。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~JsonResponseBodyPipelineTests'`。
- [ ] **Step 3：实现。** 在最终上游响应且未开始客户端响应时按status / method选择，获取或复用单一正文租约，完整有界读取 / 解压 / Task 4映射后再输出。ProxyResponse使用最终identity表示、重建Content Header并移除旧正文验证器；控制错误回应不携带旧Content / 上游正文，保持合法CORS与平台Trace。真实映射失败设置typed TransformFailure，adapter输出最终失败状态 / ProxyError，避免原200掩盖；retry以固定response_transform_rejected终止，不重试本地失败，不将其伪造成IForwarder连接故障。已有传输 / 超时及按状态配置的熔断规则保留。
- [ ] **Step 4：验证GREEN。** 同命令通过；503中间响应被RetryResponseTransformer先抑制，因此映射计数0，最终200映射1次；最终格式失败即使后端返回200也显示Rejected / ProxyError，无新的物理尝试。响应正文和业务Header规则同启时配合Hit / Miss均只执行对应阶段一次；本地映射错误不被CountConnectionFailures伪装为网络连接错误。
- [ ] **Step 5：记录限定补丁和时序证据。** cancellation / timeout / 代理错误 / 部署切换后BodyLease释放且旧映射计划不污染新代际；含Signature / Signature-Input的选择响应失败，未选择状态不偷偷映射，不提交。

### Task 12：CORS / Transform与真实缓存、重试、熔断组合

**Files:** Modify `src/WebApi.Gateway/Policies/{CacheKeyBuilder,CacheEligibility,CacheResponseMiddleware,BoundedResponseCapture,TrafficExecutionContext}.cs`；Create `tests/WebApi.Gateway.Tests/{NetworkPolicyCacheTests,NetworkPolicyRetryTests}.cs`；Extend `tests/WebApi.Gateway.Tests/Support/CachePipelineFixture.cs`与现有真实Forwarding回显夹具。

**Interfaces:** 保持`CacheKeyBuilder.Build(TrafficExecutionContext,HttpRequest,CacheConfiguration) -> CacheLookupKey?`，内部加入Task 8的Origin维度；`CacheEligibility.Response`尾部增加可选`bool originPartitioned=false`，只在绑定CORS且键已隔离Origin时启用。Task 9请求计划与Task 10映射字节用于实际发送；Task 11响应引擎先映射最终表示，再由缓存捕获。缓存Hit不调用JsonBodyMapper，其他Vary和身份条件保持。

- [ ] **Step 1：写RED。** 两个允许Origin对同业务GET分别Miss→Hit，后端正文不同且各自Vary: Origin / CORS不串用；未允许其他Vary旁路；缓存Hit断言response业务Header正确、请求改写only时Bypass、失败计划不取缓存。3次真实503→成功均断言路径前缀只出现一次、同query / header、rate一次、半开探测一次；响应JSON操作有数组add的非幂等用例，Miss只add一次，Hit不再add，两个Origin也分别只add一次。最终映射失败无缓存写入，客户端502 / 503不被上游200掩盖；POST正文仍仅一次实际调用。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~NetworkPolicyCacheTests|FullyQualifiedName~NetworkPolicyRetryTests'`；使用真实Redis与真实HTTP后端计数。
- [ ] **Step 3：实现。** Origin仅内部Key受HMAC哈希不作标签；保持上游Vary和平台Vary所有权；存储仍使用现有白名单，最终响应Header统一执行。保留原重试副本 / 超时 / 熔断 / 不安全重放判断；实际mapped UTF-8输出由BoundedResponseCapture捕获，删除旧正文摘要 / ETag后沿用原Cache-Control安全资格。
- [ ] **Step 4：验证GREEN。** 同命令及既有AdvancedPolicyCompositionTests、缓存身份分区 / Redis失败 / retry测试通过；配置发布与回滚均冷分区，旧在途请求使用旧计划。
- [ ] **Step 5：限定补丁与结果入台账。** 记录cache read / write与物理尝试，不将部署序列隔离宣称为第2包性能收益。

### Task 13：制品策略模板和目标环境显式映射

**Files:** Modify `src/WebApi.Domain/Delivery/ArtifactPolicyTemplate.cs`及仅有实际缺口的`src/WebApi.Infrastructure/Delivery/{PromotionMappingService,PromotionCandidateBuilder,ReleasePromotionService}.cs`；Create `tests/WebApi.Domain.Tests/NetworkPolicyArtifactTests.cs`、`tests/WebApi.Integration.Tests/NetworkPolicyPromotionTests.cs`。

**Interfaces:** 复用`ArtifactPolicyTemplates.Split / Normalize / Resolve`，cors环境字段为allowedOrigins，ip_filter为ranges，transform为[]，包括requestBody / responseBody；其余字段按规格冻结。保留现有Mapping DTO与目标私有策略创建规则，不新建晋级入口。

- [ ] **Step 1：写RED。** Split / Resolve断言目标Origin / CIDR必须出现且类型正确、冻结字段不可改；空targetParameters不能直接继承源值；Transform规则不变；跨项目目标策略、陈旧修订及冻结策略被共享篡改均阻断。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh domain --filter 'FullyQualifiedName~NetworkPolicyArtifactTests'`，再`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~NetworkPolicyPromotionTests'`。
- [ ] **Step 3：实现。** 扩展严格环境字段分支和目标配置校验；现有通用目标策略路径无缺口则不改相关服务。晋级预检引用Task 6实际schema门禁。
- [ ] **Step 4：验证GREEN。** 同命令和现有PromotionMapping / Submission / Execution / Concurrency / Credential回归通过；TEST→PROD新策略独立创建，原共享策略修订不变。
- [ ] **Step 5：限定补丁与结果入台账。** 记录是否确实需要服务修改及理由，不为了计划文件名强行修改无缺口代码。

### Task 14：8类受控观测完整输出与读取

**Files:** Modify `src/WebApi.Contracts/Policies/PolicyObservationContracts.cs`、Gateway `Policies/{TrafficExecutionContext,TransformPolicyMiddleware}.cs`与`Observability/{TelemetryAttributes,GatewayTelemetryRecorder,TelemetryMetricExporter,RequestTelemetryMiddleware}.cs`、Infrastructure `Observability/{PolicyObservationProjection,LokiLogSource,TempoTraceSource,PrometheusMetricSource}.cs`；Create `tests/WebApi.Gateway.Tests/NetworkPolicyTelemetryTests.cs`、`tests/WebApi.Integration.Tests/NetworkPolicyObservationTests.cs`。

**Interfaces:** 使用Task 6的`PolicyDecisionValues.Types / Allowed / Valid / MaximumPerRequest`，保留PolicyDecisionDto原字段，在尾部增加可省略nullable `int? OperationIndex=null,int? BodyOperationCount=null`（仅transform可用，分别1–32和0–64），旧数据序列化不增加null字段。Fields增加operation_index / body_operation_count，仅日志 / Trace输出，非Prometheus标签；BodyOperationCount按实际RequestBodyApplied / ResponseBodyApplied两方向的已完成操作数求和，不包含失败原子阶段的部分操作。理由逐项来自规格第6节，不接受任意reason；TrafficExecutionContext.Record容量8且一类型一次。消除TelemetryAttributes.Take(5)丢失新增类型问题，输出改用MaximumPerRequest，不移除容量上限；retry沿用Task 6已声明的受控Bypass理由response_transform_rejected。

- [ ] **Step 1：写RED。** 构造JWT认证的9类绑定8类实际决策，断言输出 / 投影含cors、ip_filter、transform；非法类型 / 理由组合拒绝；旧5类材料仍读；早期IP拒绝只有已执行阶段；秘密canary不出现在日志 / Trace / 标签，固定原因过滤保持可见作用域。仅响应正文方向启用的失败断言OperationIndex=2、BodyOperationCount=0；请求已完整应用而响应失败时仍保留真实请求操作数，不回显path / from；旧记录的两个新增nullable字段缺失可读，其他类型提供它们必须拒绝。
- [ ] **Step 2：验证RED。** Run `./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~NetworkPolicyTelemetryTests'`，再`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~NetworkPolicyObservationTests'`。
- [ ] **Step 3：实现。** 注册受控类型及原因，同步收集 / 输出 / 读取 / 查询过滤；最终Transform决策从执行上下文的固定操作序号及实际映射操作计数读取，Header-only / 缓存Hit没有伪造正文操作计数；不把真实Origin / IP / 改写值转换为新字段或Prometheus标签。
- [ ] **Step 4：验证GREEN。** 同命令及既有PolicyTelemetry / AdvancedPolicyTelemetry / PolicyObservation / AdvancedPolicyObservation回归通过。
- [ ] **Step 5：限定补丁与结果入台账。** 后续Task 16还必须验证真实三源数据，当前测试不代替外部观测验收。

### Task 15：三类表单、Review、晋级映射及可操作导航

**Files:** Create `console/src/policies/{CorsPolicyFields,IpFilterPolicyFields,TransformPolicyFields,JsonBodyMappingFields,JsonMappingPreview}.tsx`、`console/src/policies/{network-policy-state,json-body-draft}.mjs` / `.d.mts`、`console/tests/{network-policy-state,json-body-draft}.test.mjs`；Modify该policies目录`policy-state.mjs` / `.d.mts`、`PolicyForm.tsx`、`RoutePolicyEditor.tsx`、`PolicyReleaseReview.tsx`、`advanced-policy-state.mjs` / `.d.mts`、`console/src/pages/{Policies,PolicyEditor,AccessLogs,Traces,MetricsOverview}.tsx`、`console/src/delivery/{PromotionWizard,PromotionDiff}.tsx`、`console/src/delivery/delivery-state.mjs` / `.d.mts`、`console/src/api/policies.ts`；Extend现有policy / promotion / review / observability Node测试。

**Interfaces:** `initialNetworkPolicyDraft(type:'cors'|'ip_filter'|'transform') -> object`；`validateNetworkPolicyDraft(draft) -> string|null`；`networkPolicyReview(type,config) -> object`；CORS / IP组件接收`{config:any,change:(config:any)=>void,disabled:boolean}`；Transform字段额外接收`limits?:{transformMaxBodyBytes:number}`。`parseTransformDraft(configJson:string) -> TransformDraft`保留操作value原始片段，`serializeTransformDraft(draft:TransformDraft) -> string`在结构化外壳中合法嵌入经独立JSON语法校验的raw value，不能用JSON.parse后的Number重写值。`policyConfigSource(draft) -> string`按类型选择该序列化器或原JSON.stringify。`policyRequest.previewTransform(scope,config,direction,sampleJson,signal?) -> Promise<TransformPreviewDto>`；样例和返回结果字符串直接文本展示，方向 / scope epoch隔离。服务端规范化仍为最终权威，不新增重复管理API。

- [ ] **Step 1：写RED。** 新类型可初始化 / 校验 / 保存，Header受保护名称和范围错误就地提示，9绑定与schemaReview可读；初始不填通配来源或继承晋级Origin / CIDR；BodyConfig在晋级整体冻结，不允许目标环境悄悄修改操作 / value / method / status / 字节限额；作用域late-response、readonly、412、dirty导航保持；JSONbody编辑支持两方向选择 / 32条有序op / Pointer / value原始JSON / maxBodyBytes / methods或statusCodes；保持null与缺字段、大整数和精确小数。样例 / 输出只内存、不localStorage，预览明确无真实转发；超部署上限、晚到请求、只读、CSRF / 403及取消有明确反馈。
- [ ] **Step 2：验证RED。** Run `"$WEBAPI_NODE" --test console/tests/network-policy-state.test.mjs console/tests/json-body-draft.test.mjs`及相邻现有测试，失败须源于新状态缺口。
- [ ] **Step 3：实现。** 小字段组件接入既有表单，包含同引擎preview接口和raw-value保真加载 / 保存 / 校验；networkPolicyReview显示两方向规则 / 正文限额 / 契约核对提示；PromotionWizard按环境字段提供明确数组输入 / 目标策略选择。保护ETag / 幂等 / scope epoch，不将保存展示为运行生效；观测过滤增加受控决策展示。
- [ ] **Step 4：验证GREEN。** Run `./scripts/check-console.sh`全部Node用例与TS / Vite通过；当前test计数在日志实读，不预填旧计数。
- [ ] **Step 5：1440px交互QA准备。** 使用独立Console与服务owner，按新增 / 编辑 / 校验 / 复制 / 9绑定 / 发布Review / 晋级 / 日志Trace新增JSON两方向预览 / null / 大数 / 错误与实际调用对照，逐动作保留截图，无法运行的动作标未执行，不编造成功状态。

### Task 16：独立双网关正式场景驱动与严格证据门禁

**Files:** Create `scripts/network-policies/{scenario,evidence,acceptance}.mjs`、`tests/network-policies/{scenario,evidence}.test.mjs`、`tests/WebApi.EndToEnd.Tests/NetworkPolicyLoopTests.cs`；Modify现有`scripts/gateway-policies/fixtures/Program.cs`仅加回显 / 次数 / Origin Vary路径（现有行为不改）；Create `scripts/network-policies/fixtures.mjs`负责调用现有owner / runtime helpers，不复制清理逻辑。

**Interfaces:** `runNetworkPolicyScenario({revision,imageId,cloneDirectory,fixtureDirectory}) -> Promise<ScenarioResult>`；`validateNetworkPolicyEvidence(proof,files,{revision,imageId,privateValues}) -> AcceptanceResult`；`runNetworkPolicyAcceptance({revision,imageId,directory,cloneDirectory,privateValues}) -> Promise<AcceptanceResult>`。E2E trait `Scenario=NetworkPolicies`，私有材料目录环境变量`WEBAPI_NETWORK_POLICY_RESULT_DIRECTORY`。

- [ ] **Step 1：写RED。** 证据测试删除任一P3-01–12材料、伪造actual、错误source / image / owner、仅1节点ACK、旧Hash被改写、失败 / skip统计、秘密canary、错误HTTP / 后端次数 / 映射次数 / 输出JSON或字节限额均拒绝；fixture新增路径先做实请求红绿验证，不以假executor判通过。
- [ ] **Step 2：验证RED。** Run `"$WEBAPI_NODE" --test tests/network-policies/*.test.mjs`；E2E未提供材料必须失败，不默认skip。
- [ ] **Step 3：实现。** 复用现有ContractClient、runtime owner / fixed构建 / UUID夹具和三源观测。由initializeRuntime在全新独立owner内初始化空数据库并创建本轮业务数据，configureRuntime绑定本owner环境；cloneDirectory仅沿用驱动参数名，不读取或恢复4192数据库。场景包括全9类、两网关运行、旧协议阻断、冻结修订、三认证、预检0后端调用、3次无正文安全重试、JSON两方向真实回显 / gzip / br / 超限 / 原子失败 / 非幂等响应缓存Hit不重映射 / 并发配额 / Preview同源且无写入、不同Origin缓存、在途代际、2.0 / 2.1 / 2.2与2.3历史回滚、TEST / PROD审批晋级、当前实例ACK和实际观测。公开材料读取内部身份 / 数量 / Hash验证，不信任wrapper标签；不复用旧验收器中要求4192冷恢复的保护流程。
- [ ] **Step 4：跑工作区完整回归与初步真实验收。** Run所有domain / integration / gateway、check-console、现有runtime / gateway-policies与新增network-policies Node测试；逐suite失败 / 跳过0。GatewayFixture实际双进程 / PG / Redis回归可直接进行，保留为功能回归证据，不能冒充固定镜像正式验收。
- [ ] **Step 5：准备固定版本审阅包。** 汇总精确产品 / 测试 / 驱动 / 文档文件、补丁SHA、父SHA、秘密扫描与所有日志。按Global Constraints取得该具体快照的本地提交授权后，使用现有fixed-source构建工具构建同一imageId的控制面、Worker、Console与双Gateway；未获授权则保留审阅包，不自建提交或绕过固定构建门禁。
- [ ] **Step 6：运行固定镜像正式验收。** 本地快照获授权且固定构建成功后，在独立owner执行scenario / acceptance；调用`node scripts/network-policies/acceptance.mjs <revision> <imageId> <directory> <cloneDirectory>`。E2E在已挂载证据目录的测试SDK中运行`dotnet test tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj -c Release --filter 'Scenario=NetworkPolicies'`，环境变量指向该目录。要求两节点同版本 / 序列 / Hash和ACK、P3-01–12实际证据齐全、真实日志 / Trace / 指标可读；失败保留材料，按新固定快照完整复验。
- [ ] **Step 7：1440px实际页面QA。** 使用CUA访问仅本owner独立端口，动作与Task 15一一对照；截图与动作记录绑定revision / imageId，无法确认的源或页面不得标actual。仅清理本owner诊断外可删除资源，保留失败与已承诺数据，不操作4192和第2包owner。

### Task 17：最终审查、覆盖台账与交付

**Files:** Create `docs/evidence/network-policies/<run-id>/`及`docs/deployment/network-policies-runbook.md`；Modify独立工作区`docs/{acceptance,console-coverage}.md`，回主工作区时只合入审阅过的对应补丁，不覆盖其原改动。

**Interfaces:** 台账每行使用`requirementId,sourceIdentity,testLog,evidencePaths,status,remainingGate`；运行手册覆盖三类录入、绑定、审批、节点2.3能力、观测、历史回滚和目标环境参数。sourceIdentity为已授权固定SHA或尚未固定的父SHA+补丁SHA，二者明确区分。

- [ ] **Step 1：复核最新源与材料。** 确认最终实现文件与固定验收来源一致，真实计数、失败及复验保留；P3-01–12不缺项，所有承诺均可追溯。
- [ ] **Step 2：完整分支独立审查。** 使用requesting-code-review技能派出一个只读最终审查，范围仅本包精确差异与证据，检查规格遵循、Header信任、原API授权、缓存 / 重试、能力门禁、历史兼容和作用域；这不是逐任务子代理开发。遇到反馈用receiving-code-review核实后修复。
- [ ] **Step 3：必要复验。** 修复后的有关红绿 / 回归再跑；若改变已固定镜像源码，则另提供精确提交包、重新固定构建并复验正式场景，不能复用旧镜像通过证明新代码。
- [ ] **Step 4：保护与交付。** 逐文件复核主工作区原内容 / 状态、4192数据身份及第2包暂停态，报告最终功能 / 验收 / 本地快照 / 未执行部署边界。提交、main合并、远端推送、4192升级各按当时明确有效授权执行，不从“继续”推断部署许可。

## 规格覆盖与计划自检

| 规格 / 验收 | 实施任务 |
| --- | --- |
| 意图、原权限 / 数据、第2包暂停 | 1、5–7、13、15–17 |
| CORS配置 / IP配置 / 请求Header路径Query | 1–3、6–9 |
| JSON五类操作、Pointer、null / 数字 / 原子失败 | 3–4、10–12 |
| 同引擎预览、只读权限 / 配额 / 样例不持久化 | 4–5、15–16 |
| 压缩、限额、流式 / trailers / 条件 / HEAD / 标准签名 | 10–12、16 |
| 快照2.3、老历史、部分发布、能力 / ACK / 回滚 | 6–7、16–17 |
| 缓存Origin隔离、最终JSON仅一次、真实尝试 / 租约 | 7、9–12、16 |
| 显式跨环境参数与冻结正文规则 | 13、15–16 |
| 8类受控观测、真实三源、原200不可掩盖失败 | 6、11、14、16 |
| 1440px交互、预览与真实调用明确区分 | 15–16 |
| P3-01–P3-12齐全、固定镜像、最终独立审查 | 16–17 |

接口声明与使用按Task 1–5先于消费任务排列；两个正文方向共用Task 4 / 10接口。每项RED / GREEN必须实读真实测试结果，文档自检不视为功能测试。计划批准后完成Task 1–15实施 / 回归，Task 16准备精确固定快照审阅包及正式验收，Task 17统一审查与交付；新提交 / 推送 / main合并 / 4192部署各按明确授权推进。
