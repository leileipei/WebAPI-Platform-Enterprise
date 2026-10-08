# 契约管理功能补全 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成 URL/YAML 导入、可追踪的完整兼容性规则目录、Schema 树与示例验证，并在本机 4192 安装经过固定提交验收的版本。

**Architecture:** 在现有 Infrastructure 增加有界契约读取、引用图和方言适配核心，控制面负责授权及固定来源包，比较与验证只处理固定包。保留现有 Catalog、工作版本、幂等、评审和发布接口；新增会话式导入，不从运行网关访问数据库或外部契约源。验证与复杂比较使用受期限控制的子进程，避免不可协作取消的第三方解析/正则阻塞控制面。

**Tech Stack:** .NET SDK 10.0.401、现有 EF Core/Npgsql/PostgreSQL、YamlDotNet 18.1.0、JsonSchema.Net 9.4.0、React 19.2.0/Vite 6.4.2、Node 原生测试及现有 xUnit 测试框架；依赖固定并锁定。

**Spec:** [已确认的契约管理详细设计](../specs/2026-10-06-contract-management-completion-design.md)，设计提交 `6f5abec1e280c6cf8d3716954379a26175fb5041`，产品代码基线 `1259ba0dc3eafca883ede8797fb91b90612a998d`。

状态（2026-10-08 回填）：本批 12 项任务、60 个步骤已完成，本地提交、合并、4192 安装及首批交付均有实际证据。首批验收软件为 `cc880ba171de74dae33b87587a308134780f39a6`，结果文档提交为 `fe9b85dec8c790afc75d8fb7ec1a841d1155a205`；[首批部署结果](../../deployment/contract-management-completion-upgrade-result.md)保留当时状态。后两批现已完成批准的本机开发与交付，分别记录在各自计划，当前汇总见文末；不追改首批历史结果或重标旧测试身份。

复选框表示对应目标已按实际执行裁定完成；计划中的示例命令、测试名称和文件拆分不代表逐字执行记录。任务提交、实际测试及裁定见文末核对表。此次仅同步进度文档，不重新运行产品全套测试、不更换运行镜像或重封存历史 ZIP。

## Global Constraints

- 保留既有业务架构、账号、权限代码、业务字段、流程和历史证据；不得把测试发布写入原运行环境。
- 根文档原始/规范化字节各 2 MiB；来源包共 16 份、各 2 MiB、合计原文/规范化各 4 MiB；深度 64、总节点 50,000。
- URL 网络阶段 15 秒、连接 5 秒、无自动重定向；精确 origin/路径段规则、DNS 固定连接与 TLS 验证，项目规则不得突破部署规则。
- 预览有效期 20 分钟；每用户每项目最多 5 份、每项目最多 100 份有效预览；Operation 1–1,000。
- 示例 256 KiB、来源及 Schema 总计 4 MiB、500 条诊断、5 秒执行；比较单侧 4 MiB、双侧 8 MiB、5,000 发现、10 秒执行。
- 版本方言为 OAS 3.0 或 OAS 3.1/2020-12；默认 format 为 Annotation；Strict 未知格式列覆盖问题。
- 兼容性结论为 Compatible/Breaking/Unknown；Coverage 为 Complete/Limited/Invalid；无反例不等于有兼容证明；Invalid 禁止评审。
- 历史 v1 证据字节与哈希保留；新证据使用 `compatibility-v2`；冻结申请按登记的历史指纹规则复核，保留真实资源 Revision 和当前授权检查。
- API/Schema 编辑保留 `If-Match`、幂等、脏表单、403/412 恢复、封存不可变和显式保存；示例不匹配不新增发布门禁。
- 本机使用现有 Docker 固定 SDK/镜像，独立 UUID 测试项目；目录 0700、凭据 0600，不将密码、Cookie、原文或示例值写入公共证据。
- 原主工作区的七个已修改文件和历史交付材料保持；禁止 `git add .`，每次只提交本任务审阅文件。

## Review Focus

1. 两个组织并发提供相同 `$id`、内容不同：各自独立解析，不能通过全局注册表互相覆盖；任务 2/5 测试。
2. 示例为 `false`、`0`、`""`、`null`：作为真实实例校验，不能被当成“没有示例”；任务 5/9 测试。
3. 原文输入错误或异步校验尚未返回时切换定义/版本：保留草稿、阻止旧结果覆盖新版本；任务 9 测试。
4. origin 相同但路径有编码斜杠、双重编码、反斜杠或 dot segment：不能绕过路径允许范围；任务 3 测试。
5. YAML 来源从旧 `/versions/{id}/openapi` 下载：正文、MIME 与选定表示一致，不把 YAML 标成 JSON；任务 6 测试。

---

## 文件职责与执行环境

所有相对路径以 `enterprise/.worktrees/gateway-restart-readiness` 为根。执行开始使用 `using-git-worktrees` 核对并复用这个已隔离、无 tracked 产品改动的工作区；若状态变化先查明归属，不重置或丢弃文件。迁移时间戳用实际生成时间，三张新表以单独迁移 `ContractManagement` 及其 Designer/ModelSnapshot 提交。

| 文件组 | 职责 |
| --- | --- |
| `src/WebApi.Contracts/OpenApi/` | 共用的方言、来源包、诊断和限额记录，只依赖 BCL，供 DTO、解析器和离线协议使用 |
| `src/WebApi.Infrastructure/Contracts/` | ContractDocumentReader、BoundedYamlReader、ContractReferenceRegistry、DialectAdapter、SchemaEvaluator、ContractProcessRunner、执行限额检查 |
| `src/WebApi.Infrastructure/Catalog/` | ImportSourcePolicyService、ImportSourceFetcher、ImportPreviewService、ImportBatchWriter、VersionContractSourceService；原导入服务适配旧接口 |
| `src/WebApi.Infrastructure/Comparisons/Rules/` | 包含证明、结构规则、组合规则、OpenAPI 规则、规则目录；保留旧指纹算法 |
| `src/WebApi.Contracts/Catalog/` | 新来源规则、会话预览与校验 DTO；原请求保留 |
| `src/WebApi.Infrastructure/Persistence/` | 三实体/配置/迁移；不修改历史数据 |
| `src/WebApi.ControlPlane/Catalog/` | 项目来源规则、会话预览、Schema 校验端点；沿用 CSRF/授权/审计 |
| `src/WebApi.RuntimeTool/` | 原 `probe` 保留，增加离线 `contract-evaluate` 子命令；复用已有运行镜像发布目录 |
| `src/WebApi.Worker/` | 预览有界清理，复用原 Worker |
| `console/src/contracts/` | 导入状态、Pointer 操作、Schema 树、示例状态、来源配置组件 |
| `scripts/contracts/`、`tests/contracts/` | 有归属的隔离测试、固定提交端到端验收、证据保护与恢复验证 |

