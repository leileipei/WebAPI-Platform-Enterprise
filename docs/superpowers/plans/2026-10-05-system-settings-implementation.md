# 平台系统设置 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将第 40 页五组十七字段接入真实配置治理，完成四类实际消费者、受控审计 CSV 和可操作的高保真页面。

**Architecture:** 控制面以五行平台设置 JSONB 保存分组，沿用授权事务锁、修订、幂等及审计；强类型校验器和默认读取器供管理服务及业务消费者共用。前端复用现有 Shell、会话、请求与编辑保护，网关继续消费正常发布快照。隔离 PostgreSQL、双网关及真实浏览器生成独立验收证据。

**Tech Stack:** 现有 .NET / ASP.NET Core、EF Core / PostgreSQL、DataProtection、xUnit、React / TypeScript / Vite、Node 测试、Docker Compose、Playwright；沿用锁定依赖。

运行约定：新工作树准备完毕后，命令均在其根目录执行。设置 `WEBAPI_NODE=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node`、`WEBAPI_PNPM=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/bin/fallback/pnpm` 和 `pnpm_config_verify_deps_before_run=false`；本文 `node` 命令使用该已安装 Node，沿用已批准依赖缓存，不自动安装。

**Spec:** [已确认设计](../specs/2026-10-05-system-settings-design.md)，2026-10-05 用户确认。用户已选择 A 并批准执行；不代表已实现或已部署。

## Global Constraints

- 从 `79990836dedb5a3d8962ae2f828090b08208aff2` 建立独立系统设置工作树；使用 using-git-worktrees 技能，先核对可复用附件。保留现有改动、封存证据及 4192 数据。
- 仅平台范围：`scope_type=system`、`scope_id=null`；权限 `system.manage` 加系统 PlatformAdmin 身份。无组织/项目覆盖、SSO、外部发送、秘密解析、自动清理或网关热更新。
- 五键固定为 `system.security/release/gateway/audit/notification`；迁移空表，未保存修订 0、首写修订 1；读取与预览不插入数据。
- 全部十七字段及验证范围以 Spec §3 为准；兼容默认为 TTL 480 分钟、密码最短 16、LengthOnly、新路由超时 30000ms；已有密码最大长度 1024 继续保留。
- 生产审批固定两级；待部署及配置意向不得显示已生效。通知测试仅 `StructuralOnly`，不得连接、发送或解析引用。
- 保存必须 CSRF / Origin、If-Match、Idempotency-Key、五分钟确认凭证；共享 `pg_advisory_xact_lock(8901202)`。缺少 If-Match 为 428，修订冲突 412，不同语义复用键 409。
- SecretRef 仅 `vault://<provider>/<path>`，写操作 Keep / Replace / Clear；读取、预览、错误、审计、CSV 均不回显原文。
- 设置请求 JSON 限制 8192 字节，重复/未知属性和类型错误拒绝；HTTP 413 表示超限，字段验证 422，未知分组 404。8192 是本计划对 Spec“超限”的具体实现值。
- CSV 最多 10000 行、8MiB；同一授权范围与过滤条件，输出行数及截断标志。最终准入之前撤权/关闭则丢弃缓冲，准入后不承诺撤回已发送数据。
- 桌面 1440px QA；源码、镜像、测试和截图分别记录实际身份。未提交源码用实际文件摘要，不把 HEAD 当作全部构建内容。
- 不自动提交、推送、合并或升级 4192。每任务以可审阅差异与验证记录作为检查点，覆盖技能的默认 commit 步骤；后续部署另需具体授权。

## Review Focus

1. 首次保存竞争或凭证到期后的成功重试：一个首写获胜；原成功请求重放不增修订/审计，撤权者不可重放（任务 3）。
2. 通知引用 Keep / Clear 与缺项组合：保持不丢值、清除遵守完整性、错误与旧请求响应不泄漏引用（任务 1、3、6）。
3. 旧业务省略或显式 timeout、已有票据与 OpenAPI 覆盖：新默认只作用于批准的消费者，已有配置和票据保持原含义（任务 4、7）。
4. 导出过程中授权/开关变化及 CSV 公式输入：重验、丢弃、截断和转义可验证，无跨范围数据（任务 5）。
5. 前端撤权与乱序响应遇到脏表单/412：保留合法草稿，失效时清空敏感草稿，旧响应不能恢复数据（任务 6）。

---

## 文件与接口约定

所有路径相对新工作树根目录。现有基础设施只定点扩展；不顺带重构业务模块。

