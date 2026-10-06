# API 版本比较与风险评审 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成真实版本比较、不可变风险评审记录和既有发布向导衔接，补齐第 11 页可操作能力。

**Architecture:** 比较引擎只处理契约输入；比较/评审服务负责授权、快照、指纹及追加存储。发布候选增加可选证据引用，在提交、发布启动和后台构建复核，沿用原审批与双网关 ACK。前端使用现有 Shell 和请求层，不新增框架。

**Tech Stack:** .NET SDK 10.0.401、ASP.NET Core、EF Core/PostgreSQL、xUnit；React 19.2.0、TypeScript 5.9.3、Vite 6.4.2、Node 原生测试。

**Spec:** [2026-10-06-version-comparison-design.md](../specs/2026-10-06-version-comparison-design.md)，用户于本轮确认；本计划同步明确指纹排除发布自动状态字段，以及历史列表支持 reviewId 解析，不扩大功能范围。

## Global Constraints

- 规则标识固定为 `compatibility-v1`；支持 OpenAPI 3.0 的 JSON 结构及受限 JSON Schema 子集。
- 单侧全部输入合计 2 MiB、两侧合计 4 MiB、JSON 深度 64、累计节点 50,000、发现 5,000；前端请求超时 15 秒。
- `changeKind = Added | Changed | Removed`，`risk = Compatible | Breaking | Unknown`，`coverage = Complete | Limited | Invalid`；变化数量与风险数量不相加。
- 报告不可修改，风险评审记录不可修改；Invalid 不允许评审或转入发布。
- AcceptedRisk 要求 confirmRisk=true 和去掉空白后 10–2,000 字说明；Reviewed 仅限 Complete 且无 Breaking/Unknown，备注最多 2,000 字。
- 完整契约同时要求 api.read、api.version.read、api.schema.read；评审另需 API 项目范围 api.approve 可写授权，不自动扩展角色或范围。
- 比较基线由用户选择；发布环境配置基线沿用原逻辑。关联证据是可选扩展，旧请求、回滚和重试保持行为。
- 路由 `/apis/{id}/versions/compare`；桌面 1440px 无页面水平溢出，不生成演示业务记录。
- 实现复用既有隔离工作树；原主目录六项本地修改和未跟踪证据保持。沿用既有不自动提交产品代码的约定，各任务先记录可审阅检查点；固定源码提交及服务升级在本批差异审阅后处理，不推送。
- 所有测试写入随机隔离数据库/环境，不能指向 `.runtime/local`。4192 账号、授权、业务版本、候选字节及配置制品不能因测试改变。

## Review Focus

以下为规格要求之外容易遗漏的具体输入/竞态，各行已落实到对应任务测试。

1. Task 1：发布自动把版本设为 Publishing/Published 并设置 SealedAt，契约未变时指纹应相同；业务保存增加 Revision 时必须不同。
2. Task 2/3：幂等重放发生在版本已变化之后，应返回原历史结果且不重复写入；撤权后的用户不能借原 key 重放受保护内容。
3. Task 4：带有风险说明的原始冻结候选不能通过通用 DTO 泄漏给仅有 release.read 的调用者；旧候选哈希保持原始字节计算。
4. Task 5：旧范围请求忽略 AbortSignal 并晚到、评审弹窗跨 Workspace 切换，旧结果不能恢复或提交到新范围。
5. Task 3：CSV 内容以空白/控制字符后接 `= + - @` 开头，仍需公式防护；元数据 example 的测试标记不能出现在报告展示或导出中。

---

## 文件职责与执行环境

路径均相对 enterprise 的已关联工作树 `.worktrees/gateway-restart-readiness`；执行前读取 Spec 与本计划。不要把旧未跟踪部署报告混入新变更。