计划中的命令别名：`N` 指 `/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node`；`PY` 指同级 `dependencies/python/bin/python3`。设置 `WEBAPI_NODE` 为 N，既有锁定前端依赖不新增包。

新增 `./scripts/check-contracts.sh domain|integration|gateway [dotnet 参数]`：复用 `check-policies.sh` 的 UUID 项目、只读 NuGet seed 与归属验证方式，采用自己的 `com.webapi.contracts.owner` 标签与 `.runtime/webapi-contracts-test-<uuid>/`，构建本次 RuntimeTool 后执行目标测试。后续步骤中的 `--filter` 为 xUnit FullyQualifiedName 过滤。首次恢复锁文件允许联网，正式验证 `--locked-mode`；测试失败仍执行有归属的清理，保留失败退出码。

## Task 1：有界 JSON/YAML 读取与锁定依赖

**Files:** Create `src/WebApi.Contracts/OpenApi/{ContractModel,ContractLimits}.cs`、`src/WebApi.Infrastructure/Contracts/{ContractDocumentReader,BoundedYamlReader}.cs`、`scripts/check-contracts.sh`、`deploy/compose.contracts-test.yml`、`tests/contracts/runner.test.mjs`、`tests/WebApi.Domain.Tests/ContractDocumentReaderTests.cs`；Modify `Directory.Packages.props`、`src/WebApi.Infrastructure/WebApi.Infrastructure.csproj`、受依赖影响的 `packages.lock.json`；Create `docs/dependencies/contract-management.md`。

**Interfaces:** 在 `WebApi.Contracts.OpenApi` 定义 `ContractDialect { Oas30, Oas31 }`、`ContractSource(Uri LogicalUri,string RawText,string Format)`、`ContractIssue(string Code,string Pointer,string Message,int? Line=null,int? Column=null)`、`ContractDocument(ContractSource Source,JsonObject Root,ContractDialect Dialect,string CanonicalJson,IReadOnlyDictionary<string,ContractIssue> Locations)`、`ContractBundle(Uri RootUri,IReadOnlyList<ContractDocument> Documents,string Hash)`。`ContractDocumentReader.Read(ContractSource source,ContractLimits limits,CancellationToken ct) -> ContractDocument` 抛现有 ApiException，纯函数且不获取网络。ContractLimits 提供文首固定预算；Contracts DTO 只引用这些共用记录，不反向引用 Infrastructure 类型。

- [x] **Step 1：写失败测试。** `JsonAndYamlPreserveEquivalentContract` 比较 CanonicalJson；`DuplicateKeysAndAliasCycleAreRejected` 检查错误码/位置；`ExpansionCannotExceed50000NodesOr2MiB`、`ScalarConversionDoesNotRoundLargeNumbers`、`UnknownFieldsRemainInRoot`、`InvalidOpenApiShapeIsRejected` 钉住精确边界。runner 测试断言 UUID、标签检查、失败退出码及仅清理自有资源。
- [x] **Step 2：验证失败。** Run `./scripts/check-contracts.sh domain --filter ContractDocumentReaderTests` 和 `N --test tests/contracts/runner.test.mjs`；首次失败应来自未实现入口/断言，修复测试基础设施后确认有真实行为失败。
- [x] **Step 3：实现并恢复锁文件。** 固定 YamlDotNet 18.1.0、JsonSchema.Net 9.4.0；低层 YAML 事件处理、不使用 CLR 自动反序列化；处理 JSON-compatible 标量、merge/别名、重复键、原文位置和规范化后限额。记录包许可证/内容哈希；将原始字节控制放在 parse 之前。
- [x] **Step 4：验证通过。** 重跑上述命令，断言所有边界通过；在固定 SDK 中对所有受影响项目运行 `dotnet restore --locked-mode`，记录锁文件变化只来自新依赖。
- [x] **Step 5：提交。** 仅 add 本任务文件及实际变化锁文件；commit `feat(contracts): add bounded JSON and YAML document reader`。

## Task 2：方言适配、引用图与来源包

**Files:** Create `src/WebApi.Infrastructure/Contracts/{ContractReferenceRegistry,DialectAdapter,ContractBundleCodec}.cs`、`tests/WebApi.Domain.Tests/ContractReferenceTests.cs`、`tests/WebApi.Domain.Tests/ContractDialectTests.cs`。

**Interfaces:** `ContractReferenceRegistry(ContractBundle bundle,ContractLimits limits)`；`Resolve(Uri currentResource,string reference) -> ResolvedContractNode(Uri ResourceUri,string Pointer,JsonNode Node)`；`DialectAdapter.PrepareSchema(JsonNode schema,ContractDialect dialect,string direction) -> PreparedSchema(JsonNode Node,IReadOnlyList<ContractIssue> Issues)`；`ContractBundleCodec.Create(Uri root,IReadOnlyList<ContractDocument> documents) -> ContractBundle` 与 `Verify(ContractBundle bundle) -> void`。递归图保持资源/Pointer identity，不无限 deep clone。

- [x] **Step 1：写失败测试。** `RecursiveRefUsesGraphIdentity`、`BaseIdAnchorAndDynamicReferencePreserveScope`、`Oas30RefSiblingsDifferFromOas31`、`MissingRefNeverBecomesEmptySchema`、`DuplicateLogicalUrisAreRejected`。`SameIdInTwoBundlesDoesNotShareRegistrations` 并发验证两个同 `$id` 不同内容的包，输出严格分离；同一包资源/anchor 冲突拒绝。
- [x] **Step 2：验证失败。** Run `./scripts/check-contracts.sh domain --filter 'ContractReferenceTests|ContractDialectTests'`；确认相应行为未满足。
- [x] **Step 3：实现。** 固定资源注册表、正确 URI/base/Pointer/anchor；OAS 3.0 nullable/readOnly/writeOnly/exclusive 边界适配保留原文；OAS 3.1 按登记方言处理。来源包规范化哈希纳入所有资源，禁止任何 Fetch callback 或全局可变注册表。
- [x] **Step 4：验证通过。** 同一命令覆盖 64 深度、50,000 节点、16 资源/4 MiB 及并发隔离；同一包反复编解码 hash 相同，篡改被拒绝。
- [x] **Step 5：提交。** commit `feat(contracts): add dialect-aware fixed reference bundles`，只暂存本任务清单。

## Task 3：项目来源规则与有界 URL 获取