| 文件 | 职责 |
|---|---|
| 新建 `src/WebApi.Contracts/Settings/SystemSettingsContracts.cs` | 请求、分组响应、引用操作、字段来源/生效分类和路由默认 DTO |
| 新建 `src/WebApi.Infrastructure/Settings/SettingsValues.cs`、`SystemSettingsValidator.cs` | 五组内部强类型持久值、严格 JSON 解析与纯校验 |
| 新建 `src/WebApi.Infrastructure/Persistence/Entities/SystemSetting.cs`、`Configurations/SystemSettingConfiguration.cs` | 原模型列、平台范围约束与修订 |
| 新建 `src/WebApi.Infrastructure/Settings/SystemSettingsReader.cs`、`SystemSettingsService.cs`、`SettingsPreviewProtector.cs` | 兼容默认、治理命令及预览凭证 |
| 新建 `src/WebApi.ControlPlane/Settings/SystemSettingsEndpoints.cs` | 五组 API 和受范围保护的 route-defaults |
| 新建 `src/WebApi.Infrastructure/Governance/AuditAccessQuery.cs`、`AuditExportService.cs`、`AuditCsvFormatter.cs` | 复用审计授权、缓冲导出和安全列格式 |
| 新建 `console/src/pages/SystemSettings.tsx`、`console/src/settings/state.mjs`、`state.d.mts`、`contracts.ts` | 页面、状态转换及 API 类型 |
| 新建 `scripts/check-settings.sh`、`scripts/settings/{acceptance,scenario,browser-qa,evidence}.mjs`、`scripts/package-settings.py` | 隔离执行、真实场景、证据与封装 |

公共约定：`SettingsGroupDto(string Group,long Revision,JsonElement Values,IReadOnlyList<SettingFieldMeta> Fields)`；Values 按分组输出固定字段 schema，通知使用安全读取 schema，服务内部必须强类型。`SettingFieldMeta(string Key,string Source,string Effect)` 来源/生效枚举使用 Spec §6 的原名。

写入 DTO：`SettingsMutation(JsonElement Values)`；通知 Values 以 `smtpSecretRef/webhookSecretRef: SecretReferenceMutation(string Operation,string? Reference)` 表示操作。读取这两字段为 `SecretReferenceState(bool HasConfiguredReference,string? Provider)`，无 Reference。`SaveSettingsRequest(JsonElement Values,string ConfirmationToken)` 与 mutation 使用同一 Values schema。非引用字段为整组替换，不允许部分补丁。

内部五组值：`SecuritySettings(int SessionTtlMinutes,int PasswordMinLength,string PasswordComplexity,string[] AllowedOrigins)`；`ReleaseSettings(int ProductionApprovalLevels,int SnapshotRetentionCount)`；`GatewaySettings(int DefaultRouteTimeoutMs,int MaxRequestBodyMb,int ConfigRefreshIntervalSeconds)`；`AuditSettings(int AuditRetentionDays,bool AuditExportEnabled)`；`NotificationSettings(string? SmtpHost,int? SmtpPort,string? FromEmail,string? SmtpSecretRef,string? WebhookUrl,string? WebhookSecretRef)`，均继承 `SettingsValues`。引用原文仅内部持久值可访问。

## Task 1：严格分组契约与验证

**Files:** 新建上表 contracts、SettingsValues、validator；新建 `tests/WebApi.Domain.Tests/SystemSettingsValidationTests.cs`、`tests/runtime/settings-runner.test.mjs`、`scripts/check-settings.sh`。

**Interfaces:** 产出 `SystemSettingsValidator.Parse(string group,ReadOnlyMemory<byte> json)` → `SettingsMutation`（读取完整 `{values:...}` 请求）、`ParseSave(string group,ReadOnlyMemory<byte> json)` → `SaveSettingsRequest`（读取 `{values:...,confirmationToken:...}` 请求），以及 `Resolve(string group,SettingsMutation mutation,SettingsValues current)` → `ValidatedSettings(SettingsValues Value,string CanonicalValueJson,string ValueHash,IReadOnlyList<string> ChangedFields)`。两个 Parse 均在属性转换之前递归拒绝重名/未知属性，检查整个请求体大小；Resolve 应用引用操作后完整验证。错误仅字段键与固定原因，不附输入值。

