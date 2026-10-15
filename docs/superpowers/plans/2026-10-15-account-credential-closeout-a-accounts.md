# 第4包A：账号安全 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. 沿用当前会话实施，不派发子代理。

**Goal:** 新账号先改密再获得业务权限，现有账号迁移保持原行为，管理员显式重置具有权限、并发和秘密保护。

**Architecture:** 在原UserRecord和审计事务增加生命周期字段，复用原PasswordHasher、Redis限速和Cookie签发；统一HTTP及服务授权边界。SSO密码仍由IdP管理。

**Tech Stack:** 现有.NET/EF Core/PostgreSQL/Redis/React；版本与命令见[总计划](2026-10-15-account-credential-closeout.md)。

**Spec:** [规格§3–5、§12、P4-01–03](../specs/2026-10-15-account-credential-closeout-design.md)。总计划Global Constraints对本文件全部适用。

## Global Constraints

- 仅新本地账号和空库bootstrap置true；既有账号false；SSO保持false/无本地Hash。
- 改密32KiB、重置16KiB、密码最大1024原始字符；不trim、不Unicode规范化；复用当前PasswordMinLength/PasswordComplexity。
- 改密Cookie最长10分钟、非持久、不滑动；成功提交后才替换Cookie。
- 规格的“平台级user.write”为写管理能力描述；源码实际权限代码为`user.manage`。重置沿用该代码和平台范围，不增添/重命名权限，不扩大原角色授权。
- 本阶段无自动提交、原账号重置、4192操作；每个任务Step5只形成审阅记录。

## Review Focus

1. A1：旧库Hash/Stamp/角色与范围逐字节/逐行保持，不能推断旧账号首次登录。
2. A2：直接调用集合Scopes或导出绕过HTTP时仍无业务权限；旧Cookie claim不能替代数据库标记。
3. A3：Unicode组合字符/前后空格、并发旧Stamp、响应丢失与审计故障必须有明确结果。
4. A4：不同密码但相同Idempotency-Key、过期ETag、唯一管理员和SSO目标不能绕过重置边界。
5. A5：受限会话刷新/深链接/多标签页不能闪现业务数据；临时密码不能落localStorage或错误日志。

---

### Task A1：账号字段、兼容迁移与创建路径

**Files:**
- Modify: `src/WebApi.Infrastructure/Persistence/Entities/UserRecord.cs`、`src/WebApi.Infrastructure/Persistence/Configurations/UserRecordConfiguration.cs`、`src/WebApi.Infrastructure/Governance/GovernanceService.cs`、`src/WebApi.Migrator/BootstrapAccounts.cs`、`src/WebApi.Contracts/Governance/GovernanceContracts.cs`。
- Create: `src/WebApi.Infrastructure/Persistence/Migrations/20261015010000_AccountPasswordLifecycle.cs` 及同名`.Designer.cs`；Modify: 同目录`WebApiDbContextModelSnapshot.cs`。
- Test/Create: `tests/WebApi.Integration.Tests/PasswordLifecycleMigrationTests.cs`。

**Interfaces:** 产生`UserRecord.MustChangePassword:bool`、`PasswordChangedAt:DateTimeOffset?`；`UserDto`追加`bool MustChangePassword`，保留既有字段顺序。新字段DB默认false/null；CreateUser和首次Bootstrap显式true，既有initialized目录不再初始化。

- [ ] **Step1 RED:** `Existing_accounts_keep_hash_stamp_roles_and_scopes` 比较迁移前后原记录；`Fresh_admin_and_created_local_user_require_change`、`Sso_remains_without_local_password`。
  ```csharp
  Assert.False(existing.MustChangePassword); Assert.Null(existing.PasswordChangedAt);
  Assert.Equal(before.PasswordHash, existing.PasswordHash); Assert.Equal(before.SecurityStamp, existing.SecurityStamp);
  Assert.Equal(before.Grants, after.Grants); Assert.True(createdLocal.MustChangePassword);
  Assert.False(sso.MustChangePassword); Assert.Null(sso.PasswordHash);
  ```
- [ ] **Step2:** `bash scripts/check-contracts.sh integration --filter FullyQualifiedName~PasswordLifecycleMigrationTests`；预期新增字段/迁移断言失败，保留日志。
- [ ] **Step3:** 仅增加上述字段、映射、DTO和创建赋值。用已固定`dotnet-ef 10.0.12`生成迁移，迁移ID固定为`20261015010000_AccountPasswordLifecycle`并同步Designer属性；真实PG校验Up/Down及ModelSnapshot一致。Down只能用于隔离演练；存在待改密账号时不允许用旧应用接管。
- [ ] **Step4:** 重跑Step2，全部PASS；分别从前一迁移旧库和空库升级，确认历史19条迁移未被改写。
- [ ] **Step5:** 保存限定差异、数据库摘要及日志，登记后续固定验收；不提交。