| 文件组 | 职责 |
| --- | --- |
| src/WebApi.Contracts/Comparisons/ComparisonContracts.cs | 请求、报告/评审 DTO、游标页、冻结证据和安全摘要 |
| src/WebApi.Infrastructure/Comparisons/ComparisonInput.cs、ContractNormalizer.cs、ContractComparisonEngine.cs、SchemaCompatibilityRules.cs | 输入模型、规范化/指纹、Operation/参数匹配、方向性 Schema 规则 |
| 同目录 VersionComparisonService.cs、VersionRiskReviewService.cs、ComparisonCursorCodec.cs、ComparisonExporter.cs | 授权快照、追加留档、引用复核、绑定 API/过滤条件的游标、导出 |
| src/WebApi.Infrastructure/Persistence/Entities/{ApiVersionComparison.cs,ApiVersionRiskReview.cs}、Configurations/{ApiVersionComparisonConfiguration.cs,ApiVersionRiskReviewConfiguration.cs}、Migrations 的 ApiVersionComparisons 迁移 | 两张追加表及 EF 映射，不增加版本外键阻止原有删除 |
| src/WebApi.ControlPlane/Comparisons/ComparisonEndpoints.cs | Spec 的六个 API；统一 `/api/v1` 前缀、CSRF 和命令头 |
| src/WebApi.ControlPlane/ControlPlaneApp.cs、src/WebApi.Worker/WorkerApp.cs | 注册服务，Worker 必须能完成发布证据复核 |
| src/WebApi.Contracts/Releases/ReleaseContracts.cs、Infrastructure/Releases 的既有服务 | 向后兼容引用、提交冻结、发布/后台检查及安全 DTO |
| console/src/comparisons/{model.mjs,types.ts,VersionCompareView.tsx,RiskReviewDialog.tsx,ReleaseRiskReview.tsx} | 可测试状态、类型、展示、强确认、发布摘要 |
| console/src/pages/{VersionCompare.tsx,ApiDetail.tsx,ApiWizard.tsx,ReleaseDetail.tsx}、console/src/main.tsx、console/src/styles/tokens.css | 真实请求编排、入口与向导交接、路由和少量统一样式 |
| tests 的 Comparison*/VersionRiskReview*/ComparisonRelease*、console/tests/comparison*.test.mjs | 规则、安全/持久性、发布回归、页面行为测试 |
| scripts/comparisons/{acceptance.mjs,scenario.mjs,evidence.mjs}、tests/runtime/comparison-runner.test.mjs | 固定来源、独立临时部署、脱敏证据、所有权清理 |

后端验证复用 `bash scripts/check-policies.sh <domain|integration|gateway> -p:RestoreLockedMode=true`：现有脚本已创建随机 Compose 项目和私有密钥，测试库为 `test_<GUID>`。过滤用 `--filter FullyQualifiedName~Comparison`；不使用长期 core-test 或4192作为数据库。前端用 `bash scripts/check-console.sh`（Node/Pnpm 路径采用既有配置）；最终固定提交另做 Git archive 构建。当前 `console/node_modules` 是已有依赖链接，不能打包进交付。

## Task 1：确定性契约比较引擎与指纹

**Files:** 新建 contracts 与四个引擎文件；新建 `tests/WebApi.Domain.Tests/ContractComparisonTests.cs`、`ContractFingerprintTests.cs`、`SchemaCompatibilityTests.cs`。

**Interfaces:** `ContractVersionInput(VersionDto Version,IReadOnlyList<ParameterDto> Parameters,IReadOnlyList<SchemaDto> Schemas)`，`ComparisonInput(ContractVersionInput From,ContractVersionInput To)`；`ComparisonLimits` 默认值为 Global Constraints 预算；`ContractNormalizer.Fingerprint(ComparisonInput input,string engineVersion)` → string；`ContractComparisonEngine.Compare(ComparisonInput input,ComparisonLimits limits,CancellationToken ct)` → `ComparisonReport`。Report 含 EngineVersion、InputFingerprint、Coverage、Counts、Findings、CoverageIssues；Finding 含稳定 Key、Source、Operation、Pointer、ChangeKind、Risk、Reason、可空 Before/After 安全 JsonElement，不包含示例原值。CoverageIssue 含 Code、Source、Pointer、Reason；枚举契约使用字符串。Counts分别有Added/Changed/Removed/Compatible/Breaking/Unknown六个整数，不定义两类数量相加的总数；Unknown包含Source+Pointer+Code去重的Limited覆盖问题，不为这些问题伪造Added/Changed/Removed差异。

  示例断言（input 是测试内构造的请求 optional→required 契约）：
  ```csharp
  var report = new ContractComparisonEngine().Compare(input, new ComparisonLimits(), CancellationToken.None);
  Assert.Contains(report.Findings, finding => finding.Risk == "Breaking");
  ```

