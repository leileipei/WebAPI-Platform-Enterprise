# 工作包 1：登录限速、认证审计与 Header 信任边界 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or, if explicitly selected by the user, superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成可配置、跨实例一致的本地登录限速与脱敏审计，隔离代理来源和 Gateway 内部 Header，并取得隔离环境安全回归证据。

**Architecture:** Redis 原子操作负责 IP/账号两维预算，现有 PostgreSQL AuditLog 保存认证事件。框架依赖的小型 HttpSecurity 共享库统一三类 Host 的代理信任规则；Gateway 对请求及响应的平台 Header 有唯一写入来源。业务账号、API 授权、SSO、历史 Snapshot 和非 JWT 业务 Authorization 沿用既有语义。

**Tech Stack:** 现有锁定的 .NET 10、ASP.NET Core、YARP 2.3.0、EF Core/Npgsql、StackExchange.Redis、React/TypeScript、Node 22+、Docker Compose；不升级 NuGet/npm 或第三方源码依赖。

**Spec:** `/Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/docs/superpowers/specs/2026-10-08-security-hardening-design.md`（用户已确认）。

状态：实施计划待审阅；用户已选择 A，由主代理在当前会话逐项实现、测试，完成后统一审查。源码基线 `a11d41250c76eb42831797e9fabd5405906dc256`；本文件的任务均未执行，不能当作测试结果。

## Global Constraints

- 仅工作包 1；不实现 CORS/IP/Transform、新密码流程、Credential 统计或 CI/签名功能。
- 配额字段精确为 `loginIpMaxAttempts=60`、`loginIpWindowSeconds=60`、`loginAccountMaxAttempts=10`、`loginAccountWindowSeconds=300`；次数 1–10000，窗口 1–3600 秒。
- 固定窗口，所有进入认证的尝试占预算，成功不重置；两维原子准入，拒绝不消耗另一维、不续期。
- Redis TIME 决定时间，操作超时 1000ms；保护不可用返回 503，限速返回 429 和正整数秒 Retry-After，均不发会话 Cookie。
- 专用 32 字节 HMAC 秘密；用户名和来源 IP 不进入 Redis 键、通用日志或指标标签；平台审计 IP 列继续受既有权限约束。
- 限速拒绝审计按来源 IP HMAC 的 60 秒桶聚合，每桶至多一条；不宣称完整逐请求明细。
- 空可信代理集合不启用转发解析；只解析 X-Forwarded-For，有限跳数 4，不更改 OriginalHost/Scheme/CSRF/SSO 语义。
- 保留 ApiKey/Anonymous 的业务 Authorization；JWT 仅 ForwardBearer=true 时转发已验证 Bearer。
- 使用隔离工作树与随机 owner 的测试项目；不得对现有 admin 做暴力尝试、修改现有秘密或使用长期运行数据库开展测试。
- 保留主工作区现有未提交文档及截图；逐文件暂存本包变更，禁止 `git add .`，不把秘密、运行目录或备份加入 Git。
- 构建需要不可变提交时，仅在本包已获得相应提交授权后生成本包提交；没有授权则保存代码差异及测试证据，明确构建阶段尚未完成，不伪造固定版本。
- 本计划不直接切换 4192；先取得隔离安装证据，再完成原环境升级准备。生产身份源、真实入口代理及企业网络另行验收。

## Review Focus

1. 空集合及无效 CIDR 不能造成信任全部代理；映射 IPv6、多值转发链不可绕过配额（Task 3）。
2. 两实例并发触发账号/IP预算时不能超发、半扣预算或由拒绝续期造成永久锁定（Task 2/5）。
3. Redis/审计故障不能签发未受保护的会话；注销失败审计不能阻止用户清除 Cookie（Task 4/5）。
4. 缓存命中、重试、拒绝和历史回滚不能让上游/客户端伪造的平台 Header 成为输出（Task 6）。
5. Console 容器重建、旧配置 JSON、缺失秘密及恢复到新 owner 不能冒充已就绪或重置原账号（Task 1/7/8）。

## 文件职责与接口归属