- [x] **Step 1: 写失败测试。** `RejectsInvalidBoundsAndTypes` 参数覆盖 Spec 十七字段的边界及相邻非法值；`RejectsDuplicateUnknownAndOversizeJson` 断言有效 JSON 加空白至8192可入解析、8193→413，外层/内层重复或未知、null/array根→422；`RejectsUnsafeOriginsAndNotificationInputs` 覆盖 wildcard、userinfo、query、fragment、控制字符、缺 provider/path；`ReferenceOperationsPreserveAndValidateCompleteness` 断言 Keep 保持原引用、Replace 必须有值、Clear 后缺项→422、全空合法，非 Replace 携带 Reference→422。`ProductionApprovalCannotBeOne` 断言 1→422。

  核心断言（Parse / ParseSave / Resolve 为 public static）：
  ```csharp
  Assert.Equal(413, Assert.Throws<ApiException>(() => SystemSettingsValidator.Parse("security", new byte[8193])).Status);
  ```
- [x] **Step 2: 验证 RED。** 新 wrapper 的 domain/integration/gateway/console 调用现有 `scripts/check-policies.sh` 相应模式，前三模式传入 `-p:RestoreLockedMode=true`，保留随机数据库与资源所有权机制；e2e/browser/verify 暂未实现则明确非零退出。运行 `bash scripts/check-settings.sh domain`，应因新增类型/规则缺失失败；运行 `node --test tests/runtime/settings-runner.test.mjs`，证明 wrapper 不触碰 `.runtime/local`、失败不被吞掉。
- [x] **Step 3: 实现解析与验证。** 固定五组字段名，数值不接受字符串或小数；密码复杂度仅三枚举。SMTP 联合完整性、Webhook 成对校验和引用操作依 Spec；规范化使用现有 CanonicalJson，摘要覆盖解析后配置，绝不把含引用的 JSON 用作错误消息。
- [x] **Step 4: 验证 GREEN。** 重跑上述命令，新增参数测试全通过且既有领域测试通过；检查 fixture 输入是生成测试值，不是真实凭据。
- [x] **Step 5: 记录检查点。** `git diff --check`，记录字段覆盖表与验证输出，不提交。

## Task 2：平台存储、迁移和兼容默认

**Files:** 新建上表 entity/config/reader；新建 `src/WebApi.Infrastructure/Persistence/Migrations/20261005090000_SystemSettings.cs`、同名 `.Designer.cs`；修改 `WebApiDbContextModelSnapshot.cs`、`src/WebApi.ControlPlane/ControlPlaneApp.cs` DI；新建 `tests/WebApi.Integration.Tests/SystemSettingsPersistenceTests.cs`、`tests/WebApi.TestSupport/Support/SystemSettingsFixture.cs`。

**Interfaces:** `SystemSetting` 为 `string Key/ScopeType/Value`、`Guid? ScopeId`、`Guid UpdatedBy`、`DateTimeOffset UpdatedAt`、`long Revision`。`SystemSettingsReader.ReadAsync(string group,CancellationToken ct)` → `Task<SettingsStoredValue(SettingsValues Value,long Revision,bool IsSaved)>`；另外产出 `SecurityAsync(ct)` → `Task<SecuritySettings>`、`GatewayAsync(ct)` → `Task<GatewaySettings>`、`AuditAsync(ct)` → `Task<AuditSettings>`。读取未保存组只返回默认，建议字段的 source 单独标识。

- [x] **Step 1: 写失败测试。** `MigrationCreatesEmptyConstrainedTable` 断言无种子、Key PK 128、ScopeType 24、Value jsonb、UpdatedBy FK users、revision≥1、允许键及 system/null CHECK；`DefaultsDoNotCreateRows` 断言读取兼容 480/16/LengthOnly/30000、auditExportEnabled=true，表仍 0 行；`SavedValuesSurviveNewServiceScope` 新 DbContext 能读相同值；`DatabaseFailureDoesNotBecomeDefaults` 断言故障非成功返回。

  ```csharp
  Assert.Equal(480, (await reader.SecurityAsync(ct)).SessionTtlMinutes);
  Assert.Equal(0, await db.Set<SystemSetting>().CountAsync(ct));
  ```