- [x] **Step 1：写规则 RED。** `RequestRequiredAndResponseOptionalHaveDirectionalRisk` 断言请求 optional→required=Breaking、响应 required→optional=Breaking；`EnumAndBoundsRespectDirection` 覆盖请求收紧/响应放宽=Breaking、反方向=Compatible。`OperationsAndParameterIdentityFollowSpec` 覆盖 Operation 新增/删除、header 大小写一致、其他参数改名/换位置=Unknown。按 Spec §4.2 表完整参数化，包含响应新增、类型/format、状态/ContentType、安全要求与未支持关键字。
- [x] **Step 2：写覆盖和指纹 RED。** `LimitedZeroDiffStillHasUnknown`、`CompositeAndCyclicReferencesNeverClaimComplete`、`DamagedAndDuplicateInputsAreInvalid`、`BudgetLimitsCannotBeAccepted` 覆盖精确预算和超出一个单位；`FingerprintIgnoresObjectOrderAndPublishingStateButNotRevision` 断言同 Revision 下键顺序、Status、SealedAt不影响，而 Revision、Schema结构、原始来源修改影响指纹。`PointerAndContradictorySourcesArePreserved` 检查转义及不同来源矛盾。运行 domain 过滤，新增类型/规则尚缺时必须失败。
- [x] **Step 3：实现输入、规范化与规则。** 排序业务键，嵌套 JSON 字符串解析后 canonical；保留 required/enum 集合语义，其他数组有序。每侧源与管理结构分别检测；只在确定映射一致时去重。深度/节点/字节/发现预算和取消贯穿遍历；仅解析本地引用，不发网络请求。类型缺失、方向缺失、组合和未知关键字按 Spec 分类，不复用导入器的宽松“3.x 即支持”判断。未变化但存在不支持结构仍输出覆盖 Unknown；风险聚合 Breaking 优先其次 Unknown。
- [x] **Step 4：运行 GREEN。** `bash scripts/check-policies.sh domain -p:RestoreLockedMode=true --filter 'FullyQualifiedName~ContractComparison|FullyQualifiedName~ContractFingerprint|FullyQualifiedName~SchemaCompatibility'`；所有新规则及预算用例通过。记录精确输入/输出的脱敏测试证据，不声称完整 OpenAPI 兼容性证明。
- [x] **Step 5：检查点。** `git diff --check`，记录新增文件和 RED/GREEN 结果；产品代码保持可审阅，按 Global Constraints 处理提交。

## Task 2：比较存储、授权快照及历史/导出 API

**Files:** 新建文件职责表中的两张 entity/config（第二张供 Task 3 使用）、`VersionComparisonService.cs`、`ComparisonCursorCodec.cs`、`ComparisonExporter.cs`、`ComparisonEndpoints.cs`；修改 ControlPlaneApp 注册/映射、AuditedCommandExecutor 摘要捕获；生成 `Persistence/Migrations/20261006090000_ApiVersionComparisons.cs`、同名 Designer、更新 ModelSnapshot。DbContext 已使用 ApplyConfigurationsFromAssembly，无需新增DbSet或修改发现机制。新建 `tests/WebApi.Integration.Tests/ComparisonPersistenceTests.cs`、`ComparisonCommandTests.cs`；新建 `tests/WebApi.TestSupport/Support/ComparisonFixture.cs`。