| 单元 | 职责 | 所属任务 |
| --- | --- | --- |
| SecuritySettings/表单 | 配额、边界、旧 JSON 兼容与生效提示 | 1 |
| LoginRateLimits/RedisLoginRateStore/LoginKeyHasher | 两维共享预算、固定窗口、键隐私和部署依赖 | 2 |
| WebApi.HttpSecurity | 框架级可信代理、可信链后的 Header 清理；不引用 Infrastructure/YARP | 3 |
| AuthenticationAuditWriter/RedisAuthenticationAuditGate | 固定审计摘要、拒绝聚合租约及异常语义 | 4 |
| SessionEndpoints/Login 页面 | 将保护和审计接入真实登录；安全错误反馈 | 5 |
| GatewayPlatformHeadersMiddleware | 请求/响应内部 Header 唯一来源，兼容三种认证及代理分支 | 6 |
| scripts/security/runtime-secrets.mjs、proxy-trust.mjs | owner 校验、秘密和精确 Console IP 登记、恢复和状态检查 | 7 |
| scripts/security/acceptance.mjs、交付文档 | 隔离双实例/双网关闭环、证据及原部署升级准备 | 8 |

接口属于生产代码的精确约定列于各任务；测试辅助方法按同任务测试文件定义，不新增业务 API。

---

### Task 1：配额配置、旧数据兼容和安全设置表单

**Files:**
- Modify: `src/WebApi.Infrastructure/Settings/SettingsValues.cs`、`src/WebApi.Infrastructure/Settings/SystemSettingsValidator.cs`、`src/WebApi.Infrastructure/Settings/SystemSettingsService.cs`
- Modify: `console/src/pages/SystemSettings.tsx`
- Test: `tests/WebApi.Domain.Tests/SystemSettingsValidationTests.cs`、`tests/WebApi.Integration.Tests/SystemSettingsConsumerTests.cs`、`tests/WebApi.Integration.Tests/SystemSettingsPersistenceTests.cs`、`tests/WebApi.Integration.Tests/SystemSettingsCommandTests.cs`、`console/tests/system-settings.test.mjs`

**Interfaces:**
- Produces: `SecuritySettings` 在原四个位置参数后追加四个带默认值的 int 参数 `LoginIpMaxAttempts`、`LoginIpWindowSeconds`、`LoginAccountMaxAttempts`、`LoginAccountWindowSeconds`，默认值与 Global Constraints 一致。
- Existing `SystemSettingsReader.SecurityAsync(CancellationToken)` 返回扩展记录；四个字段的 `SystemSettingsService.Effect(string)` 返回 `LoginRequest`。

- [ ] Step 1：执行 using-git-worktrees 流程，核对可复用的已附属工作树或从基线创建隔离工作树；复制已批准设计/计划，不复制主工作区其他未提交内容。
- [ ] Step 2：增加失败测试：合法历史四字段 JSON 读出四个默认值；历史缺字段/重复/未知字段继续拒绝；新保存缺任一配额返回 422。对次数 0/10001、窗口 0/3601 拒绝，1/10000 和 1/3600 接受；断言示例：

```csharp
Assert.Equal(60, decoded.LoginIpMaxAttempts);
Assert.Equal(300, decoded.LoginAccountWindowSeconds);
Assert.Equal("LoginRequest", SystemSettingsService.Effect("loginIpMaxAttempts"));
```

- [ ] Step 3：运行 `bash scripts/check-policies.sh domain --filter FullyQualifiedName~SystemSettingsValidationTests -p:RestoreLockedMode=true`，确认新增测试失败原因对应缺失能力。
- [ ] Step 4：扩展记录、严格新格式校验和仅解码旧格式时的默认补齐；不在启动覆写旧数据库行。更新旧测试的“新保存请求”为八字段，保留四字段读取专用用例。
- [ ] Step 5：更新现有表单的 labels/limits/effects；说明“下一次本地登录生效”“配额变更开启新预算”。验证 412 冲突、预览 token 和权限失效仍生效。
- [ ] Step 6：上述 Domain 测试、`bash scripts/check-policies.sh integration --filter FullyQualifiedName~SystemSettings -p:RestoreLockedMode=true`、`bash scripts/check-console.sh` 通过后，记录本任务差异与证据；按已确认 Git 授权仅提交本任务文件。

### Task 2：HMAC 标识、两维 Redis 原子预算和测试依赖

