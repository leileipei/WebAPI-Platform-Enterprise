# SSO / OIDC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将第39页接入真实Provider治理，以独立Keycloak验证企业登录、显式用户绑定和会话撤销，保留本地admin与原业务权限。

**Architecture:** 官方OIDC处理器按Provider认证修订构建不可变选项；PG保存配置、绑定及一次性尝试。平台角色/Scope继续由PG决定，浏览器只持有平台Cookie，Gateway不接收OIDC令牌。治理、协议适配、网络/秘密、会话、UI和fixture分别承担单一职责。

**Tech Stack:** SDK10.0.401、ASP.NET Core、EF Core/Npgsql、PG、React19/TypeScript/Vite、Node、Docker Compose；OpenIdConnect10.0.12、IdentityModel协议包8.19.2；Keycloak26.8.0镜像在任务10解析并固定digest。

**Spec:** [已确认设计](../specs/2026-10-05-sso-oidc-design.md)。用户于2026-10-05确认设计；**本计划待审阅，执行方式待选择，以下任务尚未开始**。

## Global Constraints

- 源码基线 `f4e2cc3d81f0467fd48894127b26bc1ec6138389`；隔离实施，保护主仓库及系统设置worktree现有修改。
- 不自动提交、不push/merge；不写4192账号/Provider/业务数据，不重启4192/4196/4197。新提交和升级单独审阅。
- Provider治理要求平台资源 `system.sso.manage` 与真实系统PlatformAdmin；绑定另需 `user.manage`，角色/Scope用原权限。
- local用户不可绑定或转换；SSO用户 `AuthSource="sso"`、`PasswordHash=null`；保留一个Active且可密码认证的本地平台管理员。
- 唯一绑定 `(provider_id,issuer,subject)`，每用户一个绑定；不自动开户、不按邮箱合并、不以claim授予角色/Scope。
- openid必需，禁止offline_access；映射仅顶层字符串displayName/email，4KiB；Subject255，名称128/Issuer1024/ClientId256/SecretRef512字符。
- code＋PKCE S256、query GET、client_secret_post、PAR禁用、SaveTokens=false；不调用UserInfo、不保存Refresh Token。
- 尝试5分钟；只清理过期24小时尝试；启用需当前revision最近15分钟合格测试；时钟容差60秒。
- 平台会话/CSRF保持Strict；临时Cookie HttpOnly/Lax，生产Secure；仅Development显式fixture允许受批本机HTTP。
- SecretRef只支持 `file://sso/<alias>` 部署白名单；明文不进入PG、DTO、审计、日志、浏览器、源码或公开ZIP。
- Metadata/JWKS各256KiB/10秒、Token128KiB/15秒；批准来源及地址；禁止自动跨来源重定向；签名仅RS256/PS256/ES256与发现声明交集。
- 1440px、现有Shell/tokens；配置保存、连接测试、真实登录分别报告，不显示模拟成功。

## Review Focus

1. 登录中改名/切默认也改变完整revision：拒绝该次尝试，已有会话不因非认证修改退出（任务3/5/7）。
2. 两Provider并行及缓存淘汰：不能混用秘密、nonce、回调选项，旧修订不能绕过撤销（任务6/7）。
3. 同邮箱localadmin、Subject大小写、资料claim缺失：不合并、不赋权、不清空平台资料（任务4/5）。
4. ConsoleHost代理与Strict会话：真实跨站回调和同源完成页恢复正常（任务6/9/10/11）。
5. 领取尝试后中断：不重复兑换、不重复成功审计、错误不泄密，重启保留重放事实（任务5/6/10）。

---

## 文件边界与执行约定

路径相对新实施worktree `W`。执行时读取using-git-worktrees，先查现有附件，复用合适SSO目录或从基线创建 `feature/sso-oidc`，记录真实绝对路径；不提前创建。复制已确认设计/本计划至新worktree为未提交文档，保留原件。

