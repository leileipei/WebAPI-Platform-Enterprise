# 第39页 SSO/OIDC：现状与阶段选择

2026-10-05，基于固定源码 `f4e2cc3d81f0467fd48894127b26bc1ec6138389` 和原《WebAPI Platform Enterprise V2 产品原型与数据架构设计》。这是现状核对与接入方式比较，尚不是批准的实施设计或计划。本轮未改产品源码、提交、部署、设置或账号。

## 原设计要求

第39页 `/settings/sso` 包含 Provider 列表、OIDC 配置、Claim Mapping、连接测试，以及新增、编辑、测试、启停和默认身份源操作。保留名称、Issuer、ClientId、SecretRef、Scopes、Claim Mapping，权限 `system.sso.manage`。Secret 不回显，至少保留一种管理员登录方式。原 `sso_providers` 含可空 `organization_id`、`provider_type`、`issuer`、`client_id`、`secret_ref`、`claim_mapping`、`enabled`。

原文档和原型中的演示 Issuer 不视为真实企业部署参数。

## 代码事实与差距

| 范围 | 当前事实 | 需要解决 |
|---|---|---|
| 身份源 | 当前实体、迁移没有 SsoProvider；prototype 有演示列表 | 持久配置、并发修订、启停、默认源、测试事实 |
| 用户 | 已有 AuthSource、可空 PasswordHash、SecurityStamp | 外部身份的唯一绑定；不按邮箱自动合并 |
| 登录 | 真实 Login 仅支持本地；密码认证拒绝非 local 来源 | 身份源入口、授权跳转、回调验证、会话建立 |
| 权限 | 已有 system.sso.manage；平台资源要求真实系统 PlatformAdmin | 复用服务端授权和治理锁，不以外部 claim 自动授予平台权限 |
| 会话 | Cookie/CSRF 为 Strict；所有 /api/v1 写入验证 Origin 和防伪令牌 | 专门处理跨站认证回调，不能关闭全局防伪保护 |
| SecretRef | 第40页只保存意向，未解析秘密 | 明确解析来源和部署绑定，不开放任意路径/URL读取 |
| 角色与 Scope | 由 PG 管理，逐请求复核状态及 SecurityStamp | 企业登录仍遵循原授权，未知用户不能自动取得业务资源 |
| 能力说明 | SSO 标为后续；部分旧说明仍写4192未升级 | 新阶段更新能力说明，原封存交付包保留历史 |

核对文件：`UserRecord.cs`、`AccountService.cs`、`PermissionCatalog.cs`、`AuthorizationService.cs`、`ControlPlaneApp.cs`、`SessionEndpoints.cs`、`console/src/pages/Login.tsx`。

## 接入路径选择

**A（推荐）：通用 OIDC＋独立本机身份源验收。** 完成 Provider 治理及真实授权码登录，以独立合成身份源和测试账号验证；保留原本地 admin 和 PG 角色/Scope。无需现在提供企业秘密。验收后另行审阅4192切换，不提前启用正式企业SSO。

**B：直接对接既有 Entra ID / Keycloak。** 先提供身份源类型和 Issuer，再确定客户端注册、回调地址、测试账号和 SecretRef 部署绑定。不在聊天中发送 ClientSecret；实际企业验收依赖外部身份源条件。

**C：先完成真实配置治理页。** 验证持久配置、权限、修订和审计；真实跳转、Token 验证、连接测试及企业登录留待后续，不能显示模拟成功或标为企业SSO可用。

用户已选择A：通用OIDC＋独立本机身份源验收，并已确认 [第39页SSO/OIDC实施设计](../superpowers/specs/2026-10-05-sso-oidc-design.md)。当前等待 [实施计划](../superpowers/plans/2026-10-05-sso-oidc-implementation.md) 审阅及执行方式选择；没有把系统设置阶段的授权扩展为新提交、4192切换或自动授予角色。

## 待设计确认的边界

1. 配置治理、结构校验、发现端点测试和完整登录分别给证据。
2. 保留可用的本地平台管理员，原 admin 密码和会话策略保持现有行为。
3. 外部身份按稳定标识与 Provider 绑定。Claim Mapping 建议先覆盖显示资料，角色/Scope 仍由平台明确分配。
4. 验证停用用户/Provider、未知身份、回调重放、Issuer/Audience不匹配、无授权及修订冲突。
5. 隔离测试与正式部署分开；原4192业务数据保持当前验收版。

## 技术依据

现有 Strict Cookie 与全局同源防伪检查需要专项回调设计。Microsoft 说明 OIDC 默认 POST 回调会触发 SameSite 限制，并为 Correlation/Nonce Cookie 定义独立策略：[ASP.NET Core SameSite](https://learn.microsoft.com/en-us/aspnet/core/security/samesite?view=aspnetcore-10.0)。

后续设计以标准验证和授权码防护为依据：[OpenID Connect Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html#IDTokenValidation)、[RFC 9700](https://www.rfc-editor.org/rfc/rfc9700.html)。这些只是已核对的设计依据，尚未形成或运行产品实现。