**Files:** Create `src/WebApi.Contracts/Catalog/ImportSourcePolicyContracts.cs`、`src/WebApi.Infrastructure/Catalog/{ImportSourcePolicyService,ImportSourceFetcher,ImportAddressPolicy,ImportSourceSettings}.cs`、`src/WebApi.Infrastructure/Contracts/IContractDnsResolver.cs`、`src/WebApi.ControlPlane/Catalog/ImportSourcePolicyEndpoints.cs`；Create 三个实体 `src/WebApi.Infrastructure/Persistence/Entities/{ProjectImportSourcePolicy,ApiImportPreview,ApiVersionContractSources}.cs`、对应三个 Configuration、`ContractManagement` 迁移/Designer；Modify ModelSnapshot、ControlPlaneApp 注册；Test `ImportSourcePolicyTests.cs`（Integration）、`ImportSourceFetcherTests.cs`（Domain）。完整项目路径与现有同名层结构一致。

**Interfaces:** `ImportSourcePolicyService.GetAsync(Guid projectId,ActorContext actor,CancellationToken ct) -> ImportSourcePolicyDto`；`SaveAsync(Guid projectId,SaveImportSourcePolicyRequest request,string? tag,ActorContext actor,CancellationToken ct) -> CommandResult<ImportSourcePolicyDto>`。DTO 定义为 `(Guid ProjectId,long Revision,IReadOnlyList<ImportSourceAllowance> Allowances,ContractLimits Limits)`，Allowance 为 `(string Origin,string PathPrefix,IReadOnlyList<string> PrivateCidrs)`；Save 去掉 ProjectId/Revision。`IContractDnsResolver.ResolveAsync(string host,CancellationToken ct) -> Task<IPAddress[]>`；`ImportSourceFetcher.FetchAsync(Uri uri,ImportSourcePolicyDto policy,CancellationToken ct) -> Task<ContractSource>`；部署设置限制 HTTP/private CIDR/管理目标及固定预算。三表字段/保留规则严格按设计 §5.2，预览的内容列可清空而提交元数据保留。

- [x] **Step 1：写失败测试。** 规则首次 `If-Match` Revision 0、并发更新 412、跨项目不可见、仅 project.write 可修改；迁移后旧行哈希不变。Fetcher 测允许项成功、混合 DNS/IPv6/重绑定/跳转/超限/慢速拒绝、代理不使用及 pinned IP。`EncodedPathCannotEscapeAllowance` 覆盖 `%2f`、`%252e%252e`、反斜杠、dot segment 和路径前缀段边界。
- [x] **Step 2：验证失败。** Run `./scripts/check-contracts.sh domain --filter ImportSourceFetcherTests` 与 `./scripts/check-contracts.sh integration --filter ImportSourcePolicyTests`。
- [x] **Step 3：实现。** 参数化权限与精确地址规则；DNS 校验+SocketsHttpHandler ConnectCallback、原 host TLS；正文流与解压字节限制、总/连接期限、禁止 userinfo/query/根 fragment 和重定向。迁移仅新增三表，真实 FK/检查约束/索引；登记测试内网 origin 仅在独立部署设置，不开放现有管理端口。
- [x] **Step 4：验证通过。** 同上命令；真实 PostgreSQL 更新冲突与权限撤销均验证；捕获 fixture 网络调用次数确认被拒绝输入没有进入网络阶段。
- [x] **Step 5：提交。** commit `feat(import): add scoped source policies and protected URL fetching`。

## Task 4：固定预览、整批导入与清理

**Files:** Create `src/WebApi.Contracts/Catalog/ImportSessionContracts.cs`、`src/WebApi.Infrastructure/Catalog/{ImportPreviewService,ImportBatchWriter,ImportPreviewCleanupService}.cs`、`src/WebApi.ControlPlane/Catalog/ImportSessionEndpoints.cs`、`src/WebApi.Worker/Workers/ImportPreviewCleanupWorker.cs`；Modify OpenApiOperationParser、OpenApiImportService、ControlPlaneApp、WorkerApp；Test Integration 的 `ImportSessionTests.cs`、`ImportPreviewConcurrencyTests.cs`、现有 `ImportAndCredentialTests.cs`。

**Interfaces:** `CreateImportPreviewRequest(Guid ProjectId,Guid EnvironmentId,Guid ClusterId,string? SourceText,string? SourceUrl,string? Format,IReadOnlyList<ImportSourceFile>? Files)`，File 为 `(string Name,string Content,string? Format)`；`ImportSessionDto(Guid PreviewId,string SourceHash,string BundleHash,string Dialect,DateTimeOffset ExpiresAt,long SourcePolicyRevision,IReadOnlyList<ImportOperationDto> Operations,IReadOnlyList<ContractIssue> Issues)`；`CommitImportSessionRequest(string ExpectedBundleHash,IReadOnlyList<ImportTarget> Targets)`。ImportPreviewService 的 `CreateAsync(request,actor,ct)`/`GetAsync(id,actor,ct)` 返回 ImportSessionDto，`CommitAsync(id,request,actor,ct)` 返回 ImportCommitResponse，`RevokeAsync(id,actor,ct)` 无返回。`ImportBatchWriter.WriteAsync(ContractBundle bundle,ImportPreviewRequest target,IReadOnlyList<ImportTarget> targets,ActorContext actor,CancellationToken ct) -> Task<ImportCommitResponse>` 在调用者事务内写入，不自行获取 URL。CleanupService `RunAsync(int batchSize,CancellationToken ct) -> Task<int>`。

- [x] **Step 1：写失败测试。** `CommitUsesPreviewBytesAfterUrlChanges`；哈希篡改、过期、政策 Revision 变化、不同用户、封存、归属/授权撤销拒绝；中途映射/路由冲突无任何业务写入。`SameKeyReplayWorksAfterContentCleanup`、不同键二次提交冲突；并发用户第 6 份、项目第 101 份拒绝；清理/撤销与提交竞争不会产生部分内容。
- [x] **Step 2：验证失败。** Run `./scripts/check-contracts.sh integration --filter 'ImportSessionTests|ImportPreviewConcurrencyTests|ImportAndCredentialTests'`；已有“循环引用拒绝”测试拆成旧缺失外部包拒绝与有效递归支持，不能直接删掉全部负向断言。原 LocalReferences 测试将“所有 Schema 无 $ref”改成“引用能从保留的组件/固定包解析且请求/响应语义不变”，明确新数据保留图结构，历史已存 Schema 不改写。
- [x] **Step 3：实现。** actor/Scope 授权在获取前；多文件路径与逻辑 URI 校验、根/外部引用去重收集；有界预览存储；事务内用户/项目配额锁按固定顺序获得；幂等回执优先重放但先重查当前权限，清空内容不删授权元数据。OperationParser 用任务 2 引用图，保留参数/响应/多 examples/组件与范围状态码；旧端点适配共用 writer，继续默认 API Key、只写草稿，不发布。
- [x] **Step 4：验证通过。** 重跑所有新旧导入测试，检查 Worker 每轮有界清理和哈希/幂等回执保留。新导入版本保存原 SourceFormat、原文与固定包，草稿 Revision 同事务更新。
- [x] **Step 5：提交。** commit `feat(import): persist preview bundles and atomic import sessions`。