| 文件组 | 职责 |
|---|---|
| `src/WebApi.Contracts/Sso/SsoContracts.cs` | 治理/公开入口/绑定DTO，任务1 |
| `src/WebApi.Infrastructure/Persistence/Entities/{SsoProvider,UserExternalIdentity,SsoProviderTest,SsoLoginAttempt}.cs`及同名Configuration | 数据与约束，任务1 |
| `src/WebApi.Infrastructure/Sso/{SsoProviderValidator,SsoOptions,SsoSecretResolver,OidcAddressPolicy,OidcMetadataClient}.cs` | 结构、部署、秘密、网络，任务1/2 |
| `src/WebApi.Infrastructure/Sso/{SsoProviderService,ExternalIdentityService,LocalAdministratorGuard,SsoLoginCoordinator,SsoSessionValidator,SsoAttemptCleanupService}.cs` | 治理/身份/尝试/会话，任务3—5/7 |
| `src/WebApi.ControlPlane/Sso/{SsoProviderEndpoints,ExternalIdentityEndpoints,SsoLoginEndpoints,OidcSchemeRegistry,OidcSchemeProvider,OidcEvents,SsoProtocolMiddleware}.cs` | API和处理器适配，任务3/4/6 |
| `src/WebApi.ControlPlane/Security/PlatformSessionIssuer.cs` | 共用Cookie签发，任务6 |
| `console/src/sso/{contracts.ts,state.mjs,state.d.mts,login.mjs,login.d.mts}`、`pages/SsoProviders.tsx`、`pages/SsoUserEditor.tsx` | 状态/页面/登录，任务8/9 |
| `scripts/check-sso.sh`、`scripts/sso/{fixture,acceptance,scenario,evidence,verify,cleanup}.mjs` | 隔离验收，任务1/10—12 |
| `deploy/compose.sso-test.yml`、`deploy/sso-images.lock.json`、`tests/WebApi.TestSupport/Support/{SsoFixture,OidcTestServer}.cs` | PG/IdP、镜像锁、协议负例，任务1/6/10 |

既有文件衔接限于ControlPlaneApp、GovernanceService、AuditedCommandExecutor、SessionEndpoints、ConsoleHost及console main/Shell/Login/Governance/api/coverage/必要样式、迁移snapshot和依赖锁，不重构业务发布/Gateway。

以下任务的 `ct` 均为 `CancellationToken`，`actor` 为 `ActorContext`，`command` 为 `CommandRequestContext`。Interfaces定义为跨任务契约，后续不能另起同义类型。

命令均在W执行：新 `bash scripts/check-sso.sh domain|integration|gateway|console|e2e|verify` 于任务1/10/12逐段建立。前三种用现有固定SDK镜像和独立owner项目；console沿用check-console.sh。Node使用明确 `WEBAPI_NODE=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node`。RED必须来自新增断言或本次缺失接口，网络/环境错误不算RED。每任务GREEN后保存源码清单和证据，不stage/commit。

## Task 1：契约、持久模型与严格校验

**Files:** 新建上表Contracts、四实体/Configuration、Validator/Options、check-sso.sh/compose.sso-test.yml；修改Persistence/CoreConstraints.cs、迁移snapshot；创建 `Migrations/20261005120000_SsoOidc.cs`及Designer；测试 `tests/WebApi.Domain.Tests/SsoProviderValidationTests.cs`、`tests/WebApi.Integration.Tests/SsoPersistenceTests.cs`、`tests/runtime/sso-runner.test.mjs`。

**Interfaces:** `SsoClaimMapping(string? DisplayName,string? Email)`；`SaveSsoProviderRequest(Guid? OrganizationId,string Name,string Issuer,string ClientId,string SecretRef,IReadOnlyList<string> Scopes,SsoClaimMapping ClaimMapping)`；`SsoProviderOperation {Enable,Disable,Default}`。`SsoProviderDto`含Id及请求字段、ProviderType、Enabled、IsDefault、Revision/AuthRevision、可空LastTest；`SsoTestDto(Guid Id,long ProviderRevision,DateTimeOffset TestedAt,string Status,IReadOnlyList<SsoTestStageDto> Stages)`，Stage为Code/Passed/脱敏Detail。公开 `PublicSsoProviderDto(Guid Id,string Name,bool IsDefault)`。`CreateSsoUserRequest(string Username,string DisplayName,string? Email,Guid ProviderId,string Subject)`、`UpdateExternalIdentityRequest(Guid ProviderId,string Subject,bool Enabled)`；`ExternalIdentityDto`含Id/UserId/ProviderId/Issuer/Subject/Enabled/Revision。Validator：`SaveSsoProviderRequest Parse(ReadOnlyMemory<byte> utf8)`、`bool IsSafeReturnPath(string? path)`。

- [ ] **Step 1: 写RED。** `RejectsInvalidConfiguration`检查openid缺失、offline_access、非法引用/Issuer/claim映射及字段超长；JSON重复/未知字段拒绝。`MigrationPreservesLocalUsers`核对迁移前后hash/auth_source/业务ID不变、Provider0行。`DefaultConstraintsAreScoped`验证平台/组织默认唯一、Disabled不可Default。Node守卫拒绝生产项目、其他owner、symlink。

  ```csharp
  Assert.Equal(422, Assert.Throws<ApiException>(() => SsoProviderValidator.Parse(noOpenId)).Status);
  Assert.Equal(before.PasswordHash, after.PasswordHash);
  Assert.Equal(0, await db.Set<SsoProvider>().CountAsync());
  ```