- [x] **Step 2: 验证 RED。** `bash scripts/check-settings.sh integration` 应因新表/读取器缺失失败，数据库必须是测试随机库。新增 fixture 在既有 ApiFixture 上显式授予测试系统角色 system.manage，不能改所有历史 fixture 的权限。
- [x] **Step 3: 实现迁移与 reader。** 沿用实体配置发现方式、revision 并发 token 和 JSONB 映射；仅应用空表迁移。空组默认值依 Spec；通知全空，意向建议 50/20/365 不冒充消费者实际值。每次读取 AsNoTracking，不建立进程长期缓存。迁移标识固定上述名字，生成 Designer 与 snapshot 后核对 SQL。
- [x] **Step 4: 验证 GREEN。** 重跑 integration，核对实际 PostgreSQL 类型/FK/CHECK及新 context 持久性；保留迁移 SQL 和测试输出。
- [x] **Step 5: 记录检查点。** 检查 migration Down 只影响新表；确认没有 4192 连接信息和运行目录变更，不提交。

## Task 3：设置治理 API、确认与幂等事务

**Files:** 新建上表 service/protector/endpoints；修改 `ControlPlaneApp.cs` 注册及映射、`src/WebApi.Infrastructure/Persistence/AuditedCommandExecutor.cs` 特定设置安全 capture；新建 `tests/WebApi.Integration.Tests/SystemSettingsCommandTests.cs`。

**Interfaces:** `SystemSettingsService.ListAsync(ActorContext actor,CancellationToken ct)` → `Task<IReadOnlyList<SettingsGroupDto>>`；`GetAsync(string group,ActorContext actor,CancellationToken ct)` → `Task<SettingsGroupDto>`；`ValidateAsync(string group,SettingsMutation request,ActorContext actor,CancellationToken ct)` → `Task<SettingsValidationDto(string TestKind,string Message)>`；`PreviewAsync(string group,SettingsMutation request,string? tag,ActorContext actor,CancellationToken ct)` → `Task<SettingsPreviewDto(IReadOnlyList<SettingsDifference> Differences,IReadOnlyList<string> Impacts,string ConfirmationToken,DateTimeOffset ExpiresAt)>`；`SaveAsync(string group,SaveSettingsRequest request,string? tag,ActorContext actor,CancellationToken ct)` → `Task<CommandResult<SettingsGroupDto>>`。`SettingsDifference(string Field,string Before,string After)` 只包含安全展示值。

凭证接口 `SettingsPreviewProtector.Issue(Guid actorId,string group,long revision,string valueHash)` → string；`Require(string token,Guid actorId,string group,long revision,string valueHash)` → void，使用注入 TimeProvider、现有 DataProtection，5 分钟有效。凭证只放绑定值和到期时间，不放引用/URL。

- [x] **Step 1: 写失败测试。** `AllEndpointsRequireRealPlatformPermission` 测匿名、组织/项目管理员、自定义同名 PlatformAdmin、撤权和直接 URL；`PreviewAndValidateDoNotWriteOrConnect` 断言表/审计不变、StructuralOnly 固定文案；`FirstSaveRaceHasOneWinner` 两个 ETag 0 并发结果为一个成功、一个412；`GroupsAreAtomicAndIndependent` 测整组回滚、不同组成功；`ConfirmationIsBoundAndExpires` 测 actor/group/value/revision 篡改、5分钟后新请求失败。
- [x] **Step 2: 写并运行幂等及泄漏 RED。** `CommittedReplayAfterTokenExpiryReturnsOriginalWithoutAudit` 比较响应、revision、审计数均相同；`SemanticChangeWithSameKeyIs409`、`RevokedActorCannotReplay`、`MissingTagIs428AndCsrfRequired`；`ReferencesNeverLeaveReadPreviewProblemsOrAudit` 搜索测试引用原文应始终不存在。运行 `bash scripts/check-settings.sh integration`，上述新 API 测试应失败。

  ```csharp
  Assert.Equal(originalBody, replayBody);
  Assert.Equal(auditCountAfterFirstSave, auditCountAfterReplay);
  ```
- [x] **Step 3: 实现服务与事务顺序。** HTTP 使用 Spec §6 的路径、no-store 和 8192 字节限制（读取原始 JSON 后严格 Parse/ParseSave，不能先普通反序列化丢重复属性）。保存进入 AuditedCommandExecutor 授权锁，重验 system.manage/平台身份，再调用 IdempotentCommandExecutor；语义摘要包含输入操作及旧 tag，不包含确认凭证。只在未重放 callback 中读当前行、检查 tag、Resolve、确认、写行/修订。成功重放不重复审计。不同 JSON 顺序语义相同；Keep/Replace/Clear 与输入引用参与摘要。
- [x] **Step 4: 完成安全 capture 并验证 GREEN。** SystemSetting 专用 capture 记录 Key、revision、规范化摘要、变更字段和引用存在性/provider；通知 URL 只记变更标志，不写完整值。保持通用白名单，不加入 Value；幂等记录不抢占设置审计资源标识。重跑 integration，包含撤权及并发测试，核对审计恰好一次和无 secret 原文。
- [x] **Step 5: 记录检查点。** 确认 preview token 不作为授权凭据、持久 KeyRing 用现有部署配置；保存接口不发网关更新/快照，不提交。