**Interfaces:** `CreateVersionComparisonRequest(Guid FromVersionId,Guid ToVersionId,long ExpectedFromRevision,long ExpectedToRevision)`；Service 的 `CreateAsync(Guid apiId,CreateVersionComparisonRequest request,ActorContext actor,CancellationToken ct)` → `Task<VersionComparisonView>`；`GetAsync(Guid comparisonId,ActorContext actor,CancellationToken ct)` → 同类型；`ListAsync(Guid apiId,string? cursor,int? limit,Guid? reviewId,ActorContext actor,CancellationToken ct)` → `Task<ComparisonPage<ComparisonSummaryDto>>`；`ReadSnapshotAsync(Guid apiId,Guid fromVersionId,Guid toVersionId,ActorContext actor,CancellationToken ct)` → `Task<ComparisonInput>`，供评审服务复用。View 含不可变 Report、Id、版本标识、CreatedAt、Freshness、最近评审摘要。`ComparisonPage<T>(IReadOnlyList<T> Items,string? NextCursor)`；`ExportAsync(Guid id,string format,ActorContext actor,CancellationToken ct)` → `Task<ComparisonExport(byte[] Bytes,string ContentType,string FileName)>`。

  示例断言（originalBody/replayBody来自相同key的两次HTTP命令）：
  ```csharp
  Assert.Equal(originalBody, replayBody);
  Assert.Equal(1, await db.Set<ApiVersionComparison>().CountAsync());
  ```

- [x] **Step 1：写持久化与权限 RED。** `MigrationAddsOnlyTwoEmptyTables` 检查字段/bytea/枚举/64字符哈希约束和索引、ComparisonId 外键、无版本删除外键及旧数据不变；`ReadPermissionsAreIndependent` 逐项撤销三种权限，所有生成/查询/导出拒绝；`ForeignApiAndVersionCannotBeProbed` 验证不同API=422、无可见范围=404；`ReportSurvivesNewServiceScopeAndSourceDeletion` 验证追加快照持久及 Missing 不丢历史。fixture 只给测试角色补需要的权限，不能扩大 ApiFixture 的默认权限。
- [x] **Step 2：写事务/重放与游标 RED。** `ComparisonReplayReturnsOriginalAfterEdit` 同 key 不重复报告/审计，版本改后重放响应字节仍等于原响应；独立 GET 的 Freshness 应为 Stale，不能改写幂等记录。`RevokedUserCannotReplayOrExport` 撤权先于幂等查找；`SameKeyDifferentPairConflicts`=409；`ExpectedRevisionMismatchIs412`；`ComparisonCursorBindsApiAndReviewFilter` 检查分页 CreatedAt+Id 排序、上限50、非法/跨API/换过滤条件游标拒绝。运行 integration 过滤验证新增表/API失败。
- [x] **Step 3：实现存储与服务。** 生成报告在 AuditedCommandExecutor 锁内授权、校验scope及同API、幂等 callback读取快照、验证Revision、比较、追加两份字节/摘要。指纹仅排除 Spec 明确的生命周期/只读创建字段；API范围迁移或删除不能继续读取旧报告。列表默认20、最大50，reviewId过滤只返回该API所属报告，不允许全局反查；游标用现有 DataProtection 保护查询绑定值。JSON/CSV导出为全量结构报告，排除原文/示例值。枚举用字符串契约，不把数据库内部字节直接回传。
- [x] **Step 4：实现端点与审计并跑 GREEN。** `/api/v1` 下映射 Spec §6.3，命令需要现有CSRF/Idempotency-Key，读取no-store；审计特定捕获包含版本/指纹/规则/数量，不含 InputBytes/ReportBytes。迁移用既有锁定 dotnet-ef 工具生成，人工核对 SQL 仅追加两表；真实 PostgreSQL integration 全通过，保存迁移 SQL 和结果。
- [x] **Step 5：检查点。** 核对 actor/项目/环境范围与初始权限无变化；记录新API契约与游标/重放规则，不提交历史证据文件。

## Task 3：不可变风险评审、有效性校验与安全导出

**Files:** 新建 `VersionRiskReviewService.cs`；扩展 Task 2 contracts、ComparisonEndpoints、AuditedCommandExecutor；新建 `tests/WebApi.Integration.Tests/VersionRiskReviewTests.cs`、`ComparisonExportTests.cs`，扩展 ComparisonFixture。