- [ ] **Step 2: 建立最小隔离测试入口并跑RED。** `webapi-sso-test-<uuid>`私有目录、PG随机secret、独立Compose/owner；不得引用4192卷。运行 `bash scripts/check-sso.sh domain --filter FullyQualifiedName~SsoProviderValidationTests` 和 `integration --filter FullyQualifiedName~SsoPersistenceTests`。
- [ ] **Step 3: 实现。** revision/auth_revision分别管理；FK、平台/组织默认部分唯一索引、绑定三元组/UserID唯一；尝试状态/TTL、测试独立表。严格Parse不丢重复字段；迁移不回填或修改原认证数据。
- [ ] **Step 4: 跑GREEN并核对迁移。** 重跑过滤及 `"$WEBAPI_NODE" --test tests/runtime/sso-runner.test.mjs`，核对SQL只新增SSO结构与必要约束，DTO无ClientSecret。
- [ ] **Step 5: 记录。** `docs/evidence/sso/task-01.json`包含RED/GREEN与实际源码hash，不提交。

## Task 2：SecretRef与受限Metadata/JWKS

**Files:** 新建SecretResolver/AddressPolicy/MetadataClient；修改Directory.Packages.props、Infrastructure.csproj及受影响packages.lock.json；Domain.Tests新建 `SsoSecretResolverTests.cs`、`SsoMetadataTests.cs`。

**Interfaces:** `ISsoSecretResolver.ResolveAsync(string reference,ct) → Task<string>`；`IOidcAddressPolicy.ResolveAllowedAsync(Uri uri,ct) → Task<IReadOnlyList<IPAddress>>`；`IOidcMetadataClient.GetAsync(string issuer,bool forceRefresh,ct) → Task<OidcMetadataSnapshot>`。Snapshot只在服务端含OpenIdConnectConfiguration、FetchedAt、验证后的端点/算法。Options定义PublicBaseUrl、AllowedOrigins、AllowedPrivateCidrs、SecretFiles、AllowedSigningAlgorithms及显式Development fixture连接覆盖，默认无HTTP/覆盖。

- [ ] **Step 1: 写RED。** `SecretReferenceNeverSelectsArbitraryPath`拒绝未知alias、路径穿越、symlink、缺失/空secret；`MetadataRejectsUnapprovedRedirectAndReboundAddress`检查DNS重绑定/混合地址/非法端点/不同Issuer；`BoundedResponsesAndUnknownKidFailClosed`检查256KiB/10秒、kid单次刷新、none/HS及Token jku/x5u不发请求。

  ```csharp
  Assert.Empty(fakeNetwork.RequestsToUnapprovedOrigins);
  Assert.Equal(1, fakeNetwork.UnknownKidRefreshCount);
  Assert.DoesNotContain(secret, capturedErrorAndLogs);
  ```
- [ ] **Step 2: 跑RED。** `bash scripts/check-sso.sh domain --filter 'FullyQualifiedName~SsoSecretResolverTests|FullyQualifiedName~SsoMetadataTests'`。
- [ ] **Step 3: 实现。** 固定IdentityModel协议8.19.2；受控连接地址、禁止自动redirect、边读边限额；metadata/JWKS按Issuer缓存并受限刷新，秘密只解析映射。fixture仅覆盖已登记localhost来源/端口的连接地址，URL/Issuer不变，不能扩大一般SSO白名单。
- [ ] **Step 4: 跑GREEN及锁定还原。** 重跑过滤，缺secret映射仅使SSO不可启用，本地服务仍可启动。
- [ ] **Step 5: 记录。** 保留请求次数、拒绝码和锁差异，不保存模拟secret值。

## Task 3：Provider治理、连接测试与公开入口

**Files:** 新建ProviderService/Endpoints及TestSupport SsoFixture；修改ControlPlaneApp、AuditedCommandExecutor；Integration.Tests新建 `SsoProviderCommandTests.cs`、`SsoProviderTestTests.cs`、`SsoPublicDiscoveryTests.cs`。

**Interfaces:** Service `ListAsync(actor,Guid? organizationId,string? search,int page,int pageSize,ct) → Task<PageResult<SsoProviderDto>>`、`GetAsync(Guid id,actor,ct) → Task<SsoProviderDto>`；`CreateAsync(SaveSsoProviderRequest request,command,actor,ct)`、`SaveAsync(Guid id,SaveSsoProviderRequest request,string? tag,command,actor,ct)`、`OperateAsync(Guid id,SsoProviderOperation operation,string? tag,command,actor,ct)`均返回`Task<CommandResult<SsoProviderDto>>`；`TestAsync(Guid id,string? tag,command,actor,ct) → Task<CommandResult<SsoTestDto>>`、`PublicAsync(Guid? organizationId,ct) → Task<IReadOnlyList<PublicSsoProviderDto>>`。