## Task 5：Schema 语义校验与可终止执行

**Files:** Create `src/WebApi.Infrastructure/Contracts/{SchemaEvaluator,ContractProcessRunner,ContractProcessProtocol,SchemaValidationSettings}.cs`、`src/WebApi.Contracts/Catalog/SchemaValidationContracts.cs`、`src/WebApi.RuntimeTool/ContractEvaluationCommand.cs`；Modify RuntimeTool Program/csproj；Create `tests/WebApi.Domain.Tests/{SchemaEvaluationTests,SchemaProcessTests,OfficialJsonSchemaTests}.cs`、`tests/fixtures/json-schema-2020-12/`、`tests/fixtures/json-schema-manifest.json`；Modify Domain.Tests csproj 复制测试资源。

**Interfaces:** `SchemaValidationInput(ContractBundle Bundle,JsonNode Schema,JsonNode? Example,string Direction,string FormatMode,bool HasExample)`；`SchemaValidationResult(string Status,string Dialect,string SchemaHash,string ExampleHash,string FormatMode,IReadOnlyList<SchemaValidationIssue> Issues,IReadOnlyList<ContractIssue> CoverageIssues)`，Issue 为 `(string InstancePointer,string SchemaPointer,string Keyword,string Code,string Message,int? Line,int? Column)`。`SchemaEvaluator.Evaluate(input,limits,ct) -> SchemaValidationResult` 离线纯函数；`ContractProcessRunner.RunAsync(ContractProcessRequest request,TimeSpan deadline,CancellationToken ct) -> Task<ContractProcessResponse>`，Request 为 `(string Operation,JsonElement Payload)`、Response 为 `(string Status,JsonElement? Result,IReadOnlyList<ContractIssue> Issues)`，支持 schema/compare，stdin/out 有界 JSON；不传 Shell 命令。原 probe CLI 行为保留。

- [x] **Step 1：写失败测试。** 数组/对象/组合/条件/动态引用/readOnly/writeOnly/Annotation 与 Strict；`FalseZeroEmptyStringAndNullAreRealExamples`；错误信息不得包含样本值；缺失引用/方言/已超预算返回 Incomplete。`HungEvaluatorIsKilledWithinDeadlineAndSlotReleased` 注入不退出子进程，期限后无遗留进程且下一请求成功；并发同 `$id` 隔离。官方套件测试先选择 required/ref/dynamicRef 等直接暴露适配缺陷的用例确认真实失败。
- [x] **Step 2：验证失败。** Run `./scripts/check-contracts.sh domain --filter 'SchemaEvaluationTests|SchemaProcessTests|OfficialJsonSchemaTests'`；SDK runner 必须先构建 RuntimeTool，并传入实际 DLL 路径，不能用未构建旧文件通过测试。
- [x] **Step 3：实现。** JsonSchema.Net 9.4.0 显式 BuildOptions、独立 SchemaRegistry、登记的方言与固定资源，禁 Fetch；适配 3.0 语义。消息按 keyword/code 重建且不带原值。RuntimeTool `contract-evaluate` 从 stdin 读取单次有界请求并返回单次响应；生产路径 `/app/runtime-tool/WebApi.RuntimeTool.dll`。父进程清理环境中的连接串/凭据，协议输入最多 16 MiB、输出最多 8 MiB，最多 2 个并发执行和 2 个等待槽；执行期限含等待时间，超时/取消 Kill(entireProcessTree:true) 并 wait/reap，schema 5 秒、compare 10 秒。槽满、进程不可用返回 Incomplete/Invalid，不能无界排队或回退无期限 Task.Run；实际 Schema/比较输入仍先通过设计规定的更小语义预算。
- [x] **Step 4：验证通过。** 固定官方测试集 `f6fd52a0a95472e079cbfc6ef7f089702b80e045`，将 2020-12 根目录、remotes 和登记 Strict 格式所需 optional 用例及许可证拷贝为普通文件，逐文件 SHA 清单；所有正式根用例必须运行，不能悄悄 skip。remotes 只注册离线 URI。通过完整命令，记录用例清单/失败/未支持项；失败的必需语义修复后才可通过本任务。
- [x] **Step 5：提交。** commit `feat(schema): add bounded dialect-aware semantic evaluation`，包含明确许可证和固定套件清单。

## Task 6：版本来源一致性与只读校验接口

**Files:** Create `src/WebApi.Infrastructure/Catalog/{VersionContractSourceService,SchemaValidationService}.cs`、`src/WebApi.ControlPlane/Catalog/SchemaValidationEndpoints.cs`；Modify CatalogService、CatalogEndpoints、ControlPlaneApp、目录 DTO 的可选方言字段；Test `tests/WebApi.Integration.Tests/{SchemaValidationApiTests,VersionContractSourceTests}.cs`。

**Interfaces:** VersionContractSourceService `ReadAsync(Guid versionId,ActorContext actor,CancellationToken ct) -> Task<ContractBundle>`；`SaveDraftAsync(Guid versionId,ContractBundle bundle,CancellationToken ct) -> Task` 仅在 Catalog/Import 已授权且已锁 Revision 的事务内调用。`ValidateSchemaRequest(long ExpectedVersionRevision,Guid? SchemaId,Guid? ParameterId,bool Draft,string? DraftSchemaJson,string ExampleJson,string Direction,string FormatMode="Annotation")`；结果 `SchemaValidationView(long EvaluatedVersionRevision,SchemaValidationResult Result)`；SchemaValidationService `ValidateAsync(Guid versionId,ValidateSchemaRequest request,ActorContext actor,CancellationToken ct) -> Task<SchemaValidationView>`；只用 schema.read，不写数据库。

- [x] **Step 1：写失败测试。** schema.read-only 可临时校验但行数/Revision/审计写入数不变；无权限/跨版本 ID/用户任意来源包被拒绝；旧 Revision 返回 412。替换根文档必须失效旧包；封存不可变；已被引用的组件删除/改名必须列受影响位置，不能留下静默断链。`YamlSourceDownloadHasCorrectMimeAndJsonRepresentation` 检查旧下载返回 YAML MIME、`?representation=json` 返回规范化 JSON，原 JSON 行为保持。
- [x] **Step 2：验证失败。** Run `./scripts/check-contracts.sh integration --filter 'SchemaValidationApiTests|VersionContractSourceTests'`。
- [x] **Step 3：实现。** 版本源读取验证 bundle hash；直接草稿保存分别验证 canonical 文档和 YAML/raw 来源，保存方言及来源元数据原子更新。Schema 保存增加结构/引用合法性检查，示例仅语法合法即可保留草稿。校验端点从服务端拿 Bundle/身份，拒绝不合法选择器、非 JSON 实例；正确识别 `null` 示例，服务端指定 HasExample。Schema/read 授权先于包读取和子进程执行。
- [x] **Step 4：验证通过。** 新测试、既有 Catalog/Import 测试；正文与 MIME、一致 Revision、老非法定义可读及清晰诊断均通过。
- [x] **Step 5：提交。** commit `feat(catalog): enforce source consistency and expose read-only schema validation`。