**Interfaces:** `CreateRiskReviewRequest(string ExpectedReportHash,string Decision,string? Comment,bool ConfirmRisk=false)`；`RiskReviewDto` 包含 Id、ComparisonId、ApiId、InputFingerprint、ReportHash、EngineVersion、Decision、Comment、ActorId、CreatedAt。`CreateAsync(Guid comparisonId,CreateRiskReviewRequest request,ActorContext actor,CancellationToken ct)` → `Task<RiskReviewDto>`；`ListAsync(Guid comparisonId,string? cursor,int? limit,ActorContext actor,CancellationToken ct)` → `Task<ComparisonPage<RiskReviewDto>>`。`ResolveReferencesAsync(ScopeRef environmentScope,IReadOnlyList<Guid> targetVersionIds,IReadOnlyList<Guid>? reviewIds,ActorContext actor,CancellationToken ct)` → `Task<IReadOnlyList<FrozenRiskReviewReference>>`；`ValidateReferencesAsync(ScopeRef environmentScope,IReadOnlyList<Guid> targetVersionIds,IReadOnlyList<FrozenRiskReviewReference>? references,ActorContext actor,CancellationToken ct)` → `Task`，仅普通发布在当前契约上验证。FrozenRiskReviewReference 此任务定义在ComparisonContracts，包含 Spec §7 的标识与可信评审摘要，说明字段仅在授权视图回传；不依赖Task4尚未新增的Candidate属性。

  示例断言（invalid是Invalid报告的真实评审响应）：
  ```csharp
  Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
  Assert.Equal(0, await db.Set<ApiVersionRiskReview>().CountAsync());
  ```

- [x] **Step 1：写决定与授权 RED。** `ReviewedRequiresCompleteAndNoRisk`、`RiskAcceptanceRequiresStrongConfirmationAndCommentBounds` 测9/10/2000/2001字符及空白，采用 Unicode 标量计数；`InvalidCannotBeReviewed`；`ApproveRequiresProjectWriteScopeAndAllReadPermissions` 验证环境-only/readonly/角色同名不能绕过。`ReviewAppendsWithoutApprovalTask` 检查评审持久、审计恰好一次且 ApprovalTask 数量不变。
- [x] **Step 2：写新鲜度、并发和导出 RED。** `EitherVersionEditDeleteOrScopeMoveInvalidatesReview`；`ReviewReplayAfterEditIsHistoricalButRevocationDeniesReplay`；`AuthorizationChangeAndReviewShareTransactionLock` 检查先撤权者不会完成受保护写入。`ExportsNeverRevealExamplesOrRawDocument` 放入合成标记并验证展示/JSON/CSV无标记；`CsvGuardHandlesLeadingWhitespaceAndControlCharacters` 验证四种公式前缀、分隔符/换行、覆盖问题行存在。运行 integration 过滤见预期失败。
- [x] **Step 3：实现评审与引用验证。** 锁内先授权，再幂等重放；新命令才检查哈希、全量风险、InputFingerprint和版本存在性，追加记录。审计专用捕获记录决定/Actor而无备注全文。ResolveReferences 校验重复ID、每API最多一份、目标一致、组织项目一致、报告/评审哈希一致、合法决定和当前引擎版本；缺失/越权404，合法旧记录但不再新鲜返回 risk_review_stale。空列表无需契约扩展授权。原评审人后续撤权不删除历史事实；当前执行者权限仍检查。
- [x] **Step 4：运行 GREEN。** integration 中风险评审、ComparisonExport和Task2用例全通过；检查原报告/评审字节不被更新，没有新增角色、环境范围或正式审批任务。完整报告读取有同一权限约束，不能通过最近评审说明间接越权。
- [x] **Step 5：检查点。** 保存身份/范围矩阵、哈希与状态失效证据；代码仍按全局提交约定处理。

## Task 4：发布候选引用、两阶段复核与旧记录兼容

**Files:** 修改 ReleaseContracts.cs、ReleaseService.cs、ReleaseCandidateBuilder.cs、PublishCoordinator.cs、ControlPlaneApp.cs、WorkerApp.cs；审查 ReleaseRecoveryService.cs/RollbackService.cs，只有兼容问题才做最小修改。新增 `tests/WebApi.Integration.Tests/ComparisonReleaseTests.cs`、`ComparisonReleasePrivacyTests.cs`；扩展 ComparisonFixture，复用 DeploymentScenario。