**Files:**
- Create: `src/WebApi.Contracts/Security/LoginProtectionContracts.cs`
- Create: `src/WebApi.Domain/Security/LoginProtectionRules.cs`
- Create: `src/WebApi.Infrastructure/Security/LoginProtectionDeploymentSettings.cs`、`LoginKeyHasher.cs`、`RedisLoginRateStore.cs`、`LoginBudgetScript.cs`
- Create: `tests/WebApi.Domain.Tests/LoginProtectionRulesTests.cs`、`tests/WebApi.Integration.Tests/LoginRateStoreTests.cs`
- Create: `tests/WebApi.TestSupport/Support/LoginProtectionTestConfiguration.cs`
- Modify: `tests/WebApi.TestSupport/Support/ApiFixture.cs`（为 fixture 提供独立秘密/命名空间，尚不在 Host 中接入限速）

**Interfaces:**
- Produces: `LoginRateLimits(int IpMaxAttempts,int IpWindowSeconds,int AccountMaxAttempts,int AccountWindowSeconds)`；`LoginIdentityKeys(string Ip,string Account,string AuditAccount)`；`LoginRateRequest(LoginIdentityKeys Identity,LoginRateLimits Limits)`；`LoginRateDecision(LoginRateDecisionKind Kind,int RetryAfterSeconds=0)`，Kind 为 Allowed/Limited/Unavailable。
- Produces: `ILoginRateStore.TryAcquireAsync(LoginRateRequest request,CancellationToken ct)` 返回 `ValueTask<LoginRateDecision>`。
- Produces: `LoginProtectionRules.Validate(LoginRateLimits)`、`Fingerprint(LoginRateLimits)`；`LoginKeyHasher.Keys(IPAddress? ip,string username)`；`LoginProtectionDeploymentSettings.Read(IConfiguration)`。
- Deployment keys: `Authentication:LoginProtection:RedisPrefix`、`Authentication:LoginProtection:HmacSecretFile`，连接复用 `Redis:Connection`。秘密文件只接受 owner 私有的非链接文件及恰好 32 个解码后字节；base64 文本可含末尾换行。
- Test helper: `LoginProtectionTestConfiguration.Apply(WebApplicationBuilder builder,string directory,string deploymentId)` 设置随机秘密文件及独立 Redis 前缀；双 Host 测试显式共享这两项。

- [ ] Step 1：新增失败测试：IPv4/mapped IPv6 生成相同 IP 键；账号精确大小写语义；配额指纹只由四项生成；键不含 canary 用户名/IP，秘密长度和权限非法拒绝。
- [ ] Step 2：运行 Domain 定向红灯测试，再实现上述 DTO、规则和部署配置，复用锁定的 StackExchange.Redis，不添加新包。
- [ ] Step 3：新增真实 Redis 测试：IP 阈值 2、账号阈值 10，两个 store 并发 100 次仅允许 2 次；账号维耗尽不扣新的 IP 维；反向同理。检查被拒绝时 TTL 不增加，窗口到期可再次准入；测试窗口取合法 1 秒，设时间上限，不使用长 sleep。
- [ ] Step 4：运行 `bash scripts/check-policies.sh integration --filter FullyQualifiedName~LoginRateStoreTests -p:RestoreLockedMode=true` 确认失败，再实现同 slot 的两键 Lua 原子准入和 Redis TIME。只在准入时消费两维；Retry-After 为耗尽维剩余时间最大值向上取整。
- [ ] Step 5：加入超时/断连/非法结果/取消测试，验证 1000ms 限制、Unavailable 与调用方取消的区别；每个测试随机前缀，禁止 FLUSHDB/FLUSHALL。通过后保存证据和本任务提交检查点。

### Task 3：共享可信代理规则和转发 Header 清理

**Files:**
- Create: `src/WebApi.HttpSecurity/WebApi.HttpSecurity.csproj`、`TrustedProxySettings.cs`、`TrustedProxyExtensions.cs`、`ForwardingHeaderSanitizer.cs`、`packages.lock.json`
- Modify: `WebApi.Enterprise.sln`；`src/WebApi.ConsoleHost/WebApi.ConsoleHost.csproj`、`src/WebApi.ConsoleHost/ConsoleHostApp.cs`；`src/WebApi.ControlPlane/WebApi.ControlPlane.csproj`、`src/WebApi.ControlPlane/ControlPlaneApp.cs`；`src/WebApi.Gateway/WebApi.Gateway.csproj`、`src/WebApi.Gateway/GatewayApp.cs`；三个 Host 及测试项目受 ProjectReference 变更影响的 `packages.lock.json`
- Create: `tests/WebApi.Integration.Tests/TrustedProxyBoundaryTests.cs`
- Modify: `tests/WebApi.Integration.Tests/OidcConsoleHostTests.cs`