- [ ] **Step 1: 写RED。** `GovernanceRequiresRealPlatformAuthority`：匿名401/组织管理员及伪PlatformAdmin403；`DefaultRaceAndReplayAreAtomic`：一默认赢家、重放不增审计、异体409、旧/缺tag412；`TestCannotAuthorizeDifferentRevision`延迟metadata制造竞态；`NameAndDefaultOnlyChangeRevision`检查auth_revision不变；`PublicDtoHasOnlyThreeFields`核对最小DTO和组织入口。

  ```csharp
  Assert.Equal(before.AuthRevision, renamed.AuthRevision);
  Assert.Equal(before.Revision + 1, renamed.Revision);
  Assert.Equal(HttpStatusCode.PreconditionFailed, staleSave.StatusCode);
  Assert.Equal(1, successfulDefaultCount);
  ```
- [ ] **Step 2: 跑RED。** `bash scripts/check-sso.sh integration --filter 'FullyQualifiedName~SsoProvider|FullyQualifiedName~SsoPublicDiscovery'`。
- [ ] **Step 3: 实现。** Spec§9路径、no-store、If-Match、幂等；先授权/治理锁，再重放，未重放才检查revision；默认切换双行同事务。测试网络在锁外、结果提交重核revision，启用需15分钟内合格测试及localadmin；已有绑定时Issuer/ClientId/组织不可改。公开默认只调按钮优先级，无自动跳转/组织枚举。
- [ ] **Step 4: 脱敏审计并GREEN。** Provider捕获ID、修订、状态、配置hash、变更字段/引用存在标志，不捕获Subject/完整引用/Token；幂等记录不抢资源标识。重跑过滤及IdempotencyTests，未授权不能连接IDP。
- [ ] **Step 5: 记录。** Discovery合格不代表ClientSecret或用户登录有效，结果明确显示“客户端认证与用户登录尚未验证”。

## Task 4：SSO用户、绑定与最后本地管理员

**Files:** 新建ExternalIdentityService/LocalAdministratorGuard/Endpoints；修改GovernanceService、ControlPlaneApp；Integration.Tests新建 `SsoExternalIdentityTests.cs`、`LocalAdministratorGuardTests.cs`。

**Interfaces:** `CreateAsync(CreateSsoUserRequest request,command,actor,ct) → Task<CommandResult<UserDto>>`；`GetAsync(Guid userId,actor,ct) → Task<ExternalIdentityDto>`；`SaveAsync(Guid userId,UpdateExternalIdentityRequest request,string? tag,command,actor,ct) → Task<CommandResult<ExternalIdentityDto>>`。Guard `RequireAvailableAsync(ct) → Task`、`ProtectRemovalAsync(Guid userId,ct) → Task`，调用者需持治理锁。

- [ ] **Step 1: 写RED。** `SameEmailNeverBindsLocalUser`保全local hash；`SubjectIsCaseSensitiveAndUnique`区分大小写、拒绝重复；`BindingNeedsBothPermissionsAndDisabledUser`缺一权限403/Active修正409；`LastLocalAdminIsProtectedEvenWithSsoAdmin`覆盖停用、撤角色、Provider启用及并发。

  ```csharp
  Assert.Equal("sso", created.AuthSource);
  Assert.Null(createdRecord.PasswordHash);
  Assert.Empty(created.RoleIds);
  Assert.Equal(originalLocalHash, local.PasswordHash);
  ```
- [ ] **Step 2: 跑RED。** `bash scripts/check-sso.sh integration --filter 'FullyQualifiedName~SsoExternalIdentityTests|FullyQualifiedName~LocalAdministratorGuardTests'`。
- [ ] **Step 3: 实现。** 用户/绑定同事务；local目标全拒绝；修改先Disabled、校验tag、旋转SecurityStamp。请求不可夹带权限/密码；管理员明确分配角色/Scope。新增local保护接所有用户停用/撤平台角色/Provider启用，保留原最后PlatformAdmin检查。
- [ ] **Step 4: GREEN及治理回归。** 重跑过滤、GovernanceTests/SessionTests；SSO密码登录失败，local原密码成功。
- [ ] **Step 5: 记录。** 双权限、唯一性、管理员竞争及原账号保全单独记录。

## Task 5：一次性尝试与验证后平台身份

**Files:** 新建Coordinator及 `Infrastructure/Sso/SsoLoginContracts.cs`；Integration.Tests新建 `SsoLoginAttemptTests.cs`、`SsoIdentityAdmissionTests.cs`。

**Interfaces:** 私有`SsoProviderSnapshot`含配置/两修订；`PendingSsoLogin(Guid AttemptId,SsoProviderSnapshot Provider,string ReturnPath,DateTimeOffset ExpiresAt)`；`ValidatedOidcIdentity(string Issuer,string Subject,IReadOnlyDictionary<string,string> Claims)`只能由验证后适配器构造；`SsoSessionGrant(Guid UserId,string SecurityStamp,Guid ProviderId,long AuthRevision,Guid BindingId,int TtlMinutes,string ReturnPath)`。Coordinator：`BeginAsync(Guid providerId,string? returnPath,ct) → Task<PendingSsoLogin>`、`ClaimAsync(Guid attemptId,Guid providerId,long expectedRevision,ct) → Task`、`CompleteAsync(Guid attemptId,ValidatedOidcIdentity identity,string traceId,IPAddress? ip,ct) → Task<SsoSessionGrant>`、`FailAsync(Guid attemptId,string safeCode,ct) → Task`。