### Task A2：受限身份、Cookie与业务授权边界

**Files:**
- Create: `src/WebApi.Infrastructure/Security/PasswordLifecycleGuard.cs`、`src/WebApi.ControlPlane/Security/PasswordChangeRequiredMiddleware.cs`。
- Modify: `src/WebApi.Contracts/Security/IdentityContracts.cs`、`src/WebApi.Infrastructure/Security/AccountService.cs`、`AuthorizationService.cs`、`CollectionAuthorizationQuery.cs`、`src/WebApi.ControlPlane/Security/PlatformSessionIssuer.cs`、`SessionEndpoints.cs`、`src/WebApi.ControlPlane/ControlPlaneApp.cs`。
- Test/Create: `tests/WebApi.Integration.Tests/PasswordRequiredSessionTests.cs`；Test/Modify: `SessionTests.cs`、`SsoSessionRevocationTests.cs`（同测试目录）。

**Interfaces:** `IdentityDto`末尾追加`string AuthSource,bool MustChangePassword`。`PasswordLifecycleGuard.RequireBusinessAccessAsync(ActorContext actor,CancellationToken ct):Task`读取Active/flag。`PlatformSessionIssuer.IssueRestrictedAsync(HttpContext context,Guid userId,string securityStamp,int systemTtlMinutes,CancellationToken ct):Task`独立于原`IssueAsync`；前者明确禁止续期，原本地/SSO签发不改。

- [ ] **Step1 RED:** `Restricted_identity_has_no_permissions_or_scopes`、`Restricted_cookie_cannot_read_write_export_or_call_services`、`Reset_invalidates_cookie_before_route_execution`、`Restricted_cookie_expires_without_sliding`。
  ```csharp
  Assert.Empty(identity.Permissions); Assert.Empty(identity.Scopes); Assert.True(identity.MustChangePassword);
  Assert.Equal(403, business.Status); Assert.Equal("password_change_required", business.Code);
  Assert.Equal(0, directCollection.Count); Assert.False(cookie.IsPersistent);
  Assert.Equal(TimeSpan.FromMinutes(10), cookie.ExpiresUtc-cookie.IssuedUtc);
  ```
  参数化系统TTL=5/30分钟，应为5/10；测试资料导出、列表、详情、直接`RequireAsync`、`RequireActiveActorAsync`和`Scopes`。集合Scopes本身必须在SQL中排除flag；业务入口返回403，不能以空列表假装已授权。
- [ ] **Step2:** `bash scripts/check-contracts.sh integration --filter FullyQualifiedName~PasswordRequiredSessionTests`，预期访问控制断言RED。
- [ ] **Step3:** Middleware置于认证/当前Stamp验证之后、业务处理之前；精确放行四个认证端点，公共SSO/登录和内部nodeBearer保留原协议。所有普通请求读取当前DB状态/Stamp；服务Guard检查Active/flag，集合Scopes增加flag条件，集合入口调用Guard以提供403。不得修改持久角色/范围。成功登录根据数据库flag选择签发方法。
- [ ] **Step4:** 重跑Step2及`bash scripts/check-contracts.sh integration --filter 'FullyQualifiedName~SessionTests|FullyQualifiedName~SsoSessionRevocationTests'`，均PASS，SSO/普通Cookie时长不变。
- [ ] **Step5:** 列出所有HTTP、服务、集合/导出入口覆盖结果和未覆盖入口；有遗漏则阻断本任务关闭，不提交。

### Task A3：自助改密事务、独立限速和会话替换

**Files:**
- Create: `src/WebApi.Contracts/Security/PasswordLifecycleContracts.cs`、`src/WebApi.Infrastructure/Security/PasswordLifecycleService.cs`、`PasswordPolicyValidator.cs`。
- Modify: `src/WebApi.Infrastructure/Security/LoginKeyHasher.cs`、`src/WebApi.Infrastructure/Persistence/AuditedCommandExecutor.cs`、`src/WebApi.ControlPlane/Security/SessionEndpoints.cs`、`src/WebApi.ControlPlane/ControlPlaneApp.cs`。
- Test/Create: `tests/WebApi.Integration.Tests/PasswordLifecycleTests.cs`。