**Interfaces:**
- Produces: `TrustedProxySettings.Read(IConfiguration)`，配置为 `HttpSecurity:TrustedProxyIps` 和 `HttpSecurity:TrustedProxyNetworks`，空集合表示不解析，ForwardLimit 固定 4。
- Produces: `IServiceCollection.AddTrustedProxyBoundary(IConfiguration)`、`IApplicationBuilder.UseTrustedProxyBoundary()`；后者先按可信清单解析 IP，再删除外部 Forwarded/X-Forwarded-*/X-Original-*。
- New project only references `Microsoft.AspNetCore.App` FrameworkReference，不引用 Infrastructure/EF/Redis/YARP；三类 Host 显式 ProjectReference，更新受影响项目锁文件而不升级版本。

- [ ] Step 1：写真实 socket 测试：空清单 + 伪造 XFF 保持 peer IP；精确登记 loopback 后单跳取给定合成 IP；未知代理、非法/重复链、4 跳之外不能扩展信任。断言 `0.0.0.0/0`、`::/0`、全私网范围配置拒绝；不得清空集合后无条件调用 UseForwardedHeaders。
- [ ] Step 2：运行 `bash scripts/check-policies.sh integration --filter FullyQualifiedName~TrustedProxyBoundaryTests -p:RestoreLockedMode=true` 确认红灯。
- [ ] Step 3：实现共享库，使用 .NET 10 的 `KnownProxies` 和 `KnownIPNetworks`，仅启用 XForwardedFor；空清单跳过框架中间件。保留原 Host、Scheme；非法输入按框架停止解析，不将任意字符串映射为可信 IP。
- [ ] Step 4：Control Plane 置于认证/审计前，Gateway 置于 RequestTelemetry 前，ConsoleHost 置于代理前；YARP 重新生成链，测试上游不收到客户端伪造的 Forwarded 链。
- [ ] Step 5：定向测试和既有 OidcConsoleHost/PersistentSession/SSO 路由回归通过，确认 CSRF Origin 和回调地址不变；记录证据和提交检查点。

### Task 4：固定认证审计及有界拒绝聚合

**Files:**
- Create: `src/WebApi.Infrastructure/Security/AuthenticationAuditWriter.cs`、`RedisAuthenticationAuditGate.cs`
- Create: `tests/WebApi.Integration.Tests/AuthenticationAuditTests.cs`
- Modify: `tests/WebApi.Integration.Tests/SystemSettingsAuditExportTests.cs`（可见范围与脱敏回归）

**Interfaces:**
- Produces: `AuthenticationAuditEvent(string Action,Guid? UserId,IPAddress? Ip,string TraceId,string? AccountFingerprint,string Code)`；允许 Action 仅 auth.login/auth.login.failed/auth.login.throttled/auth.logout，Code 由固定枚举映射。
- Produces: `IAuthenticationAuditWriter.WriteAsync(AuthenticationAuditEvent,CancellationToken)` 返回 Task；成功审计由登录事务内调用，失败事件由失败事务之外的新 scope 写入。
- Produces: `AuthenticationAuditLease(string BucketKey,string Token,DateTimeOffset FirstRejectedAt)`；`IAuthenticationAuditGate.ClaimAsync(string ipFingerprint,CancellationToken)` 返回 `ValueTask<AuthenticationAuditLease?>`，`ConfirmAsync(AuthenticationAuditLease,CancellationToken)` 和 `ReleaseAsync(AuthenticationAuditLease,CancellationToken)` 返回 ValueTask。
- 租约最长 5 秒且不得超过桶剩余时间；审计提交后 Confirm，失败 Release；60 秒桶和租约时间均取 Redis TIME。迟到持有者必须校验 Token，不能删除下一位持有者状态。