## Task 4：真实消费者与新旧行为兼容

**Files:** 修改 `src/WebApi.ControlPlane/Security/SessionEndpoints.cs`、`src/WebApi.Infrastructure/Governance/GovernanceService.cs`、`src/WebApi.Contracts/Catalog/CatalogContracts.cs`、`src/WebApi.Infrastructure/Routing/RouteService.cs`、`src/WebApi.Infrastructure/Catalog/OpenApiImportService.cs`、`SystemSettingsEndpoints.cs`；新建 `tests/WebApi.Integration.Tests/SystemSettingsConsumerTests.cs`。

**Interfaces:** 消费任务 2 reader；SaveRouteRequest.TimeoutMs 改为 `int? TimeoutMs=null`，实际 Route DTO 与实体保持非空。新增 `RouteDefaultsAsync(Guid environmentId,ActorContext actor,CancellationToken ct)` → `Task<RouteDefaultsDto(int TimeoutMs,string Source)>`，仅 scoped route.write 返回。

- [x] **Step 1: 写失败测试。** `NewLoginUsesTtlButExistingTicketKeepsExpiry` 解密实际 Cookie 检查 IssuedUtc/ExpiresUtc，保存5分钟后新票据差值5分钟、旧票据仍480，不使用长等待；`NewUserUsesPolicyAndOldLoginStillWorks` 检查三复杂度及最短32，新弱密码拒绝，旧16字符密码仍登录；`LoginAndDisableShareConsistentLockOrder` 并发完成、无死锁、最终停用者不能继续认证。

  ```csharp
  Assert.Equal(TimeSpan.FromMinutes(5), newTicket.Properties.ExpiresUtc - newTicket.Properties.IssuedUtc);
  Assert.Equal(TimeSpan.FromMinutes(480), oldTicket.Properties.ExpiresUtc - oldTicket.Properties.IssuedUtc);
  ```
- [x] **Step 2: 写路由 RED 并运行。** `NewRouteOmittedUsesDefaultAndExplicitWins` 设默认12000、省略→12000、显式9000→9000；`EditOmittedTimeoutIsRejected` →422且旧值不变；`ImportUsesDefaultOnlyForNewRoutes` 新增→12000、覆盖沿用已审阅旧语义30000而非12000；`RouteDefaultsDoesNotExposePlatformSettings` 检查跨范围拒绝与响应字段仅 TimeoutMs/Source。运行 `bash scripts/check-settings.sh integration`。
- [x] **Step 3: 实现登录与密码消费。** 登录事务先 advisory lock 再 User FOR UPDATE，再读 TTL，明确 AuthenticationProperties.IssuedUtc/ExpiresUtc、IsPersistent=false；保持 stamp 撤权和无滑动延期。创建账号在现有授权事务、HashPassword 之前读取策略；只影响本地账号创建，旧认证/bootstrap 不变。复杂度按 Unicode letter/digit、upper/lower 和非字母数字非空白 special 判断，密码最长仍1024。
- [x] **Step 4: 实现路由消费并验证 GREEN。** 新 route 省略/null 读默认，edit 省略/null 422，显式仍1–300000；OpenAPI 只在新分支读取默认。defaults 通过 ScopeResolver 与 route.write 校验。重跑 integration 与 gateway；核对保存设置后已有 snapshot bytes、版本/部署序列不变。
- [x] **Step 5: 记录检查点。** 保留真实 cookie/DB 断言摘要，不记录 cookie/密码；不修改 Gateway ConfigWatcher 两秒间隔或 GatewayApp 请求上限，不提交。

## Task 5：审计 CSV 的权限、开关和有界导出

**Files:** 新建上表 AuditAccessQuery/AuditExportService/AuditCsvFormatter；修改 `GovernanceService.cs`、`src/WebApi.Contracts/Governance/GovernanceContracts.cs`、`src/WebApi.ControlPlane/Governance/GovernanceEndpoints.cs`、DI；新建 `tests/WebApi.Integration.Tests/SystemSettingsAuditExportTests.cs`、`tests/WebApi.Domain.Tests/AuditCsvFormatterTests.cs`。