**Interfaces:** `ChangePasswordRequest(string CurrentPassword,string NewPassword,string ConfirmPassword)`；内部`PasswordChangeGrant(Guid UserId,string SecurityStamp,IdentityDto Identity)`禁止JSON/审计序列化。`PasswordLifecycleService.ChangeAsync(ActorContext actor,string expectedStamp,ChangePasswordRequest request,CancellationToken ct):Task<PasswordChangeGrant>`。`PasswordPolicyValidator.Validate(string password,int minLength,string complexity):void`复用已有策略枚举；`LoginKeyHasher.PasswordChangeKeys(IPAddress? ip,Guid userId):LoginIdentityKeys`使用独立purpose，原Keys保持字节兼容。

- [ ] **Step1 RED:** `Only_one_concurrent_old_stamp_can_change`、`Audit_failure_rolls_back_password_and_stamp`、`Lost_response_recovers_by_new_login_not_old_cookie_replay`、`Password_bytes_are_not_normalized`、`Redis_failure_and_rate_limit_fail_closed`。
  ```csharp
  Assert.Equal(1, concurrent.Count(x=>x.Status==200)); Assert.Equal(before.Hash, rolledBack.Hash);
  Assert.Equal(before.Stamp, rolledBack.Stamp); Assert.False(after.MustChangePassword);
  Assert.NotEqual(before.Stamp, after.Stamp); Assert.Equal(401, oldCookieReplay.Status);
  Assert.Equal(503, redisDown.Status); Assert.Equal(429, limited.Status); Assert.True(limited.RetryAfter>0);
  ```
  另测确认不同、新旧相同、超1024、32KiB+1、前后空格、组合字符、停用/SSO、CSRF和Idempotency-Key缺失；实际秘密扫描覆盖requestlog/audit/outbox/replay。
- [ ] **Step2:** `bash scripts/check-contracts.sh integration --filter FullyQualifiedName~PasswordLifecycleTests`，预期404或事务断言RED。
- [ ] **Step3:** 实现`POST /api/v1/auth/change-password`。限速复用`ILoginRateStore`和配置预算，按IP/当前用户独立键；获取现有治理advisory锁8901202及用户行锁，重读Stamp/flag/Hash，原PasswordHasher校验。一次事务更新Hash、flag=false、PasswordChangedAt/UpdatedAt/Revision/Stamp和脱敏审计，审计白名单仅加flag/time。提交后才用原issuer签普通Cookie并只返回Identity；不持久重放签发结果。把CreateUser密码规则调用统一Validator，避免重置/改密规则漂移。
- [ ] **Step4:** 重跑Step2及A2套件，全部PASS；并发与故障必须使用真实PG/Redis，模拟层测试不能代替事务结论。
- [ ] **Step5:** 保存事务失败、成功和秘密扫描证据；不记录私有密码内容，不提交。

### Task A4：管理员显式重置与HMAC幂等

**Files:**
- Modify: A3的`PasswordLifecycleContracts.cs`、`PasswordLifecycleService.cs`、`LoginKeyHasher.cs`；`src/WebApi.ControlPlane/Governance/GovernanceEndpoints.cs`。
- Test/Create: `tests/WebApi.Integration.Tests/PasswordResetTests.cs`；Test/Modify: `GovernanceTests.cs`。

**Interfaces:** `ResetPasswordRequest(string TemporaryPassword)`；`PasswordResetDto(Guid UserId,long Revision,bool MustChangePassword)`。`ResetAsync(Guid targetId,ResetPasswordRequest request,string? ifMatch,ActorContext actor,CommandRequestContext command,CancellationToken ct):Task<CommandResult<PasswordResetDto>>`。`LoginKeyHasher.PasswordResetFingerprint(Guid actorId,Guid targetId,long expectedRevision,string password):string`用原32字节私钥HMAC-SHA256及动作域；输出只用于规范化幂等输入。

- [ ] **Step1 RED:** `Expired_etag_cannot_replay_reset_success`、`Different_password_same_key_conflicts`、`Resetting_sole_admin_preserves_admin_roles_and_scopes`、`Sso_disabled_self_and_nonplatform_resets_denied`。
  ```csharp
  Assert.Equal(412, stale.Status); Assert.Equal(409, changedInput.Status);
  Assert.Equal(before.RoleIds, after.RoleIds); Assert.Equal(before.ScopeIds, after.ScopeIds);
  Assert.True(after.MustChangePassword); Assert.Equal(401, previousSession.Status);
  Assert.DoesNotContain(temporaryPassword, persistedAuditAndReplayText);
  ```
  同时测试临时密码变化/目标变化/操作者变化指纹不同、不可用普通SHA猜测；成功响应仅含目标/修订/flag，无密码/Hash/Stamp。