- [ ] Step 1：写失败测试：匿名失败无 UserId/组织/项目/环境；AuditLog 无 canary 密码、Cookie、Bearer、原始用户名或请求体；平台审计可见、组织审计不可见，CSV 不扩散秘密。
- [ ] Step 2：新增 100 个并发拒绝聚合测试，仅一条审计；持有者写入失败可以重试；过期旧 Token 不能确认或释放新租约；审计提交后重复请求不追加。缺失桶剩余时间不发永久租约。
- [ ] Step 3：运行 `bash scripts/check-policies.sh integration --filter FullyQualifiedName~AuthenticationAuditTests -p:RestoreLockedMode=true` 红灯后实现固定 DTO 序列化、平台归属及租约规则；不接受任意 JSON/异常对象做审计摘要。
- [ ] Step 4：增加无标识标签的限速拒绝总量和审计失败 Meter 计数；注销审计调用失败可报告固定码，不能抛出包含密码的通用异常。
- [ ] Step 5：审计测试与 SystemSettingsAuditExportTests 通过，记录 DB 写入上限、可见范围和故障结果，保存提交检查点。

### Task 5：真实登录/注销接入和用户错误提示

**Files:**
- Modify: `src/WebApi.ControlPlane/Security/SessionEndpoints.cs`、`src/WebApi.ControlPlane/ControlPlaneApp.cs`
- Modify: `tests/WebApi.TestSupport/Support/ApiFixture.cs`、`tests/WebApi.Integration.Tests/PersistentSessionTests.cs`
- Create: `tests/WebApi.Integration.Tests/LoginProtectionPipelineTests.cs`
- Modify: `console/src/api/transport.mjs`、`console/src/api/transport.d.mts`、`console/src/pages/Login.tsx`
- Create: `console/src/auth/login-protection.mjs`、`login-protection.d.mts`、`console/tests/login-protection.test.mjs`

**Interfaces:**
- Consumes: Tasks 1–4 的 SecuritySettings、ILoginRateStore、LoginKeyHasher、审计 writer/gate 和可信 RemoteIpAddress；沿用 `AccountService.AuthenticateAsync` 和 `PlatformSessionIssuer.IssueAsync`。
- Produces: auth/login 的 429 `login_rate_limited`、503 `login_protection_unavailable`、503 `authentication_audit_unavailable`；错误响应继续使用 ProblemDetailsMapping，包含 TraceId，无预算维度。
- Frontend: ApiError 增加可选 `retryAfterSeconds`；`loginProtectionMessage(code,retryAfterSeconds)` 返回固定中文安全提示；仅解析 1–3600 的整数秒，不信任任意 Header 文本为页面 HTML。

- [ ] Step 1：红灯测试：合成账号 10 次进入认证，下一次 429 且无会话 Cookie；两个真实 ControlPlane Host 共享账户预算；成功不清零；轮换可信合成 IP 不绕过账号预算。用计数 PasswordHasher 证明被限速/Redis 异常请求未执行密码验证。
- [ ] Step 2：接入顺序为现有 CSRF/Origin → 基础字段校验 → SecuritySettings/共享预算 → 原认证及事务内状态重查 → 审计提交 → 会话签发。不把限速用于 auth/me、SSO 回调或 Gateway 业务请求。
- [ ] Step 3：统一失败认证追加匿名审计；预算故障 503 不发 Cookie；成功审计故障不签发会话。注销先清 Cookie，再写审计；审计故障返回已注销语义并报固定运营信号。
- [ ] Step 4：所有通过 ApiFixture 启动的旧测试使用随机保护命名空间，重建 Host 的测试显式复用 fixture 秘密/命名空间；只有测试环境准备 helper，生产缺少秘密不能自动生成。验证已建立会话在 Redis 故障时仍可读取 auth/me。
- [ ] Step 5：前端保持密码不出现在错误信息；显示“登录尝试过于频繁，请在 N 秒后重试”或“登录保护暂不可用，请稍后重试”。前端不自动重复发送密码，不增加倒计时自动登录。
- [ ] Step 6：运行 LoginProtectionPipelineTests、SessionTests、PersistentSessionTests、Sso 及 SystemSettings 定向回归和 console 登录提示测试；验证缺 CSRF/外域仍 403、账号停用竞态仍拒绝，全部通过后保存检查点。

### Task 6：Gateway 请求/响应内部 Header 唯一来源

