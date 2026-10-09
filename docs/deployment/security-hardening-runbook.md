# 工作包 1：登录保护与 Header 信任边界运行手册

状态：源码实现和本机回归阶段。本文说明实际代码接口；独立固定版本安装、浏览器 QA 和 4192 升级须分别记录证据，不能由单元测试推断。

## 1. 生效范围

本地账号密码登录使用共享 Redis 两维固定窗口：来源 IP 默认 60 次/60 秒，账号默认 10 次/300 秒。成功与失败均占一次预算，成功不清零；两维原子准入，拒绝不扣另一维、不延长窗口。修改四项配额开启新预算，下一次本地登录读取。次数范围 1–10000，窗口范围 1–3600 秒。

SSO 回调、已有 Cookie 读取和 Gateway 业务流量保持原流程。本包不包含 CORS/IP/Transform、首次改密或 Credential 统计。

## 2. 部署依赖与秘密

Control Plane 配置：

| 键 | 值或要求 |
| --- | --- |
| Redis:Connection | 现有部署 Redis 连接；所有 Control Plane 实例共享 |
| Authentication:LoginProtection:RedisPrefix | 同一部署同一命名空间，不同部署分别隔离 |
| Authentication:LoginProtection:HmacSecretFile | 独立 32 字节秘密的 base64 文件路径 |
| HttpSecurity:TrustedProxyIps | 明确可信的直接代理 IP 列表；缺省为空 |
| HttpSecurity:TrustedProxyNetworks | 可选窄 CIDR，IPv4 至少 /24、IPv6 至少 /64 |

不复用 API Key、Cookie 密钥或观测 HMAC。文件禁止链接、组/其他用户权限和错误长度。长期容器以 10001:10001 读取 0600 文件，秘密目录 0700。启动验证秘密，Redis 不可用时登录返回 503；已有会话仍通过数据库校验。

首次独立初始化时按新的 Compose 能力生成秘密。已初始化的旧部署不会自动补建或轮换；升级准备须显式调用 `prepareLoginProtectionSecret(directory,state,{allowCreate:true})`。已有 receipt 后，丢失或哈希变化一律阻断，恢复原备份。

本机文件 `secrets/login-protection-hmac` 仅注入已有 `secrets-control-plane` 卷。receipt 只保存 owner、项目和原文件 SHA-256。原字节进入冷备 manifest；恢复到新 owner 时重绑 receipt 和 Redis 命名空间，保留原 HMAC、账号、密码和数据。

## 3. 来源 IP 与代理重建

三类 Host 默认不解析转发链。显式清单非空时仅解析 X-Forwarded-For，最多 4 跳，从直接连接方逐跳检查；遇到未知代理停止。保持 Host、Scheme、SSO 和 CSRF Origin 语义。解析后移除外部 Forwarded、X-Forwarded-*、X-Original-*，Console 的 YARP 再生成链。

生命周期先启动 Console，按 owner 标签和项目网络检查其当前容器 ID、网络 ID 和精确 IPv4，再写入私有 overlay，仅重建 Control Plane。Console 更换容器或 IP 后旧 receipt 失效，状态必须 Degraded/Unknown，重新登记后才恢复。不要登记整个 Docker 私网。

运行状态 `loginProtection` 分别检查秘密挂载哈希与当前代理登记；Legacy 表示旧发布不支持本包，Unknown 不等于 Ready。

## 4. 认证结果与排查

| 结果码 | 状态 | 行为 |
| --- | --- | --- |
| invalid_credentials | 401 | 统一账号错误，写匿名平台审计 |
| login_rate_limited | 429 | Retry-After 为正整数秒，no-store，不签发 Cookie、不验证密码 |
| login_protection_unavailable | 503 | Redis 无法在 1 秒内完成保护，不验证密码、不签发 Cookie |
| authentication_audit_unavailable | 503 | 认证审计无法提交，不签发 Cookie |

不要自动重发密码。查 TraceId 与固定运营码，检查 Redis、PostgreSQL、私有秘密和代理 receipt。日志与指标不得包含用户名、密码、Token 或原始 Header。

事件为 auth.login、auth.login.failed、auth.login.throttled、auth.logout。匿名事件不推断 UserId/组织/项目/环境，只对有平台审计权限的管理员可见。限速审计按 IP HMAC 的 60 秒桶至多一条；Redis 5 秒租约与数据库事务内幂等共同保护提交后崩溃场景。该审计不是逐请求明细，剩余拒绝计入不带身份标签的总量指标。

注销先清 Cookie；审计故障保持已注销语义，可能缺少该次注销审计。Redis 重启会重置短期预算，不改变账号或 Cookie 身份。

## 5. Gateway Header

三种认证模式统一剥离入站 X-WebApi-*，平台只注入服务端 TraceId 和实际执行 generation 的 deployment sequence。最终响应清理上游/缓存平台 Header，再注入本平台值。X-API-Key 不外传。

ApiKey/Anonymous 保留业务 Authorization；JWT 仅 ForwardBearer=true 才转发已经验证的 Bearer。诊断 Header 不构成签名身份，未就绪/健康响应不得冒充获取了 generation。Snapshot 2.0/2.1/2.2 格式不变。

## 6. 本包验证命令

在本包隔离仓库目录执行，Node 使用本机已配置的运行路径：

```sh
bash scripts/check-contracts.sh domain -p:RestoreLockedMode=true
bash scripts/check-contracts.sh integration -p:RestoreLockedMode=true
bash scripts/check-contracts.sh gateway -p:RestoreLockedMode=true
bash scripts/check-console.sh
bash scripts/check-local-runtime.sh unit
node scripts/security/acceptance.mjs --revision <本包完整40位固定提交>
```

契约测试入口构建 RuntimeTool，设置 WEBAPI_RUNTIME_TOOL_DLL，独立 tar 源码、随机 owner 的 PostgreSQL/Redis 测试项目。只读使用 NuGet 种子，不清空已有 Redis。

固定版本验收命令建立独立长期栈、第二 Control Plane、双 Gateway 和合成账号，执行并发配额、匿名审计、Redis 停止、Console 重建、冷备/恢复检查。失败保留 owner 资源诊断，不清理主部署。HTTP 阶段通过后仍 complete=false，保留私有 fixture 供 1440px 浏览器 QA 与独立审查。

浏览器 QA 必须覆盖安全配额编辑、预览、412 冲突、权限撤销、429/503 固定提示；截图不得出现密码、Cookie、API Key 或真实账号。私有 UI fixture 不能提交。审查 receipt 与 UI proof 均绑定同一个 sourceRevision，完成后执行：

```sh
node scripts/security/acceptance.mjs --finalize <本次owner专属fixture目录>
```

该命令仅在所有证据齐全时清理本次资源并写公开 verification。源码回归、不可变镜像安装、实际 UI、4192 安装状态和生产验收分别标记。

## 7. 4192 升级准备与回退

本包阶段不直接切换 4192。升级前确认实际版本、owner、原文件改动、账号/数据基线；保留现有全部秘密和 DP keys。经本包固定版本验收后准备新 HMAC、冷备、精确代理登记及挂载哈希检查，再按明确升级授权切换。

新版本读取旧四字段 security JSON 时补默认值，不自动回写。新保存写八字段；旧版严格解码器不能读八字段。因此不可直接回退旧镜像并沿用新行。采用经验证的兼容桥接版本；或在维护窗口恢复升级前冷备，明确升级后数据将随冷备恢复边界处理。禁止静默删配额字段冒充兼容。

真实企业入口代理、生产 TLS、HA/SLA 和外部基础设施验收另行执行。本包本机证据不代表生产验收。