- [ ] **Step 1: 写RED。** `AttemptClaimHasOneWinner`；`ChangedRevisionAndExpiredAttemptCannotRedeem`覆盖改名/默认/5分钟；`UnknownIdentityAndOrganizationMismatchDenyAdmission`；`ClaimsOnlySynchronizePresentProfileStrings`缺claim不清资料、roles/groups不授权、超长拒绝；`InterruptedProcessingCannotBeClaimedAfterRestart`新实例仍拒重放。

  ```csharp
  Assert.Equal(1, claimedCount);
  Assert.Equal(beforeRoles, afterRoles);
  Assert.Equal(beforeScopes, afterScopes);
  Assert.Equal(originalDisplayName, identityWithoutName.DisplayName);
  ```
- [ ] **Step 2: 跑RED。** `bash scripts/check-sso.sh integration --filter 'FullyQualifiedName~SsoLoginAttemptTests|FullyQualifiedName~SsoIdentityAdmissionTests'`。
- [ ] **Step 3: 实现。** Begin要求Enabled及安全路径；Claim PG条件更新Pending/期限/revision；Complete持8901202锁重验源/绑定/用户/有效组织Scope、同步资料、读取真实TTL、成功审计及状态同事务。平台源无Scope可认证但不赋权，组织源需满足准入。失败不返回grant，Fail只写脱敏码。
- [ ] **Step 4: GREEN。** 用TimeProvider推进时间；重复Complete无重复成功审计；失败异常不含身份原文。
- [ ] **Step 5: 记录。** 本任务不代替签名验证；内部ValidatedOidcIdentity不能成为HTTP请求DTO。

## Task 6：官方动态处理器、协议端点与ConsoleHost代理

**Files:** 新建ControlPlane Sso适配文件、PlatformSessionIssuer、TestSupport OidcTestServer；修改ControlPlane.csproj、包版本/锁、ControlPlaneApp、SessionEndpoints、ConsoleHost/Program.cs；Integration.Tests新建 `OidcProtocolTests.cs`、`OidcProviderIsolationTests.cs`。

**Interfaces:** `OidcSchemeRegistry.PrepareAsync(Guid providerId,ct) → Task<string>`返回`oidc:{providerId:N}:{authRevision}`并发布不可变named options；`OidcSchemeProvider : AuthenticationSchemeProvider`仅为当前callback选择远程scheme；`SsoProtocolMiddleware.InvokeAsync(HttpContext context)`先异步准备，普通请求跳过；`PlatformSessionIssuer.IssueAsync(HttpContext context,Guid userId,string securityStamp,int ttlMinutes,SsoSessionGrant? sso,ct) → Task`共用Cookie。OidcEvents接任务5接口，TicketReceived替换为平台principal/properties，不签外部principal。

- [ ] **Step 1: 写RED。** `CallbackRejectsInvalidProtocolBeforeCookie`覆盖state/correlation/nonce、签名/算法/Issuer/aud/azp、过期、错Provider路径/ReturnUrl；`InvalidNonceNeverCommitsSuccess`；`ParallelProvidersNeverShareOptions`包含淘汰后重建；`StartRequiresOriginAndCsrf`实际form；`ConsoleHostForwardsOnlyOidcCallbacks`核对代理与SPA路径。

  ```csharp
  Assert.False(HasPlatformSession(response));
  Assert.Equal(0, fakeIdp.RedemptionCountForRejectedState);
  Assert.Equal(0, await db.Set<AuditLog>().CountAsync(x => x.Action == "auth.sso.login"));
  ```
- [ ] **Step 2: 固定包并跑RED。** 安装OpenIdConnect10.0.12并更新锁；`bash scripts/check-sso.sh integration --filter 'FullyQualifiedName~OidcProtocolTests|FullyQualifiedName~OidcProviderIsolationTests'`。合成IDP数据只用于负例。
- [ ] **Step 3: 实现适配。** 默认Cookie/401 JSON不变；code/query/PKCE、MapInboundClaims=false、PAR Disable、SaveTokens=false、UserInfo=false；ConfigurationManager/Backchannel只能走任务2受限网络。callback前准备named options，AuthorizationCodeReceived验证state/correlation后Claim，TicketReceived在全部协议验证后Complete；仅GET、禁远程注销默认入口。
- [ ] **Step 4: 实现端点及代理。** 同源POST form隐藏CSRF，拒绝已有会话静默切换；state关联attempt/修订/安全路径。签平台Cookie，local端点仅提取签发辅助，保留锁内密码复核。ConsoleHost新增 `/auth/oidc/callback/{**rest}`精确代理，complete交SPA；PublicBaseUrl不取任意Host，callback后303清query，no-store/no-referrer，敏感query/body不进入访问日志/遥测。
- [ ] **Step 5: GREEN。** 重跑过滤及PersistentSessionTests；检查实际Set-Cookie的临时Lax/HttpOnly/路径、生产Secure及平台Strict。保存事件顺序证据，无code/state/token/secret原文。