**Files:**
- Create: `src/WebApi.Gateway/Security/GatewayPlatformHeadersMiddleware.cs`
- Modify: `src/WebApi.Gateway/Security/ApiKeyMiddleware.cs`、`src/WebApi.Gateway/GatewayApp.cs`、`src/WebApi.Gateway/Policies/CacheResponseMiddleware.cs`
- Create: `tests/WebApi.Gateway.Tests/GatewayPlatformHeaderBoundaryTests.cs`
- Modify: `tests/WebApi.Gateway.Tests/JwtAuthorizationPipelineTests.cs`、`tests/WebApi.Gateway.Tests/ResponseCachePipelineTests.cs`、`tests/WebApi.Gateway.Tests/RetryPipelineTests.cs`、`tests/WebApi.Gateway.Tests/CacheActivationTests.cs`

**Interfaces:**
- Produces: `GatewayPlatformHeadersMiddleware.InvokeAsync(HttpContext)`，放在已匹配的 MapReverseProxy 子管线最外层；入站清除 X-WebApi-*，登记统一 OnStarting 回调。
- Consumes: ApiKeyMiddleware 在获取固定 generation 后设置的 `TrafficExecutionContext`；响应回调最终清除上游/缓存 X-WebApi-* 后注入 TraceId 和已获取的 sequence。不从客户端 Header 读 generation。

- [ ] Step 1：参数化三模式真实上游回显测试：X-WebApi-Fake/Application-Id/Subject/Trace-Id/Deployment-Sequence 及大小写/多值伪造全部剥离；上游只见 Gateway 生成的两个诊断 Header，平台 API Key 不外传。
- [ ] Step 2：响应伪造红灯测试：上游设置同名前缀；调用方仅得到平台诊断 Header，没有上游 Fake；正常代理、401/403/429/503、缓存命中与重试覆盖相同断言。
- [ ] Step 3：删除只在 JWT 模式清理的条件，集中生命周期处理；移除 CacheResponseMiddleware 的重复 Header 注入，避免 OnStarting 回调覆盖顺序不确定。不要改非 JWT Authorization 或增加身份 Header。
- [ ] Step 4：运行 `bash scripts/check-policies.sh gateway --filter 'FullyQualifiedName~GatewayPlatformHeaderBoundaryTests|FullyQualifiedName~JwtAuthorizationPipelineTests' -p:RestoreLockedMode=true`；同时跑已有缓存/重试/历史 Snapshot 回滚定向用例。
- [ ] Step 5：检查 2.0/2.1/2.2 历史 Snapshot 无数据格式更改；双网关、回滚、重启、健康/未就绪未取得 generation 的响应不伪造 sequence，证据齐全后保存检查点。

### Task 7：秘密、代理登记及本机生命周期/恢复支持

**Files:**
- Create: `scripts/security/runtime-secrets.mjs`、`scripts/security/proxy-trust.mjs`
- Modify: `scripts/runtime/context.mjs`、`scripts/runtime/lifecycle.mjs`、`scripts/runtime/configure.mjs`、`scripts/runtime/status.mjs`、`scripts/runtime/acceptance-backup.mjs`
- Modify: `deploy/compose.runtime.yml`、`deploy/compose.runtime.gateways.yml`
- Create: `tests/runtime/security-secrets.test.mjs`、`proxy-trust.test.mjs`、`tests/runtime/integration/security-proxy.test.mjs`
- Modify: `tests/runtime/backup.test.mjs`、`tests/runtime/lifecycle.test.mjs`、`tests/runtime/status.test.mjs`

**Interfaces:**
- Produces: `prepareLoginProtectionSecret(directory,state,{allowCreate:false})`、`assertLoginProtectionSecret(directory,state)`，返回无明文的配置路径/哈希；文件名 `login-protection-hmac`，专用既有 secrets-control-plane 卷挂载，不新增业务表。
- Produces: `resolveOwnedConsoleProxy(state,{inspect})` 返回 `{containerId,networkId,ipAddress}`；`applyConsoleProxyTrust(ctx,receipt)` 写私有运行 overlay、仅重建 Control Plane 并复核同一 Console 身份；`verifyConsoleProxyTrust(state,receipt,{inspect})` 返回可信登记一致性。
- Status 增加 `loginProtection` 摘要，秘密可用和代理一致性未知/失败时不能报告完整 Ready；摘要不输出秘密或可复用认证材料。