## Task 7：Schema 包含证明与完整规则目录

**Files:** Create `src/WebApi.Infrastructure/Comparisons/Rules/{CompatibilityProof,PrimitiveRules,ObjectRules,ArrayRules,CompositionRules,ReferenceRules,CompatibilityRuleCatalog}.cs`、`src/WebApi.Contracts/Comparisons/CompatibilityRuleContracts.cs`、`tests/WebApi.Domain.Tests/{CompatibilityProofTests,CompatibilityRuleCoverageTests}.cs`、`tests/fixtures/compatibility-v2/`；Modify SchemaCompatibilityRules/ComparisonContext 使 v2 独立、不改变 v1 输入算法。

**Interfaces:** `CompatibilityRuleDescriptor(string Id,string Family,IReadOnlyList<string> Dialects,IReadOnlyList<string> TestIds,string ProofCondition,string UnknownCondition)`；`CompatibilityProof.Compare(JsonNode baseline,JsonNode target,ContractBundle baselineBundle,ContractBundle targetBundle,string direction,ContractLimits limits,CancellationToken ct) -> CompatibilityProofResult(string Risk,IReadOnlyList<ComparisonFinding> Findings,IReadOnlyList<ComparisonCoverageIssue> CoverageIssues)`；RuleCatalog `All -> IReadOnlyList<CompatibilityRuleDescriptor>`。Finite proof 按请求 baseline⊆target、响应 target⊆baseline；每个成功证明记录 RuleId，未处理语义不产生 Compatible。

- [x] **Step 1：写失败测试。** 每个设计 §6.1 Schema 族建立请求/响应、放宽/收紧、相同、交互测试；`OptionalFieldAgainstClosedObjectIsNotCompatible`、`MultipleOfUsesExactArithmetic`、`OneOfOverlapIsNotAnyOf`、`UnevaluatedPropertiesKeepsAnnotationContext`、`RecursiveRefTerminatesAndUnknownIsVisible`、`NoWitnessDoesNotProveInclusion`。每个 RuleId 至少对应一个已执行的测试 ID；type/format/default annotation 不混为强制断言。
- [x] **Step 2：验证失败。** Run `./scripts/check-contracts.sh domain --filter 'CompatibilityProofTests|CompatibilityRuleCoverageTests'`；保留能显示错误方向/遗漏关键字的失败证据。
- [x] **Step 3：实现。** 各规则文件仅处理对应语义，精确边界/集合算术、完整父节点约束上下文、组合/引用对缓存；复杂包含问题保守 Unknown。反例经任务 5 同方言校验器复核才记 Breaking；存在反例输出仅哈希/规则定位，不公开样本值。Known keyword 有全目录，不受支持自定义 vocab 一律 CoverageIssue。
- [x] **Step 4：验证通过。** 全部规则用例、混合约束、数字精度、Unicode/pattern 预算；输出逐 RuleId 覆盖 JSON，无遗漏族、无零风险但有未处理约束。
- [x] **Step 5：提交。** commit `feat(comparison): add directional schema proof rules and coverage catalog`。

## Task 8：OpenAPI v2 比较及历史冻结证据兼容

**Files:** Create `src/WebApi.Infrastructure/Comparisons/Rules/{OpenApiContractRules,ResponseCoverageRules,SecurityRequirementRules}.cs`、`src/WebApi.Infrastructure/Comparisons/{CompatibilityEngineRegistry,LegacyV1Fingerprint}.cs`；Modify ContractComparisonEngine、ContractNormalizer、ComparisonInput、VersionComparisonService、VersionRiskReviewService、PublishCoordinator、ComparisonEndpoints；Test `tests/WebApi.Domain.Tests/OpenApiCompatibilityV2Tests.cs`、`tests/WebApi.Integration.Tests/{ComparisonEngineUpgradeTests,ComparisonReleaseTests}.cs`。

**Interfaces:** 保留 `ContractComparisonEngine.Compare(ComparisonInput,ComparisonLimits,CancellationToken) -> ComparisonReport` 外观，当前 EngineVersion=v2；新增输入包为可选元数据，但 `LegacyV1Fingerprint.Compute(ComparisonInput input) -> string` 仅按原 v1 输入 shape/规范化算法输出。`VersionRiskReviewService.ValidateFrozenReferencesAsync(ScopeRef environmentScope,IReadOnlyList<Guid> targetVersionIds,IReadOnlyList<FrozenRiskReviewReference>? references,ActorContext actor,CancellationToken ct) -> Task` 与新证据 ResolveReferencesAsync 分开。RuleCatalog GET `/api/v1/comparisons/rules` 返回当前规则版本/预算/已登记规则，需登录，不含业务数据。

- [x] **Step 1：写失败测试。** 操作/参数/content/serialization/security 继承及 OR/AND、response range/default 优先覆盖、OAS30→31 nullable 类型等。`FrozenV1ReleaseSurvivesEngineUpgrade` 在升级前构造真实 v1 输入/报告/评审/候选的固定 bytes，升级后发布启动和 Worker 构建能通过；新评审/未提交草稿不能用旧证据；真实资源变化/权限撤销仍拒绝；未知 EngineVersion 拒绝。报告/候选 SHA 与原值相等。
- [x] **Step 2：验证失败。** Run `./scripts/check-contracts.sh domain --filter 'OpenApiCompatibilityV2Tests|ContractFingerprintTests'` 与 `./scripts/check-contracts.sh integration --filter 'ComparisonEngineUpgradeTests|ComparisonReleaseTests|VersionRiskReviewTests'`。
- [x] **Step 3：实现。** v2 输入来源包、解析/format 配置入指纹；VersionComparisonService 通过任务 5 runner 调用子进程，子进程直接使用纯 ContractComparisonEngine.Compare，不递归调用 runner。Invalid/Unknown/coverage 汇总沿原 DTO。历史 v1 shape/引擎登记只用于证据复核；启动和 Worker 均使用冻结入口，不重算历史报告。不覆盖 v1 frozen 引用或增加全平台发布门禁。
- [x] **Step 4：验证通过。** 新测试加全部原 comparison 持久化/导出/审批发布/回滚恢复测试；超 5,000 输出和 10 秒执行均 Invalid，不保留可接受的“部分通过”。
- [x] **Step 5：提交。** commit `feat(comparison): upgrade OpenAPI rules with frozen evidence compatibility`。