## Task 7：会话撤销、专用清理与回归

**Files:** 新建SessionValidator/AttemptCleanupService；修改ControlPlaneApp；Integration.Tests新建 `SsoSessionRevocationTests.cs`、`SsoAttemptCleanupTests.cs`。

**Interfaces:** `ValidateAsync(Guid userId,string stamp,Guid providerId,long authRevision,Guid bindingId,ct) → Task<bool>`；`CleanupAsync(DateTimeOffset now,ct) → Task<int>`，HostedService每5分钟、每批最多1000行，仅过期24h尝试。既有Cookie validator的SSO分支调用，local保留原检查。

- [ ] **Step 1: 写RED。** `DisableAndReenableNeverReviveCookie`；`AuthRevisionRevokesButNameAndDefaultDoNot`；`SecondControlPlaneSeesRevocationFromDatabase`两个应用实例共享PG而registry独立；`CleanupCannotDeleteCurrentAttemptsOrAudit`。

  ```csharp
  Assert.Equal(HttpStatusCode.Unauthorized, afterReenable.StatusCode);
  Assert.Equal(oldExpiry, localTicketAfterProviderEdit.ExpiresUtc);
  Assert.Equal(auditCountBeforeCleanup, auditCountAfterCleanup);
  ```
- [ ] **Step 2: 跑RED。** `bash scripts/check-sso.sh integration --filter 'FullyQualifiedName~SsoSessionRevocationTests|FullyQualifiedName~SsoAttemptCleanupTests'`。
- [ ] **Step 3: 实现。** 每请求查DB源/绑定/auth_revision/用户stamp/组织准入，重新启用不复活旧Cookie；不长期缓存撤销成功。清理可取消/失败重试、不触及其他表。
- [ ] **Step 4: GREEN。** 重跑过滤、SystemSettingsConsumerTests/PersistentSessionTests，确认真实TTL、非滑动及旧local票据不变。
- [ ] **Step 5: 记录。** 两实例撤销、Cookie过期解密、清理边界分别留证。

## Task 8：第39页高保真治理与异步状态

**Files:** 新建console SsoProviders/contracts/state、`console/tests/sso-state.test.mjs`；修改main/Shell/coverage/styles/tokens.css、settings/state.mjs及声明的平台页面key。

**Interfaces:** `createSsoState()`、`beginSsoRequest(state,providerId)`、`applySsoResponse(state,response,epoch)`、`applySsoFailure(state,status,epoch)`、`invalidateSsoState(state)`、`ssoAuthorityKey(user)`为纯状态接口；state含providerId/epoch/draft/etag/loaded/conflict/authority。401/403清详情及草稿，412保留。TS DTO与任务1一致；页面`SsoProviders()`路由/settings/sso。

- [ ] **Step 1: 写RED。** late result不能替换其他Provider、412保留草稿、撤权清引用/请求、组织筛选不误换平台page identity、dirty导航确认、历史测试不能启用。

  ```javascript
  assert.equal(applySsoResponse(current, oldResult, oldEpoch).providerId, current.providerId);
  assert.equal(applySsoFailure(draft, 412, epoch).draft.secretRef, draft.draft.secretRef);
  assert.equal(invalidateSsoState(draft).draft, null);
  ```
- [ ] **Step 2: 跑RED。** `"$WEBAPI_NODE" --test console/tests/sso-state.test.mjs`。
- [ ] **Step 3: 实现。** 原列/新增/编辑/测试/启停/默认接真实API，重试复用幂等键；组织筛选显式管理，平台页不随业务Scope重挂载。Drawer有引用状态、回调复制、Scopes/Mapping、测试阶段/时间/revision，完整pending/空/失败/冲突/撤权/dirty状态。
- [ ] **Step 4: GREEN与构建。** `bash scripts/check-sso.sh console`；Shell身份key包括user/平台权限；新页导航回归。覆盖状态仅在真实验收后更新，不将其他后续页标作可用。
- [ ] **Step 5: 记录。** 单测不宣称视觉QA，截图/交互归任务11。

## Task 9：SSO用户界面与登录完成页

**Files:** 新建SsoUserEditor、sso/login、`console/tests/sso-login.test.mjs`、`sso-user.test.mjs`；修改Governance/Login/SessionProvider/api/transport.mjs及声明/main。

