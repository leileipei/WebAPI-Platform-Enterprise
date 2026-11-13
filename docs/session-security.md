# 会话、首次改密与初始化

本页按[当前状态页](deployment/current-project-status.md)列出的源码解释会话和初始化行为；实际部署的 Cookie、可信代理和密钥目录仍须现场核对。

## Cookie 与会话期限

平台使用 ASP.NET Core Cookie，`HttpOnly`、`SameSite=Strict`；生产环境强制 Secure，Development 按请求协议处理。本地普通登录及 SSO 最终签发平台会话，不在 Cookie 中缓存业务权限。

普通会话期限来自 `system.security.sessionTtlMinutes`：默认 480 分钟，当前设置校验范围为 5–1440 分钟，签发时写入明确过期时间。Cookie 选项中的 8 小时是默认选项，不能据此写成所有会话固定 8 小时。修改设置影响新签发，不追溯改写已有票据；未启用滑动续期。

需要首次改密的本地账号仅取得受限会话，期限不超过系统设置与 10 分钟中的较小值，业务接口被限制。完成改密后更新安全标记并重新签发普通会话；SSO 账号的密码由身份源管理。

依据：[会话端点](../src/WebApi.ControlPlane/Security/SessionEndpoints.cs)、[PlatformSessionIssuer](../src/WebApi.ControlPlane/Security/PlatformSessionIssuer.cs)、[设置默认值](../src/WebApi.Infrastructure/Settings/SettingsValues.cs)、[设置校验](../src/WebApi.Infrastructure/Settings/SystemSettingsValidator.cs)。

## 请求与会话验证

`/api/v1` 下非 GET/HEAD/OPTIONS 请求（含登录）验证防伪令牌；通常通过 `X-CSRF-Token`，SSO 启动表单可用标准防伪字段。携带 Origin 时还要求与实际请求同源；缺少 Origin 不免除防伪校验。身份改变后应重新取得防伪令牌。

每次平台会话验证都会查询数据库中的有效账号与 `SecurityStamp`；SSO 会话还检查 Provider、认证修订和绑定状态。具体业务权限始终在服务端按实际资源判断。依据：[ControlPlaneApp](../src/WebApi.ControlPlane/ControlPlaneApp.cs)、[SSO 会话验证](../src/WebApi.Infrastructure/Sso/SsoSessionValidator.cs)。

## 显式初始化与密钥保留

迁移器默认不创建账号。首管理员通过显式 `--bootstrap`、用户名及受限密码文件建立；已有同名账号不会因再次初始化被覆盖密码。新建管理员需首次改密，业务写入仍需实际角色和 Scope。依据：[BootstrapAccounts](../src/WebApi.Migrator/BootstrapAccounts.cs)。

Data Protection 持久化已有实现，配置 `DataProtection:KeysDirectory` 与 `DataProtection:ApplicationName` 时必须同时有效，目录使用绝对路径；不能再写成“将在部署任务实现”。没有配置不代表密钥已经持久化，实际安装必须保留其受控密钥及 HTTPS 配置。依据：[PersistentProtectionConfiguration](../src/WebApi.Infrastructure/Security/PersistentProtectionConfiguration.cs)。

## 登录保护与来源边界

本地登录使用 IP/账号共享 Redis 固定窗口配额。成功登录也消耗预算；拒绝或依赖故障不签发新会话。匿名登录审计不保存密码，注销先清会话，审计失败不阻止退出。

可信代理边界和 Header 处理按[登录保护手册](deployment/security-hardening-runbook.md)配置。源码存在、固定版本测试及历史安装记录分别解释，不据此声称某个持久服务已升级或当前健康。