## Task 9：Schema 树、原文编辑与示例验证 UI

**Files:** Create `console/src/contracts/{schema-state.mjs,schema-state.d.mts,SchemaTree.tsx,SchemaWorkbench.tsx,ExampleValidation.tsx}`、`console/tests/{schema-state,schema-view}.test.mjs`；Modify pages/SchemaEditor.tsx、styles/tokens.css，保留 ui.tsx 的原接口；Modify 新来源例选项 DTO 投影，仅附加可选值以兼容旧响应。

**Interfaces:** `applySchemaPatch(document,patch) -> document`（补丁 op=add/remove/replace/rename，pointer/value/newName）；`buildSchemaNodes(document,{budget:500}) -> {nodes,truncated}`；`validationMatches(result,{versionId,revision,schemaHash,exampleHash,formatMode}) -> boolean`。SchemaWorkbench props `{version,definition,readOnly,save,loadLatest,close}`，save 继续交给原批量 PUT/Revision；ExampleValidation 接收同一草稿、定义身份、方向和 formatMode，不自行写业务数据。

- [x] **Step 1：写失败测试。** Pointer ~ 与 /、父 required 改名、unknown 字段保留、boolean/组合/递归树、500 节点截断提示；`DirtyRawTextSurvivesDefinitionSwitchAttempt` 和 `LateValidationCannotOverwriteNewDraft`；`FalsyInstancesAreShownAndValidated`；SSR 检查只读/封存、错误位点可访问名称、显式保存和格式模式提示。
- [x] **Step 2：验证失败。** Run `N --test console/tests/schema-state.test.mjs console/tests/schema-view.test.mjs`；新状态/组件行为尚未满足时确认真实失败。
- [x] **Step 3：实现。** 树/原文共用草稿，不重建丢未知字段；语法错误暂停树编辑并保留文本；response/content tabs、多 examples（externalValue 显示未验证）、字段位置跳转、readonly 可验证。复用 Editor dirty/403/412/focus，校验结果绑定完整草稿身份和取消序号；Enter 不隐式保存。
- [x] **Step 4：验证通过。** 新 Node/SSR 测试与现有 editor 系列；`N console/node_modules/typescript/bin/tsc --noEmit -p console/tsconfig.json`。1440/1280 操作 QA 留到 Task 11。
- [x] **Step 5：提交。** commit `feat(console): add schema tree and example validation workbench`。

## Task 10：多入口导入及规则覆盖 UI

**Files:** Create `console/src/contracts/{import-state.mjs,import-state.d.mts,ImportSourcePolicyEditor.tsx,ImportSourceInput.tsx,ImportOperationMapping.tsx}`、`console/tests/{import-state,import-view,comparison-rule-view}.test.mjs`；Modify pages/OpenApiImport.tsx、pages/VersionCompare.tsx、styles/tokens.css、coverage.json。

**Interfaces:** `importIdentity(input) -> string`、`invalidatePreview(state,input) -> state`、`canCommitPreview(state,now) -> boolean`、`mapImportTarget(operation,target) -> ImportTarget`；沿用 apiRequest/Session/Workspace 接口。UI 仅根据服务端 DTO 展示预览支持性/规则覆盖，不自行计算风险。

- [x] **Step 1：写失败测试。** file/text/url 互斥、切换 Scope/来源后映射失效、到期拒绝但保留输入、不支持项不能勾选；URL/引用错误定位、403/412/政策改变恢复；不可配置来源角色没有保存入口。比较页 Limited/Unknown 和规则清单均显示，规则版/方言/说明/人工评审边界清楚。
- [x] **Step 2：验证失败。** Run `N --test console/tests/import-state.test.mjs console/tests/import-view.test.mjs console/tests/comparison-rule-view.test.mjs`。
- [x] **Step 3：实现。** 三来源入口、有界文件读取/附带文件、精确来源规则编辑、Operation 勾选及现有映射字段、固定 hash/expiry 显示、提交后到真实 API 导航。比较页附加规则清单/覆盖与方言，已有评审和发布交接行为保留；不加入新导航页面凑数量。
- [x] **Step 4：验证通过。** 新测试及全部 console/tests；Vite/tsc 通过。coverage.json 标注真实已接入页面；不修改受保护主工作区的旧覆盖文档。
- [x] **Step 5：提交。** commit `feat(console): complete import workflow and compatibility coverage UI`。

## Task 11：固定提交端到端、独立审查与合并

**Files:** Create `scripts/contracts/{acceptance.mjs,scenario.mjs,evidence.mjs}`、`tests/contracts/acceptance.test.mjs`、`docs/evidence/contract-management-completion/{verification.json,rule-coverage.json,result.md,report.html}` 和截图/实际日志；Create `.runtime/contract-management-completion/` 私有操作目录，不提交；Create `docs/deployment/contract-management-completion-runbook.md`。

**Interfaces:** `runContractAcceptance({revision,directory,cloneDirectory}) -> Promise<AcceptanceResult>`，Result 为 `{sourceRevision,imageId,passed,failed,notExecuted,checks,ruleCoverage}`；复用 `scripts/runtime/build.mjs` 的 `buildRelease({revision,directory,root,nodePath,nugetSeedVolume})`，只从 Git archive 构建，runner 只操作 UUID owner 对应的克隆。证据编号采用设计 C01–C15。

- [x] **Step 1：写验收器失败测试。** 错 commit/image 不接受、缺少规则覆盖不能完整通过、private 文件禁止入包、无归属不清理、模拟 UI 成功不能计作真实业务成功；Run `N --test tests/contracts/acceptance.test.mjs` 确认失败。
- [x] **Step 2：实现验收器并验证。** 使用独立契约 HTTP fixture 与真正克隆 PG/Redis/双网关，实际执行 URL YAML→固定预览→Schema→v2 比较→风险评审→两级审批发布→两网关请求/ACK/回滚；控制面进程/第三方正则强超时、原 JSON 路径、真实 403/412/授权撤销均验证。所有测试源/证据只引用本次提交，不引用历史通过数。
- [x] **Step 3：完整检查与浏览器 QA。** domain/integration/gateway 全量测试、Node console/runtime/contracts 测试、locked backend publish、tsc/Vite；通过 CUA 在克隆 1440/1280 真实操作导入和树/示例/评审/发布，验证 Tab、Escape、脏表单、Enter 不保存、晚响应不覆盖。记录非 JSON 验证边界及 bundle warning；未执行项不能计完成。
- [x] **Step 4：独立审查与修复。** 用户选择 Native 时使用 requesting-code-review 的独立最终 reviewer，选择 Subagent-driven 时逐任务审查后仍做整分支审查。重点检查规则误报、SSRF、历史证据/发布、作用域与 Registry 隔离。每个实际问题先重现测试，修复后按影响范围重跑；无未解决 P1/P2 后提交最终证据。
- [x] **Step 5：提交与合并。** 精确文件提交 `test(contracts): verify fixed-commit contract management workflows`；确认 M 工作区受保护文件/归档哈希不变，然后按既有 merge 工具模式将审阅提交合入 `feature/core-loop`；不夹带原七文件改动，不 push。合并后对实际合并 SHA 检查源码/锁文件一致性，重新构建固定包。