**Interfaces:** `fetchLoginProviders(organizationId?:string,signal?:AbortSignal) → Promise<PublicSsoProviderDto[]>`、`createSsoStartForm(providerId:string,returnPath:string,csrfToken:string) → HTMLFormElement`；transport导出 `refreshCsrfToken() → Promise<string>`、`clearCsrfToken() → void`，与apiRequest同一缓存。`SsoUserEditor({userId?,onSaved,onCancel})`接双权限API；complete先SessionProvider.refresh、再刷新CSRF、再navigate。

- [ ] **Step 1: 写RED。** 默认不自动跳转、Provider读取失败local仍可登录、POST form不fetch跨域、complete刷新身份/CSRF后才写、SSO编辑不含密码/claim角色；错误query只接受白名单码/trace不原样渲染。

  ```javascript
  assert.equal(form.method, 'post');
  assert.equal(payload.password, undefined);
  assert.deepEqual(calls, ['me', 'csrf', 'navigate']);
  ```
- [ ] **Step 2: 跑RED。** `"$WEBAPI_NODE" --test console/tests/sso-login.test.mjs console/tests/sso-user.test.mjs`。
- [ ] **Step 3: 实现。** local入口常驻；organizationId链接不枚举组织；失败/取消可重试，已有会话先退出。用户页区分local/SSO，SSO无密码/隐式角色；双权限查看/修正，Active提示先停用。complete恢复后安全进入授权项目，无Scope显示真实空权限。重新验证用受控prompt，不接受任意协议参数。
- [ ] **Step 4: GREEN及local回归。** `bash scripts/check-sso.sh console`，client/navigation/settings测试通过；身份变化清旧CSRF与私有页。
- [ ] **Step 5: 记录文案。** 平台退出不代表IDP全局退出；说明还需原流程分配角色/Scope。

## Task 10：Keycloak隔离fixture与真实闭环

**Files:** 新建sso-images.lock.json、scripts/sso fixture/acceptance/scenario/evidence/cleanup；完善compose.sso-test.yml/check-sso.sh；新建 `tests/runtime/sso-fixture.test.mjs`、`tests/WebApi.EndToEnd.Tests/SsoLoopTests.cs`。

**Interfaces:** `createSsoFixture({root,retainForReview:false}) → Promise<SsoFixtureContext>`；context私有存project/owner/directory/ports/URLs/composeArgs；`runSsoScenario(context) → Promise<SanitizedSsoResult>`、`cleanupSsoFixture(context) → Promise<void>`、`assertOwnedSsoResources(context,resources) → void`。realm为webapi-sso-fixture、client为webapi-console，绑定/未绑定合成账号、所有密码及ClientSecret随机私有生成。

- [ ] **Step 1: 写RED。** 外来owner/project/目录/volume禁止清理，禁止4192/4196/4197端口/生产卷/symlink，无digest不得启动。E2E检查真实登录/me、未知用户/错误secret失败、localadmin成功、跨站临时Cookie及消费后重启拒重放。

  ```javascript
  assert.throws(() => assertOwnedSsoResources(ctx, foreignResources));
  assert.equal(result.realIdpLogin.passed, true);
  assert.equal(result.sameSiteEvidence.idpHost, 'localhost');
  assert.equal(result.sameSiteEvidence.platformHost, '127.0.0.1');
  ```
- [ ] **Step 2: 固定镜像及RED。** 解析官方 `quay.io/keycloak/keycloak:26.8.0` manifest digest/架构、写锁，再按digest拉取；本计划未取得实际digest。核对owner/独立端口，运行fixture Node测试及 `bash scripts/check-sso.sh e2e`，下载失败不算RED。
- [ ] **Step 3: 实现fixture。** 独立平台PG/Keycloak/CP/ConsoleHost/必要负例服务，固定现有PG/SDK镜像；confidential/S256/query/精确回调，无通配符。平台127.0.0.1、IDPlocalhost；container仅对指定localhost:port用Development连接覆盖到Docker主机，保留外部Issuer，不改全机hosts。独立持久KeyRing/secret只读挂载。
- [ ] **Step 4: GREEN。** 无自动redirect客户端逐步保留cookie跑真实code；fault服务只补负例。覆盖领取后中断、配置变更、双CP、重启、secret轮换，兑换/成功审计次数明确。输出脱敏JSON；私有review-context供浏览器，不公开。
- [ ] **Step 5: 保全/清理。** 生产只读核对公开状态，不输出密码；本owner容器/volume/secret清理证据。保留浏览器fixture时列owner与cleanup命令，浏览器后清理，不借用4192身份源。

## Task 11：真实浏览器QA、回归与完整审阅

**Files:** 新建 `scripts/sso/qa-checklist.json`、`docs/evidence/sso/ui/final-report.md`及截图/脱敏JSON；完善SsoLoopTests/实际缺陷；修改coverage、`docs/deployment/sso-oidc-capability.md`。