**Interfaces:** `CreateReleaseRequest(...,IReadOnlyList<Guid>? RiskReviewIds=null)`，`FrozenReleaseCandidate(...,IReadOnlyList<FrozenRiskReviewReference>? RiskReviewReferences=null)`，`FrozenCandidateView(...,IReadOnlyList<RiskReviewSummaryDto>? RiskReviews=null)`，均追加末尾可选参数保持旧构造调用兼容。Summary 含版本标识、数量、覆盖、决定、Actor、时间、哈希，Comment仅有契约读取权限者可见；不加入InputBytes/ReportBytes。PreviewReleaseRequest 保持原有字段；创建前独立解析评审，预览草稿时展开可信引用。

  示例断言（后台发现比较基线版本变化后）：
  ```csharp
  Assert.Equal("Failed", release.Status);
  Assert.Equal(beforeSequence, environment.DeploymentSequence);
  Assert.Equal(beforeConfig, environment.DesiredConfigVersion);
  ```

- [x] **Step 1：写关联生命周期 RED。** `CreateChecksTargetScopeAndReviewFreshness`、`RefreshCannotDropReviewIds`、`SubmitFreezesServerEvidence`、`FromOrToChangesBeforePublishAreRejected`。冻结摘要采用服务端记录，构造客户端额外证据对象/假哈希不能覆盖结果。风险评审不能使生产申请跳过 WaitingApproval，原申请人自批和两步骤人员隔离测试继续成立。运行 integration 过滤。
- [x] **Step 2：写后台竞态与隐私 RED。** `BaselineEditAfterStartFailsBuildWithoutDesiredVersionAdvance`：Start成功后修改比较基线，BuildNext失败且env DesiredConfigVersion/DeploymentSequence、GatewayConfigSnapshot数不变；`PublishSealingAloneDoesNotInvalidateContractFingerprint`；`ReleaseOnlyReaderCannotReadReviewCommentOrReportFragments` 同时检查预览与详情 JSON；`LegacyCandidateBytesHashAndRollbackRemainUnchanged` 缺新字段的旧候选可读，CandidateHash仍SHA原始字节，回滚/重试忽略当前比较基线新鲜度。
- [x] **Step 3：实现引用生命周期。** Create保存经过验证的RiskReviewIds；Draft Preview/Refresh重新解析且保留；Submit把可信对象及摘要写入新冻结候选。旧CandidateBytes永不重写；新可选字段缺失/null都当空。普通发布Start锁内和BuildNext原有锁内调用ValidateReferencesAsync，传当前环境Scope、Candidate.VersionIds与RiskReviewReferences，在设置Building/写Config/递增seq之前完成；所有目标revision与原cohort检查保留。若自动恢复类型不调用新的当前比较门禁，继续复用历史制品。DI在ControlPlane及Worker均注册；引擎不触及Gateway运行协议。
- [x] **Step 4：构建授权视图并跑 GREEN。** ReleaseDto及FrozenCandidateView经过权限分支序列化，风险说明无权时不输出；所有包含候选的路径共用安全构造方法。运行全部 integration 和 gateway，一并验证既有 Approval/Publish/Ack/Rollback 用例；SnapshotCompilerTests证实评审数据不会进入RuntimeSnapshot，双节点确认目标数量保持2。
- [x] **Step 5：检查点。** 保存旧候选原始SHA、失败前后环境值与两网关ACK证明；不在4192创建风险/发布测试数据。

## Task 5：比较页面、强确认与发布向导交接

**Files:** 新建前端 comparisons 文件及 pages/VersionCompare.tsx；修改 main.tsx、ApiDetail.tsx、ApiWizard.tsx、ReleaseDetail.tsx、styles/tokens.css；新建 `console/tests/comparison-state.test.mjs`、`comparison-view.test.mjs`、`comparison-handoff.test.mjs`，补 navigation.test.mjs 的compare路由用例。