- [ ] Step 1：单元红灯测试：非 owner 容器/网络、同名资源无标签、多个 Console、非法/空 IP 拒绝；一台 owner Console 换 IP 后旧 receipt 不再有效。预设私有网段范围不作为允许清单。
- [ ] Step 2：秘密测试：未显式 allowCreate 不生成；现有秘密永不轮换；丢失、错误权限、链接、错误长度阻断；冷备/恢复使用原字节，新 owner 仅更新 Redis 命名空间而不重置用户密码。
- [ ] Step 3：更新初始化卷只向 Control Plane 注入专用秘密；旧部署新增该秘密需明确的升级准备入口。历史旧 release 不支持本包时保持旧能力状态，不用新配置冒充旧镜像支持。
- [ ] Step 4：生命周期先启动 owner Console，再登记精确地址到 Control Plane；up/start/restart/configure 均复核，禁止递归 prepareContext/restart 循环。若重建不完成，标 Degraded/Unknown，保留旧配置与诊断，不推断就绪。
- [ ] Step 5：扩展原冷备 manifest 的秘密校验，不盲目信任恢复后的旧 Console IP；保留已有所有权、数据卷、SSO、缓存和通知备份逻辑。
- [ ] Step 6：执行新 runtime 定向测试及现有 state/context/lifecycle/backup/status 回归，再在随机 owner 长期栈执行真实 Console 重建与回填验证；仅查看本测试资源，保存证据和检查点。

### Task 8：隔离闭环、文档和原部署升级准备

**Files:**
- Create: `scripts/security/acceptance.mjs`、`docs/deployment/security-hardening-runbook.md`
- Create: `docs/evidence/security-hardening/verification.json`、`source-review.md`（只在真实执行后写结果）
- Modify: 本包实际测试适配涉及的 TestSupport/直接 Host 构造点；`docs/session-security.md` 中与本包相关的事实说明

**Interfaces:**
- Acceptance entry: `node scripts/security/acceptance.mjs --revision <本包固定提交>`；随机 owner、项目与端口，使用合成账号，输出分阶段事实。源码 SHA、运行镜像、测试结果、UI 与安装状态分别记录。
- 命令失败必须保留可诊断状态，不对主部署执行 cleanup；清理只接受本次 owner receipt。未取得固定提交或浏览器证据时 complete=false，不能以脚本执行成功代替完整验收。

- [ ] Step 1：将 Spec 第 9 节逐项映射到测试名称和证据文件。运行 Domain、Integration、Gateway 对本包影响范围的完整项目回归，以及 console/runtime 全部单元测试；不重复通过的检查，除非新改动或失败需要重跑。
- [ ] Step 2：以固定源码构建隔离镜像，启动双 ControlPlane 实例共享 Redis/HMAC、一个 Console、双 Gateway 和合成上游；真实证明并发预算、失败审计、代理边界、平台 Header、缓存/重试及历史回滚。Redis 故障期间验证已登录会话继续可用，不能只靠 fake store。
- [ ] Step 3：实际浏览器检查 1440px 安全设置配额编辑/预览/冲突/权限，以及合成账号登录 429/503 提示；截图排除密码、Cookie、API Key 和真实账号。
- [ ] Step 4：在隔离环境从旧格式 security 行升级、冷备后恢复到新 owner，验证账号/密码/原业务事实与新秘密；明确 Redis 重启会重置临时预算、不会改变账号身份。
- [ ] Step 5：完整审查本包 diff，处理安全/兼容性问题并定向重测；根据用户所选执行方式使用对应技能的最终审查流程。不能把主工作区已有截图/文档混入本包。
- [ ] Step 6：写 runbook 和真实 verification：源码/测试/部署分开，记录测试数、失败/跳过及原因；没有完整证据则 complete=false。准备 4192 原数据保全、升级预检、冷备及兼容回退方案，但本计划阶段不执行切换。

## 自检与执行交接

- Spec 配额、状态码、Redis TIME/原子性、旧 JSON、IP 信任、四种审计事件、Header 三模式/响应分支和恢复均有对应任务。
- 五项 Review Focus 分别由 Tasks 1–8 的具体失败用例覆盖；计划不把固定窗口当滑动窗口，不把平台诊断 Header 当签名身份。
- 未运行产品测试；未新增产品代码；文件角色清晰、接口无业务跨层引用。
- 用户已选择当前会话逐项实施；仅待本计划审阅。任务共享配额、审计和 Host 管线接口，集中实现便于控制兼容性；实施完成后统一独立审查，不再重复询问执行方式。