## Task 12：备份恢复验证、4192 安装与交付

**Files:** Create 私有 `.runtime/contract-management-completion/{preflight,build,operate,recovery,status,deliver}` 操作文件（相应 .mjs/.py）、对应 recovery tests；Create `docs/evidence/contract-management-completion/installed-status.json`、`docs/deployment/contract-management-completion-upgrade-result.md`、`deliverables/WebAPI_Enterprise_契约管理补全_4192部署验收_<实际日期>.zip`，不覆盖历史材料。

**Interfaces:** 按现有 permission-matrix-upgrade 的固定维护工具/归属检查实现，读取本批自己的 baseline 与 candidate-release；不修改旧工具或旧 baseline。Baseline 记录真实当时表内容、主工作区受保护文件、既有归档、20 个 Secret/SSO 文件和 96 个固定维护文件 SHA，公开状态仅打印非敏感计数/哈希。

- [x] **Step 1：验证部署保护。** 写失败测试证明原未提交文件变化、密码变化、旧包变化、错误 image/commit、未恢复克隆、升级失败时都阻止宣布完成；新迁移新增三表属于预期，原表内容必须保持，不能要求总表数永远不变。
- [x] **Step 2：冷备与恢复验收。** 按现有授权在升级窗口停止写入、完成平台/Keycloak 冷备，隔离恢复后验证原数据及新迁移；旧提交对旧数据加新表可启动，产生新 YAML 数据后仅使用兼容构建或另实例恢复，禁止用旧备覆盖含新业务的原实例。
- [x] **Step 3：安装实际合并 SHA。** 再次确认固定包哈希、restore proof、原数据/文件/凭据一致性；使用既有 runtime maintenance 工具升级 4192。原环境不登记测试来源、不导入、不评审/发布测试业务；新功能默认无 URL 允许项。
- [x] **Step 4：安装后验证。** 登录原账号只读检查新页面；确认安装 image/commit/静态 hash、平台/Keycloak 运行状态、双网关 Ready/LKG、既有业务请求成功及四个遥测源。校验原业务行、密码和保护材料，重复异常时先恢复服务，不宣称完成。
- [x] **Step 5：封存交付。** 公共 ZIP 仅含本批报告、规则目录、源码清单/补丁、日志和脱敏截图；CRC/SHA、私有路径/Secret 扫描通过。提交结果文档，报告实际本批完成状态及后两批尚待实施；只清理已核对 owner 的 QA 资源，保留备份和新/旧 image。

## 覆盖自查与执行交接

| 设计验收 | 计划任务 |
| --- | --- |
| C01/C02 | 1、4、10、11 |
| C03/C04 | 3、4、10、11 |
| C05/C06 | 2、4、5、10、11 |
| C07 | 3、4、6、11 |
| C08/C09 | 7、8、11 |
| C10 | 2、5 |
| C11/C12 | 5、6、9、11 |
| C13 | 8、11 |
| C14/C15 | 11、12 |

已按用户确认的 Native 方式连续实施全部 12 项任务，并完成一次独立整分支审查及同一批集中修复。五类 Review Focus 均有归属测试；最终首批固定提交完整回归 1473 项，失败 0、跳过 0，C01–C15 材料门禁及 30 条登记规则覆盖通过。复选框由任务执行账本、Git 历史和下列公开证据回填，不以当前服务可启动替代历史验收。