**Interfaces:** `AuditAccessQuery.QueryAsync(ActorContext actor,string? traceId,string? resourceId,CancellationToken ct)` → `Task<IQueryable<AuditLog>>`，完整复用现有功能权限与精确 Scope union；`AuditExportService.ExportAsync(ActorContext actor,string? traceId,string? resourceId,CancellationToken ct)` → `Task<AuditExportResult(byte[] Bytes,int RowCount,bool Truncated)>`；`AuditCsvFormatter.Row(AuditDto value)` → string。定点新增 `AuditPageDto(Items,Total,Page,PageSize,bool ExportAllowed)`，Items 为 IReadOnlyList<AuditDto>，其余整数与既有 PageResult 相同；不修改全局 PageResult。

- [x] **Step 1: 写失败测试。** `ExportMatchesListScopeUnion` 验证全平台/组织/项目/环境及 project-only 看不到组织级行；`ExportRequiresPermissionAndEnabledFlag` 缺 audit.read 或 flag=false→403，列表仍正常；`ExportRevocationOrDisableBeforeAdmissionDropsBuffer` 用内部可控批次测试 barrier，在生成期间改角色/范围/flag，断言无CSV响应。
- [x] **Step 2: 写有界 RED 并运行。** `ExportHasStableUpperIdAndLimits` 并发新增行不进入已固定 long 类型 upperId，10001行→10000且Truncated，超8MiB不切半行；`CsvFormulaAndQuotingAreSafe` 测 ASCII =/+/-/@、前置空格、tab/CR/LF、逗号与引号，复用 CsvLogExporter.Cell；`ExportContainsNoIpOrArbitraryJson`。运行 domain 与 integration 两模式。

  ```csharp
  Assert.Equal(10000, export.RowCount);
  Assert.True(export.Truncated);
  Assert.True(export.Bytes.Length <= 8 * 1024 * 1024);
  ```
- [x] **Step 3: 实现共享查询和安全缓冲。** 保持现有 JSON 审计列和授权含义，仅抽取查询；过滤值最长128。CSV固定列 id、createdAt、actorId、action、resourceType、resourceId、organizationId、projectId、environmentId、traceId，省略IP/BeforeJson/AfterJson。UTF-8字节包括表头/BOM（若采用BOM则计入），最多每批500行，固定 upperId、按Id降序游标。
- [x] **Step 4: 实现准入并验证 GREEN。** 每批新授权查询并检查开关；结束后在 advisory lock 事务下最后重验角色、scope与flag，授权范围与生成时不一致则拒绝并清空 buffer。成功返回 Content-Disposition、安全文件名、X-Export-Row-Count/X-Export-Truncated、no-store。Task3 API保存flag立即影响新导出，list安全返回 ExportAllowed。运行 domain/integration，原审计列表回归通过。
- [x] **Step 5: 记录检查点。** 明确最终准入后已发送数据无法追回；测试 barrier 仅测试注入，不提供公共绕过参数，不提交。

## Task 6：第 40 页与关键页面交互

**Files:** 新建上表 SystemSettings 页面/state/contracts，新增 `console/tests/system-settings.test.mjs`；修改 `console/src/main.tsx`、`Shell.tsx`、`pages/Routes.tsx`、`pages/Governance.tsx`、`styles/tokens.css`，仅定点追加所需样式，不引入新 UI 依赖。

**Interfaces:** `SystemSettings` React组件；contracts.ts 定义 `SettingsInputValues=Record<string,number|string|boolean|string[]|SecretReferenceMutation|null>`、`SettingsEditorState={group:string;loaded:SettingsGroupDto|null;values:SettingsInputValues|null;etag:string|null;dirty:boolean;requestEpoch:number;conflict:boolean;idempotencyKey:string|null}`。状态模块导出 `createSettingsState():SettingsEditorState`（默认security）、`applySettingsResponse(state:SettingsEditorState,response:SettingsGroupDto,requestEpoch:number):SettingsEditorState`、`invalidateSettingsState(state:SettingsEditorState):SettingsEditorState`、`settingsSemanticKey(group:string,values:SettingsInputValues,etag:string):string`。invalidate 清空loaded/values（含引用替换输入），递增epoch；semanticKey为规范化语义字符串，排除confirmationToken。state.d.mts使用相同类型，复用已有导航守卫及CSV保存函数。

