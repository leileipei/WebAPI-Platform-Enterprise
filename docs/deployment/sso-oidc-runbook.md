# 企业单点登录 OIDC 操作与部署说明

本批基于 `f4e2cc3d81f0467fd48894127b26bc1ec6138389` 的隔离工作树开发。实际新增源码尚未提交，构建识别以源码清单 SHA256 为准。验收结论见 `docs/evidence/sso/`；原 4192 长期环境尚未应用 SSO。

## 部署配置

配置 `Sso:PublicBaseUrl` 为用户访问的可信 HTTPS 控制台 Origin，登记 `Sso:AllowedOrigins` 的精确身份服务 Origins（含 Discovery、授权、Token、JWKS 端点）。明确列出必要的私网 CIDR；未经允许的私网、链路本地和元数据地址会被拒绝。回调必须是控制台中的完整 `/auth/oidc/callback/{ProviderID}`，不能使用任意 Host 或通配符。

通过 `Sso:SecretFiles` 将别名 `enterprise` 映射到受控部署文件，页面填写 `file://sso/enterprise`。文件仅含客户端秘密，权限 0600、只读挂载，路径及任何父目录均不能为符号链接。秘密由管理员在受控主机上写入，不通过聊天、页面或数据库提交。示例：

```json
{
  "Sso": {
    "PublicBaseUrl": "https://console.example.com",
    "AllowedOrigins": ["https://identity.example.com"],
    "AllowedPrivateCidrs": [],
    "SecretFiles": {"enterprise": "/run/sso/enterprise"},
    "AllowedSigningAlgorithms": ["RS256", "PS256", "ES256"],
    "MaxCachedSchemes": 128,
    "FixtureEnabled": false
  },
  "DataProtection": {
    "KeysDirectory": "/var/lib/webapi/keyring",
    "ApplicationName": "webapi-enterprise"
  }
}
```

生产必须使用 HTTPS。平台 Cookie 为 HttpOnly、Strict、Secure；回调专用临时 Cookie 为 HttpOnly、Lax、Secure 且限定路径。多控制实例须共享平台数据库及受控 DataProtection KeyRing 和 ApplicationName。控制台代理只转发管理 API 与 OIDC 固定回调路径。

独立本机验收例外仅在 `Development` 且 `FixtureEnabled=true` 下允许 loopback HTTP。`FixtureConnectOverridesJson` 是精确 Origin 到 Docker fixture 主机的 JSON 映射，用于保持 localhost Issuer 与浏览器来源一致；生产忽略此适配。正式部署不得沿用这些开发例外。

## 管理与账号开通

1. 以真实系统 PlatformAdmin 登录，进入“审计与设置 → SSO 身份源”。管理需 `system.sso.manage` 平台权限。
2. 新增身份源，填写名称、Issuer、Client ID、受控 SecretRef、Scopes 与可选 `name`/`email` 字符串映射。保存后登记精确回调。
3. 执行连接测试，核对引用、Discovery、JWKS、授权码、S256 与 client_secret_post 声明。当前修订通过且在15分钟内才可启用；结构通过不等于客户端认证成功。
4. 在用户管理预创建 SSO 用户，填写身份服务稳定 Subject。创建和绑定修正同时要求 `user.manage`、`system.sso.manage`。Subject 区分大小写，不以邮箱匹配。
5. 按原平台流程分配角色和组织、项目、环境 Scope。无自动开户、邮箱合并、Claim 角色或隐式范围授予；SSO 用户无本地密码。本地账号不能转换或绑定为 SSO。
6. 组织身份源登录要求用户拥有该 Active 组织内有效范围；平台级来源可认证无 Scope 用户，但不会额外授予业务资源访问。
7. 使用企业登录入口完成实际用户登录验收。默认身份源仅标识，不自动跳转。本地管理员入口持续保留。

## 修订、轮换与恢复

名称或默认标识变更保留已有会话，但待完成登录须按最新治理修订重试。认证配置、停用、用户/绑定停用与秘密轮换会在后续请求撤销会话；重新启用不能复活旧票据。修改已有绑定的 Issuer、Client ID 或组织范围需新建身份源。

秘密轮换：先更新身份服务客户端与部署秘密文件，再在控制台明确执行“秘密轮换后更新认证修订”，重新连接测试并验证新登录。仅替换文件不会自动更新认证修订；启用与明确认证变更将秘密摘要通过 DataProtection 按 ProviderID 和 auth_revision 保护后持久绑定。每次准备登录均核对绑定，包括已有缓存、缓存淘汰后重建和新控制实例；不匹配时拒绝登录，须明确轮换后才能使用新文件。数据库不存秘密或可直接枚举的裸摘要，DTO、审计和日志不返回受保护摘要。密钥环与应用标识必须共享并随冷备份保留；无法解密时 SSO 拒绝而本地登录入口仍可用。双控制实例使用相同部署映射。部署文件替换到完成认证修订更新之间，新登录会暂时拒绝，已有平台权限流程保持服务端治理。

用户修正绑定前先停用用户；保存后重新核对角色、范围再启用。删除最后一个可用本地系统平台管理员的路径会被服务端阻止。发生身份源故障时使用本地管理员处理。已有登录会话先退出再选择其他身份源。

平台退出只结束本平台会话，身份服务会话可能仍有效；勾选“在身份服务重新验证账号”使用受控 `prompt=login`。本批没有 IdP 全局注销、UserInfo、刷新 Token 或离线登录。

## 隔离验收与清理

`scripts/check-sso.sh domain|integration|gateway|console` 运行相关回归。`e2e` 创建自身 owner 的 Postgres、Keycloak、控制实例、测试故障代理及控制台，完成检查后清理。`browser` 保留同一隔离环境供浏览器检查；私有 `.runtime/sso-review.json` 指向该 owner 上下文，不能复制入交付包。

验收使用平台 `127.0.0.1` 与身份服务 `localhost`，生成随机合成密码和客户端秘密；Keycloak 镜像按 `deploy/sso-images.lock.json` 摘要固定。故障代理仅在 fixture 中阻塞兑换，验证领取后中断与重放拒绝。清理调用 `cleanupSsoFixture`，先核对目录、project、owner、容器和卷标签，再删除本 owner 资源。不得使用通配清理、`docker system prune` 或操作 4192/4196/4197。

合成 IdP 和本机 HTTP 通过不代表企业 IdP、生产 TLS/PKI、真实网络、生产运维及灾备验收完成。
