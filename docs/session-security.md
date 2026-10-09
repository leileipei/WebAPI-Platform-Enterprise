# 真实会话与初始化

会话采用ASP.NET Core Cookie，HttpOnly、SameSite Strict、固定8小时；生产Secure，仅Development支持本机HTTP。所有/api/v1写操作（包括登录）验证X-CSRF-Token及Origin。CSRF令牌在登录后必须重新获取，因为身份已变化。会话Cookie不含权限缓存，数据库用户状态及SecurityStamp在每个请求验证；写权限由服务端按实际资源查询。

迁移器默认只迁移，不产生账号。显式--bootstrap配合WEBAPI_BOOTSTRAP_USERNAME、WEBAPI_BOOTSTRAP_PASSWORD_FILE创建首管理员；密码文件必须仅所有者可访问且至少16字符。已有账号不会被再次初始化覆盖密码。不输出密码或Cookie。首管理员的角色提供首组织及治理管理能力；业务资源写入仍须实际Scope。

通过真实HTTP验证登录、错误密码、CSRF、账号停用即时失效和登录审计脱敏。企业SSO不在首期内。开发环境的DataProtection密钥持久化将在部署任务配置；生产必须保留密钥并通过HTTPS访问。

## 工作包 1：登录保护与来源边界（2026-10-09）

新增本地登录 IP/账号共享 Redis 固定窗口；四项配额由平台安全设置管理，旧四字段 JSON 仅在读取时补默认值。成功登录仍消耗预算，429/503 不验证密码或签发新会话。失败审计为匿名平台范围，限速审计按来源 IP HMAC 的 60 秒桶聚合，数据库幂等防止提交后确认中断重复写入。注销先清 Cookie，审计故障不阻止退出。

三个 Host 使用显式可信代理清单；空清单不会启用转发解析。Gateway 在三种认证模式统一移除 X-WebApi-*，响应最终注入本平台诊断值，保留非 JWT 的业务 Authorization。部署、恢复、八字段配置回退限制及验收步骤见 `docs/deployment/security-hardening-runbook.md`。源码实现不等于 4192 已升级。