- [x] **Step 1: 写失败测试。** `preservesDraftOn412`、`sameSemanticRetryKeepsIdempotencyKey`、`secretKeepDoesNotSendPlaceholder`、`revokeClearsDraftAndLateResponseCannotRestoreIt`、`dirtyNavigationRequiresExplicitDiscard`、`notificationStructuralTestNeverClaimsDelivery`。断言所有 group17字段与 effect/source 映射齐全、PROD1级不可选。运行 `node --test console/tests/system-settings.test.mjs`，缺模块/行为应失败。

  ```javascript
  const cleared = invalidateSettingsState(state);
  assert.equal(cleared.values, null);
  assert.deepEqual(applySettingsResponse(cleared, oldResponse, state.requestEpoch), cleared);
  ```
- [x] **Step 2: 实现状态转换。** requestEpoch 与会话身份绑定，失效时递增并清空敏感替换输入；有效412只保留草稿，用户读取最新后重新预览，不后台覆盖。按语义值/ETag换幂等键，确认 token 更新不换键。会话/权限错误处理包含直接 API 失败，不能仅隐藏按钮。
- [x] **Step 3: 实现页面与导航。** `/settings/system` 五组十七字段、平台级标题、来源/消费者提示、保存预览确认和结构测试，通知显式 Keep/Replace/Clear。使用现有权限导航，顶部当前组织不改变平台目标；离开/后退/关闭走既有 dirty guard。route新建加载受范围保护 defaults 并显示来源，编辑读取原值；异步默认不得覆盖用户已修改输入。
- [x] **Step 4: 接审计导出并验证 GREEN。** 审计页读取 ExportAllowed；关闭时禁用并解释，正常下载服务端 CSV 和截断提示，错误不下载假文件。运行 `bash scripts/check-settings.sh console`（既有node测试、tsc、vite均通过）；新增 `lateRouteDefaultDoesNotOverwriteInput` 测试通过。
- [x] **Step 5: 记录检查点。** 暂不将 coverage.json 标记实际完成，留到真实验收；检查页面无原型“测试连接成功”、无 secret占位符保存，不提交。

## Task 7：独立双网关实测与 1440px 浏览器 QA

**Files:** 新建上表 scripts/settings 四模块；修改 `scripts/check-settings.sh` e2e/browser 模式；新增 `tests/WebApi.EndToEnd.Tests/SystemSettingsLoopTests.cs`、扩展 `tests/runtime/settings-runner.test.mjs`；输出独立 `docs/evidence/settings/`。

**Interfaces:** `runSettingsAcceptance({root,retainForReview})` → `{sourceManifestHash,images,resultPath,reviewContextPath}`；scenario 的 `runSettingsScenario(context)` → JSON 结果（checks 每项含 name/pass/evidence，必须含 `settings-persistence`、`snapshot-unchanged-before-publish`、`both-gateways-ack`、`old-route-still-works`、`audit-export-disabled`）；browser `runSettingsBrowserQa(context)` → screenshots/result。复用 `buildPolicySource({root,outputDirectory})` 和 `verifyPolicySource(source)`，不调用会覆盖 policies 证据的旧 acceptance。新 evidence 模块补充实际 scripts/settings/check-settings/package文件摘要，确保验收工具也有身份。

- [x] **Step 1: 写失败测试。** `SettingsRunnerOwnsOnlyItsResources` 断言随机project/owner labels、私密文件0600、目录0700、不使用4192与local卷、失败退出/清理可追溯；`SettingsChangeRequiresNormalPublishToReachBothGateways` xUnit bridge 要求 scenario 对两个实际节点确认版本/序列与请求结果，不能只检查容器healthy。运行 `node --test tests/runtime/settings-runner.test.mjs` 与 e2e 模式，缺runner/结果应失败。

  ```javascript
  assert.equal(result.checks.find(c => c.name === 'both-gateways-ack')?.pass, true);
  assert.equal(result.checks.find(c => c.name === 'snapshot-unchanged-before-publish')?.pass, true);
  ```