依赖核查依据（2026-10-07）：[YamlDotNet 18.1.0](https://www.nuget.org/packages/YamlDotNet/18.1.0)、[JsonSchema.Net 9.4.0](https://www.nuget.org/packages/JsonSchema.Net/9.4.0)、[BuildOptions/SchemaRegistry 官方说明](https://docs.json-everything.net/schema/basics/)、[固定 JSON Schema 测试集提交](https://github.com/json-schema-org/JSON-Schema-Test-Suite/commit/f6fd52a0a95472e079cbfc6ef7f089702b80e045)。包/API/许可证的实际锁定及 SDK 编译证据由 Task 1/5 提供，计划写作阶段不安装依赖。


## 完成证据核对（2026-10-08）

首批[固定提交验收](../../evidence/contract-management-completion/verification.json)绑定 `cc880ba`；[规则覆盖](../../evidence/contract-management-completion/rule-coverage.json)、[实际场景](../../evidence/contract-management-completion/scenario.json)、[界面操作](../../evidence/contract-management-completion/ui-proof.json)、[保护与恢复](../../evidence/contract-management-completion/protection.json)、[安装状态](../../evidence/contract-management-completion/installed-status.json)、[交付摘要](../../evidence/contract-management-completion/package-status.json)和[归属清理](../../evidence/contract-management-completion/cleanup-status.json)均保留原身份。

| 任务 | 已核实的执行结果 | 任务提交 | 最终证据归属 |
| --- | --- | --- | --- |
| 1 读取与依赖 | 14 项未实现行为失败、追加 6 项边界失败后修复；Domain 271/271，锁定构建完成 | `c789bcd` | C01/C02；domain、runtime-contracts |
| 2 引用与方言 | 18/18 引用及方言边界通过，累计预算有真实失败后修复；Domain 289/289 | `d28ff81` | C05/C06/C10；domain |
| 3 来源规则与 URL | Fetcher 19/19、规则端点 3/3 初始行为失败；修复后 Domain 311/311、Integration 411/411，迁移及编码路径保护验证 | `b6622d2` | C03/C04/C07；domain、integration |
| 4 固定预览与整批导入 | 首轮 31 项中 25 项行为失败；固定包、配额、幂等及数据库竞争修复后 Integration 452/452、Domain 317/317 | `c75f761` | C01/C04/C05/C06/C07；integration、场景 |
| 5 Schema 与期限进程 | 26 项语义和 4 项进程用例均记录失败后通过；1301 正式根实例与 842 Strict 实例两层执行，最终 Domain 366/366 | `c3e8bf5` | C10/C11/C12；domain、runtime-contracts |
| 6 来源一致性与只读 API | 首轮 32 项中 26 项真实行为失败；来源、MIME、Revision、只读及 Worker 回归后 Domain 366/366、Integration 488/488 | `7af57c0` | C07/C11/C12；integration |
| 7 Schema 证明规则 | 初始 143/143 行为失败；精度、完整父约束、引用副本预算等修复后 Domain 516/516、Integration 488/488 | `555da5b` | C08/C09；规则覆盖、domain |
| 8 OpenAPI v2 与冻结证据 | 新 Domain 36 项中 33 项真实行为失败；历史 v1 字节、当前 v2、范围响应与维护来源冲突均独立回归；最终分支补齐 30 条规则 | `cd93ac5` | C08/C09/C13；domain、integration、规则覆盖 |
| 9 Schema 工作台 | state 8/8、SSR 4/4 初始行为失败；接入后 console 141/141、目标 integration 40/40、tsc/Vite 通过；真实键盘及双宽度操作在任务 11 完成 | `05d9808` | C11/C12；console、界面操作 |
| 10 导入与规则展示 | state/SSR 初始 10/10 行为失败；后续边界修复，最终 console 154/154、tsc/Vite 通过；三入口及 Scope 操作在任务 11 完成 | `b45c471` | C01/C03/C04/C06/C08/C09；console、界面操作 |
| 11 固定验收与合并 | 验收门禁 5/5 和材料内部身份负例 13 项真实失败后修复；一次整分支审查的 9 项 Important 集中修复，响应冲突延伸 RED 2/2 后修复；最终固定构建、恢复、E2E、双宽度 CUA、1473 项完整回归及本地合并完成 | `d3b8c09` → `8031023` → `cc880ba` | C01–C15；verification、场景、界面、五套测试 |
| 12 备份与安装交付 | 保护门禁 2/2 真实失败后修复，保护/恢复最终 7/7；真实冷备及候选→旧→候选演练，原 4192 安装 `cc880ba`、账号/SSO/双网关/遥测复核、首批 ZIP 扫描及归属清理完成 | `fe9b85d`（结果文档） | C14/C15；保护、安装、交付、清理 |

首批最终五套结果分别为 [domain 620](../../evidence/contract-management-completion/tests-domain.json)、[integration 516](../../evidence/contract-management-completion/tests-integration.json)、[gateway 52](../../evidence/contract-management-completion/tests-gateway.json)、[console 157](../../evidence/contract-management-completion/tests-console.json)、[runtime-contracts 128](../../evidence/contract-management-completion/tests-runtime-contracts.json)。表中的逐任务数量来自当时执行记录，不与最终 1473 项重复相加。1301/842 为聚合测试内的实例数量，也不额外计入套件总数。

### 实际执行裁定与保留边界

- Task 1 的首次失败验证使用已存在的隔离 `check-policies.sh`，随后创建并验证 `check-contracts.sh`；后者当时尚不存在，不能声称首次直接运行了它。
- JsonSchema.Net 9.4.0、JsonPointer.Net 7.0.2、Json.More.Net 3.0.1 实际采用固定官方 MIT 源码自行编译，保留许可证、嵌入资源及逐文件 SHA；未采用原计划的对应 NuGet 二进制引用。实际依赖见[依赖记录](../../dependencies/contract-management.md)。YamlDotNet 仍按锁定依赖交付。
- 外部 boolean Schema 使 `ContractDocument.Root` 实际采用 `JsonNode`；新增资源读取、定义级身份及可选元数据，沿固定包传递。导入写入/配额/清理复用已有共享治理锁；网络仍在事务外。实际拆分与接口以任务提交源码为准。
- 子进程 compare 分发在 Task 8 随 v2 引擎接入完成；Task 5 单独阶段返回 Incomplete，未冒称已完成 v2 比较。最终子进程、预算、真实构建 DLL 及离线注册测试均执行。
- 官方完整套件的底层引擎与产品适配器分层执行；5 个自定义方言实例在产品侧明确 Incomplete。Strict 格式补充本地注册表及固定 Unicode 数据，未改第三方字节或过滤失败用例。
- 前端全套测试及 tsc/Vite 使用已安装的固定 Node 依赖执行，避免 pnpm 重建共享依赖目录；沙箱监听失败在获得授权后重跑完整套件，未通过 skip 隐去失败。
- 浏览器实际完成导入、编辑、校验、评审和发布向导至 WaitingApproval；两角色审批、发布、双 ACK/请求及回滚由真实 HTTP 场景完成，未将 HTTP 发布结果描述为 GUI 点击结果。
- `prior-803` 浏览器证据保持 `8031023` 身份；`cc880ba` 的新实际操作另记，两镜像 3 个前端静态文件 SHA 相同。未将旧浏览器执行重标为新提交。
- Minor M1 局部长行排版仍延期；复杂、动态、递归或未登记语义保守 Unknown/Incomplete，externalValue 未获取、未验证。首轮 Domain 的进程时序失败及工具延迟材料保留，最终结果与首次失败分列；详见[首批结果](../../evidence/contract-management-completion/result.md)。

### 当前项目状态与后续验收边界

截至本次回填，批准的本机功能补全已覆盖本批契约管理及后两批：[JWT/重试/缓存](2026-10-07-gateway-jwt-retry-cache.md)、[跨环境审批](2026-10-08-cross-environment-approvals.md)、[外部通知](2026-10-08-external-notifications.md)、[通知与审批交付](2026-10-08-notification-approval-delivery.md)。

当前原 4192 软件为 `adbcbed56a7e465c483b7462e4f3de9ac1156669`，运行镜像为 `sha256:e7b9c1324e0f880ea1813c69fa0b4ee8782ea8dcbcfd71a1212800bb1e7b902b`；固定产品六套封存回归 2042 项通过，固定包装源码 `b3f320b22242f8d10d17fef0aee9865573c0cf93` 的 Node 功能测试 208 项通过（其中 179 已计入 2042，另有 29 项 D3 门禁，不重复加总）。当前部署与交付证据见[通知与审批升级结果](../../deployment/notification-approval-upgrade-result.md)。本次进度同步只核查已有证据和实际服务状态，不把封存测试称为本次重新执行。

原实例 Email/Webhook 保持未配置、自动外发关闭；真实 SMTP/签名 HTTPS 及跨环境审批已在本机独立克隆验证，企业真实端点接入和生产验收尚未执行。40 页映射及导航接入已交付；高并发容量、生产可靠性和企业业务验收需要另外执行对应场景。本机功能完成不能替代这些验收。

历史首批及最新 ZIP 保持原封存字节，本次回填只进入仓库当前计划；最新交付包摘要保持 `4e32fb8e76f7b212eef1b528a9e4b3d62b762ba3073a05c509b9743294129f0b`，不因文档状态更新覆盖旧包。