- [ ] **Step2:** `bash scripts/check-contracts.sh integration --filter FullyQualifiedName~PasswordResetTests`，预期RED。
- [ ] **Step3:** 接入`POST /api/v1/users/{id}/password-reset`，`user.manage`平台权限、CSRF、16KiB、If-Match。受限操作者不得重置。治理锁/目标行锁内先重新授权/ETag，再以目标/预期修订/目的HMAC输入调用现有IdempotentCommandExecutor；Hash/Stamp/flag/Revision及`user.password.reset`审计同事务。保留LocalAdministratorGuard，不动角色状态；不读写主机bootstrap密码文件。
- [ ] **Step4:** 重跑Step2及`bash scripts/check-contracts.sh integration --filter FullyQualifiedName~GovernanceTests`；PASS且旧Cookie立刻失效，审计故障无半成功。
- [ ] **Step5:** 精确审阅权限和秘密输入/输出边界，记录规格权限用词与源码代码映射，不提交。

### Task A5：首次改密与重置UI、夹具兼容

**Files:**
- Create: `console/src/auth/password-state.mjs`、`password-state.d.mts`、`console/src/pages/ChangePassword.tsx`、`console/src/auth/PasswordResetDialog.tsx`、`console/tests/password-state.test.mjs`。
- Modify: `console/src/main.tsx`、`console/src/auth/SessionProvider.tsx`、`console/src/api/types.ts`、`console/src/api/client.ts`、`console/src/pages/Governance.tsx`。
- Modify: `tests/WebApi.TestSupport/Support/ApiFixture.cs`；Create: `tests/WebApi.EndToEnd.Tests/PasswordLifecycleFlowTests.cs`。
- Create: `scripts/account-credential/auth.mjs`、`tests/account-credential/auth.test.mjs`、`docs/evidence/work-package4/auth-call-sites.json`；Modify: `scripts/runtime/acceptance-scenario.mjs`、`scripts/network-policies/scenario.mjs`、`scripts/prepare-e2e.mjs`、`scripts/wait-nodes.mjs`、`scripts/e2e-faults.mjs`，仅适配独立空库/新本地用户的首次改密和后续重登录。

**Interfaces:** `passwordRoute(identity:IdentityDto|null,path:string):string`待改密固定`/auth/change-password`；TS Identity与A2一致。`completeInitialPasswordChange({client,identity,currentPassword,newPassword}):Promise<IdentityDto>`只对明确flag=true调用A3接口，随机新密码仅保存在当前owner私有夹具，flag=false不改密码。fixture密码采用每用户名独立映射`passwordFiles:Record<string,string>`，上下文只含0600私有文件路径；后续登录不得继续用旧的共享bootstrap-password。测试fixture提供同等显式HTTP流程，无直接清flag捷径。

- [ ] **Step1 RED:** `restricted_deep_link_stays_on_password_form`、`password_form_does_not_trim_or_persist_secret`、`existing_login_never_changes_password`、`new_fixture_admin_changes_via_http`。
  ```javascript
  assert.equal(passwordRoute({mustChangePassword:true}, '/applications'), '/auth/change-password');
  assert.equal(passwordRoute({mustChangePassword:false}, '/applications'), '/applications');
  assert.equal(existingAccount.changeRequests.length, 0);
  assert.equal(newAdmin.changeRequests.length, 1);
  ```
  浏览器E2E另验证多标签旧会话失效、表单错误保留非秘密上下文、成功后恢复Shell、重置弹窗仅有权限且local/Active/非本人可用。
- [ ] **Step2:** `node --test console/tests/password-state.test.mjs tests/account-credential/auth.test.mjs`，预期缺实现RED。
- [ ] **Step3:** 在App进入Shell之前强制渲染改密页，提供退出；新密码两次输入，错误不回显服务端请求内容。重置临时密码只内存、关窗清理、不写storage。上述空库E2E/运行验收首次登录显式使用helper，新审核者/只读测试用户也必须走HTTP改密；s.password/s.passwordFile/reauthenticate随当前测试账号同步，使用每用户映射。全仓登录调用清单标明空库、新用户、已有用户、SSO或未运行；旧fixture本就模拟已有账号时维持false并写明目的，禁止把新bootstrap标记全局关闭。LocalClient.login和原运行目录configure不增加自动改密行为；其余独立工具只有实际走新账号时才按清单适配并登记精确差异。
- [ ] **Step4:** 重跑Step2和`bash scripts/check-contracts.sh console`、`bash scripts/check-contracts.sh integration --filter 'FullyQualifiedName~Password|FullyQualifiedName~Session'`；PASS。实际浏览器/完整SDK E2E放入C5，尚未执行须明示。
- [ ] **Step5:** 记录登录调用清单、触及文件和未运行UI门禁；若清单发现还需修改其他夹具，先补精确文件/原因到任务审阅记录再实现，不扩展产品范围、不提交。