**Interfaces:** QA每项id/route/action/expected/evidence/status；只用合成账号及脱敏协议事实，无cookie/code/token/秘密文件路径。真实浏览器操作不能被直接DB改动替代。

- [ ] **Step 1: 操作验收。** 1440px列表/新增/编辑/分阶段测试/默认/启停/冲突/撤权；用户无密码绑定；真实Keycloak跳转/取消/错误secret/未知Subject；complete后写操作、退出/重新验证、停用源后401、localadmin登录。
- [ ] **Step 2: 逐图设计QA。** Shell/tokens、密度、长Issuer/名称/错误溢出、Drawer/disabled、焦点/键盘/标签、原生confirm、权限变化清私有内容。实际缺陷先定位、写能验证原因的测试再修，复验相关操作。
- [ ] **Step 3: 完整相关回归。** `bash scripts/check-sso.sh domain`、integration、gateway、console、e2e，记录实际数量/退出码，含local登录、系统设置、Scope与发布相关测试；不提前承诺测试数。
- [ ] **Step 4: 全分支独立审阅。** A本人逐任务、末尾独立审阅一次；B分派逐任务实现/审阅，末尾复核分支。重点信任边界、锁顺序、Token泄露、网络旁路、localadmin、撤销、证据真实性。修复阻塞项后复验受影响范围。
- [ ] **Step 5: 更新能力说明。** 通过后第39页标真实已接入；本机SSO/企业接入条件/生产TLS未验/无全局注销或自动授权分别写清，不改旧封存ZIP。

## Task 12：交付与4192升级提案准备

**Files:** 新建scripts/sso/verify.mjs、`scripts/package-sso.py`、`tests/runtime/sso-package.test.mjs`；产出deployment下`sso-oidc-runbook.md`、`sso-oidc-upgrade-proposal.md`、evidence/sso/final-verification.json及新ZIP，不覆盖旧deliverables。

**Interfaces:** `verifySsoDelivery({root,packagePath}) → Promise<VerificationResult>`；manifest含baseCommit、实际源码hash、依赖锁、镜像digest、证据/未执行边界。未提交源码冻结副本明确标`actual-uncommitted-build-source`，不能冒称baseCommit就是SSO完成commit。

- [ ] **Step 1: 写RED。** ZIP CRC/SHA/证据存在性；未验不可complete=true；排除node_modules/runtime/privatecontext/keyring/password/token；拒绝把旧4192镜像标为SSO。跑 `"$WEBAPI_NODE" --test tests/runtime/sso-package.test.mjs`。
- [ ] **Step 2: 中文runbook。** SecretFiles/AllowedOrigins/PublicBaseUrl、精确回调、测试含义、Subject开通及角色Scope、轮换/停用/退出/local恢复、fixture/清理；示例只有占位引用。
- [ ] **Step 3: 具体4192提案。** 列本次审阅源码/所需本地提交、验收、冷备份及新增卷、空Provider迁移/localadmin保全、停机/回退/SSO Cookie处置；授权明确为本批新提交/备份/升级，准备后再请求，不执行。
- [ ] **Step 4: GREEN公开包。** 冻结实际源码和工具，ZIP/readback/CRC/hash、secret扫描、证据完整性；`bash scripts/check-sso.sh verify`退出0。源码污染或浏览器未验不得宣称全部通过。
- [ ] **Step 5: 交付。** 新ZIP/SHA/runbook/证据及升级提案；主仓库摘要/旧ZIP SHA保全。不stage/commit/push/merge，待另次升级批准才固定commit构建/切换。

## 覆盖核对与执行交接

Spec§1—3/13→1/11/12；§4→1/3/5/7；§5→4/5/9；§6→2/6/10；§7→5/6/9/10；§8→4/5/7；§9→3/4/6；§10→8/9/11；§11→10；§12→各任务测试及11；§14依据随设计交付。

自审：12项均有文件/接口/具体检查；五项Review Focus各归属明确；跨任务类型一致；没有自动开户或生产部署扩展。协议交给标准处理器，计划不展开完整代码。

版本已核对 [OIDC10.0.12](https://www.nuget.org/packages/Microsoft.AspNetCore.Authentication.OpenIdConnect/10.0.12)、[IdentityModel8.19.2](https://www.nuget.org/packages/Microsoft.IdentityModel.Protocols.OpenIdConnect/8.19.2)、[Keycloak官方26.8.0](https://www.keycloak.org/downloads)。镜像digest/包还原/测试均为未执行动作，不能视为当前通过事实。

建议 **A：本会话逐任务实施，最终独立审阅**。模型、协议、会话、UI共享接口，顺序实施方便一致，末尾独立检查认证风险。**B：按任务分派子代理实现并逐项独立审阅**，独立复核更频繁，上下文开销更高。审阅计划并选择方式后进入任务1；目前没有创建SSOworktree、安装包、启动fixture或修改产品源码。