**Interfaces:** model.mjs 导出 `comparisonQueryState(query)`、`filterComparison(report,filters)`、`reviewCommand(report,decision,comment,confirmRisk)`、`loadComparison(request,scopeKey,signal,onResult)`、`resolveReviewHandoff(apiId,reviewId,request,signal)`。View消费report、freshness、版本列表、筛选、授权按钮及回调；请求/权限/路由编排留在VersionComparePage。handoff用 `/apis/{id}/version-comparisons?reviewId=...` 解析ComparisonId，再GET报告和reviews查可信目标，空/越权不能继续。所有请求复用apiRequest与统一认证。

  示例断言（unknownReport为零普通差异但有覆盖问题的报告）：
  ```javascript
  assert.equal(unknownReport.coverage, 'Limited');
  assert.ok(unknownReport.counts.unknown > 0);
  assert.deepEqual(filterComparison(unknownReport, {risk: 'Breaking'}), []);
  ```

- [x] **Step 1：写状态和交接 RED。** `UnknownCoverageSurvivesEmptyFilter`、`FilteredRowsNeverChangeReviewTotals`、`VersionSelectionClearsEffectiveReview`、`WorkspaceAbortRejectsLateTransport`；`ReviewHandoffUsesServerTargetAndLocksWizardVersion` 检查只携ReviewId且目标从服务端解析；`ChangedTargetCannotCarryOldReview`、`FailedCreateRetainsCommentAndSameRetryKey`、`CommandTimeoutIs15Seconds`。用真实可执行 model 函数测试，不用源码字符串断言代替行为。
- [x] **Step 2：写呈现 RED。** SSR模式参照WorkbenchView tests；`CompareRoutePrecedesGenericDetail` 验证具体路径解析结果；`NoVersionsAndDeniedStatesHaveNoEnabledActions`、`BreakingDialogHasFullCountsRequiredCommentAndCheckbox`、`ManualChangeTypeIsSeparateFromDetectedRisk`、`ReleaseSummaryDoesNotInventReviewForLegacyCandidate`。执行 `node --test console/tests/comparison*.test.mjs`，缺模型/组件时必须失败。
- [x] **Step 3：实现比较与评审页面。** 版本选择→POST生成→GET更新；历史列表/报告用服务端游标。风险与变化分开展示，覆盖问题始终可见；行详情可键盘打开、安全片段内部滚动，完整原文/示例不回显。弹窗按全量风险检查并发送期望hash，412/过期清除有效评审，备注保留；跨范围卸载取消并清理。比较/评审权限独立，能读不能评审的人仍可比较导出。
- [x] **Step 4：实现向导/发布摘要并跑 GREEN。** main先匹配compare路径；ApiDetail加入口；ReviewId query进入ApiWizard第六步，固定目标并校验环境scope，创建body包含RiskReviewIds。版本切换明确退出关联模式并提示重新比较，不能默认继续旧review；草稿提交失败不丢已创建ID。ReleaseRiskReview展示可信授权摘要，原审批与ACK组件保持。运行 `bash scripts/check-console.sh`：全部Node测试通过、tsc无错、Vite构建成功。
- [x] **Step 5：检查点。** 记录可操作路径、空态/权限/过期行为，不使用mock报告撑满页面。临时预览端口选可用端口，不接管4180/4192/4193/4194。

## Task 6：隔离验收、设计 QA 与可审阅交付

**Files:** 新建 scripts/comparisons 三个文件、`tests/runtime/comparison-runner.test.mjs`、`docs/evidence/version-comparison/result.md` 和脱敏JSON/截图；修改 `docs/console-coverage.md` 第11页、相关真实能力说明。新增交付包名 `WebAPI_Enterprise_版本比较与风险评审_20261006.zip`，若交付日期变化使用实际日期，禁止覆盖历史包。