- [x] **Step 2: 实现冻结构建与夹具。** 用实际源manifest冻结构建，保持 locked restore、既有镜像/SDK缓存与各服务配置；随机 PostgreSQL、双网关、三源/告警组件，凭据仅私有 `.runtime/<随机id>`。先保存既有快照与双节点状态，再改变新路由默认；保存本身不能改原快照/双节点状态。新路由产生工作配置后走原审批发布，两节点 ACK 后验证行为；原业务路由继续正常。
- [x] **Step 3: 运行真实 E2E。** `bash scripts/check-settings.sh e2e --retain-for-review`，产出可核对sourceManifest、实际imageID/labels、测试条件和双节点结果；覆盖保存重启持久、确认冲突/撤权、消费者、CSV关闭/范围/上限。stdout/public evidence做 SecretRef/密码/节点秘密/cookie扫描。失败保留标记，不生成成功总结。
- [x] **Step 4: 运行并查看浏览器 QA。** `bash scripts/check-settings.sh browser`，真实1440px截图覆盖五组17字段、来源/未应用、确认保存、412草稿、引用安全、离开保护、撤权与迟到响应、route默认、CSV下载/禁用。实际view_image检查截图，修复缺陷后仅重跑受影响场景；无夹具不得用静态截图代替。
- [x] **Step 5: 记录检查点。** 精确清理仅己方owner的资源与私密文件，记录清理结果；已有 local/policies资源不变。测试后的保留review夹具单独归属，清理命令由新runner提供，不提交。

## Task 8：回归、独立审查与可复核交付

**Files:** 完成 `scripts/check-settings.sh` verify；新建 `scripts/package-settings.py`、`docs/deployment/system-settings-validation-result.md`、`docs/deployment/system-settings-delivery-index.md`；修改 `console/src/coverage.json` 第40项；输出新 `deliverables/WebAPI_Enterprise_系统设置源码及验收_20261005.zip` 及外置 manifest，不覆盖旧交付。

**Interfaces:** verify 顺序执行 domain/integration/gateway/console、runner安全测试、settings E2E/browser；package CLI `python3 scripts/package-settings.py --root <checkout> --evidence <settings-evidence>` 在证据与当前实际源码摘要一致时封装，否则非零退出。manifest 记录每文件SHA-256、包摘要、baseCommit及未提交源码分类。

- [x] **Step 1: 写失败测试。** 扩展 runner测试 `VerifyFailsOnStaleSourceOrFailedCheck`、`PackageExcludesSecretsAndOldRuntimeData`，源摘要不符、失败项、缺浏览器结果→拒绝；包排除 `.runtime/.secrets/.git/node_modules/bin/obj`、真实账号凭据和旧升级备份。运行 runner测试应失败。

  ```javascript
  assert.notEqual(staleSourceRun.status, 0);
  assert.notEqual(failedCheckRun.status, 0);
  ```
- [x] **Step 2: 完成验证/封装入口及覆盖说明。** 实现 verify 和 package CLI 的失败门禁。根据任务7实际结果更新第40项，部署意向/通知投递/清理仍未实现，第39项不变。文档列出源码/镜像身份、结果、限制、CSV准入边界、运行/停止/清理命令及4192未升级；先完成这些源文件变更再冻结最终验收源码。
- [x] **Step 3: 执行回归和最终审查。** 运行 `bash scripts/check-settings.sh verify`，保留已有关联 Session/Governance/OpenAPI/Release/Policy测试；证据缓存只允许当前实际源摘要完全一致。按已选择执行方式完成最终独立源码审查，发现项修复并重测相关项；若实际构建源码变化，重新冻结构建并验证，不沿用旧hash。保留审查结论。
- [x] **Step 4: 封装并回读验证。** 执行 package CLI，ZIP完整CRC检查、解包逐文件摘要比对、敏感值扫描，输出包SHA及外置manifest。运行 `git diff --check`，核对所有设计验收条目均有结果；测试结果文档的补充不更改已验收的产品源码。
- [x] **Step 5: 最终交付。** 链接新原型review地址（仅夹具存续时）、源码/验收包和中文索引，报告实现/隔离验证/未部署边界；保留原账号数据。没有额外授权不执行源码提交、长期环境升级或发布。

## 计划自检与执行交接

自检覆盖：Spec §1–3→任务1/6，§4→任务2，§5–6→任务3/5，§7→任务4/5，§8→任务6/7，§9→任务7/8。五项 Review Focus 均落到具名测试；审计ID核对为 long；17字段沿用同一验证矩阵；任务间reader、DTO和路由默认接口保持一致。

执行建议：**Native：本会话逐任务自执行，最终独立审查。** 八项任务共享契约、授权锁和现有业务消费者，统一实现更易保持接口一致；每项仍执行RED/GREEN与检查点。另可选择 Subagent-driven，逐项新实现者/审查者，独立审查更频繁但上下文成本更高。

本文件完成后请用户审阅计划及执行方式；获计划审阅通过再使用相应执行技能、建立隔离工作树并开始任务1。当前设计及实施计划已批准，采用 Native 执行。