**Interfaces:** `runComparisonAcceptance({root,revision,retainForReview=false})` → 证据清单；`runComparisonScenario(context)` → API/权限/发布验证结果；`validateComparisonResources({project,owner},labels)` 严格限制 `webapi-enterprise-e2e-comparisons-<UUID>`，不能清理长期部署。evidence.mjs 的 `finalizeComparisonEvidence(proof)` 只有API/权限/发布/UI全部真实通过且本批临时资源清理完成时返回complete=true，否则返回complete=false及原因；保留review环境阶段只能是部分证据。复用既有固定来源与部署工具的所有权机制，新增前缀的guard应是专用实现，不能直接传入只允许settings前缀的验证函数。

  示例断言（foreignLabels属于不同owner）：
  ```javascript
  assert.throws(() => validateComparisonResources(context, foreignLabels));
  ```

- [x] **Step 1：写隔离执行器 RED。** `RunnerRefusesLocalDirectoryAndForeignOwner`、`FailureCannotEmitPassedEvidence`、`UnknownOrUnverifiedUiCannotFinalizeDelivery`、`ArchiveExcludesSecretsPrivateInputsAndDependencies`。证据finalize必须有真实API、权限、发布和UI记录，不能靠HTTP200替代交互QA。执行 `node --test tests/runtime/comparison-runner.test.mjs` 见失败。
- [ ] **Step 2：实现隔离执行器并审阅源码。** 执行器校验随机项目/owner、私有目录0700/秘密0600、可用loopback端口；使用传入的明确Git revision archive构建，不能把HEAD标签贴到混有工作区修改的来源。运行domain/integration/gateway全部、console全部、runtime执行器测试；按选定方式完成独立审查并修正问题。出示本批差异/结果并沿用本地源码提交授权流程，固定本批已审阅提交后才进入下一步；本计划批准不代表合并旧材料或推送。
- [ ] **Step 3：运行固定源码隔离验收。** 用前一步提交运行 `node scripts/comparisons/acceptance.mjs --revision <本批实际提交> --review`，执行器必须核验来源清单/镜像/工具哈希。申请人、风险评审人和两级正式审批人用隔离账号，授予恰当项目读取范围；不能改长期用户。HTTP场景包含真实报告、AcceptedRisk、向导payload、正式审批、双网关ACK和旧无引用发布回归。保留UI验收环境至下一步完成，清理前逐项验证owner，秘密不入公开报告。失败修复需重新审阅、更新固定源码并重新验收，不能用旧镜像证明新源码。
- [ ] **Step 4：真实界面 QA。** 1440×1024截图覆盖比较、Unknown、强确认、成功留档、向导交接、发布摘要；浏览器实际操作版本选择/生成/筛选/详情/导出/强确认/返回/环境切换。验证页面宽度、文字层级、长字段滚动、键盘/焦点、无越权按钮和晚到数据。只有1个版本或当前账号无权限时给真实空态，不造生产示例。记录已经验证的界面动作与未验证条件；调用执行器 `--cleanup <本批私有目录>` 校验owner后清理，读回资源数及秘密文件数为0，再调用finalizeComparisonEvidence。
- [ ] **Step 5：交付与升级边界。** 输出检查结果、中文说明、源码标识、截图及ZIP，CRC/读回SHA/秘密标记扫描通过。第11页标注真实路由，其他39页不顺带改为完成。若进入4192升级，先复核实时源码/镜像/业务v及seq、原数据/授权/配置字节、全部历史ZIP和工具哈希，再执行已审阅冷备份与新镜像切换；暂停服务和迁移属于本批升级的最终授权步骤，未授权时保留现网，交付可操作隔离预览。不能把源码完成或隔离验收写成已部署/生产验收。

## 自查与待执行状态

规格覆盖映射：§1–3目标/边界→Tasks1/4/5；§4规则/预算→Task1；§5存储/指纹→Tasks1/2；§6权限/审计/API/导出→Tasks2/3；§7发布→Task4；§8界面→Task5；§9迁移/验收/交付→Tasks2/6。Review Focus五项分别已有对应RED用例。

用户已选择 A 本会话执行。Tasks 1–5 已完成源码实现和自动化检查；Task 6 的固定源码隔离验收、真实 UI QA、最终交付和4192升级仍按上述边界推进。未操作4192业务数据或切换服务。
