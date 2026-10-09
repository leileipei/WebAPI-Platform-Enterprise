# 工作包 1：登录限速、认证审计与内部 Header 信任边界设计

日期：2026-10-08。状态：用户已确认设计；进入实施计划编制，未实施、未部署、未取得本包安全回归证据。

源码核对基线：`a11d41250c76eb42831797e9fabd5405906dc256`。本设计不包含工作区已有文档、截图和交付证据的未提交改动。

## 1. 目标与约束

用户已确定收尾顺序：安全加固 → 查询与 Gateway 索引优化 → CORS/IP/Transform 策略 → 原页面功能收尾 → CI/签名/发布与迁移门禁。本设计只处理第一包，不把后续能力提前计入完成。

目标是限制本地密码登录的暴力尝试，补齐认证事件审计，在所有业务认证模式下隔离客户端提供的内部 Header。保留账号、密码、权限、组织/项目/环境、SSO、既有 Cookie/CSRF、发布及历史 Snapshot 行为。密码变更和 Credential 使用统计属于第四包。

不新增验证码、MFA、永久账号锁定或第三方登录提供方。不得修改用户现有密码，不把运行秘密、备份或真实凭证写入文档、截图、Git 或测试输出。第一包不实现新的 CORS/IP/Transform 策略。

## 2. 已核实的现状

| 证据文件 | 当前行为 | 本包需补齐的部分 |
| --- | --- | --- |
| `src/WebApi.ControlPlane/Security/SessionEndpoints.cs` | 本地登录成功写入 `auth.login`；写操作保留 Origin 和 CSRF 校验 | 失败、限速事件及注销审计；接入登录限速 |
| `src/WebApi.Infrastructure/Security/AccountService.cs` | 错误密码、不存在、停用和非本地账号统一返回 `invalid_credentials` | 限速不得破坏统一错误响应；失败事件不泄露用户是否存在 |
| `src/WebApi.ConsoleHost/ConsoleHostApp.cs` | YARP 代理管理 API，并保留 OriginalHost | 明确代理来源及转发 Header 的信任范围 |
| `src/WebApi.Gateway/Security/ApiKeyMiddleware.cs` | 总是移除 `X-API-Key`；只有 JWT 模式批量移除 `X-WebApi-*` | 三种模式一致剥离客户端内部 Header |
| `tests/WebApi.Gateway.Tests/JwtAuthorizationPipelineTests.cs` | 有 JWT Header 清理测试；明确要求非 JWT 保留业务 Authorization | 增加 ApiKey/Anonymous 伪造 Header、缓存和重试分支验证 |
| `src/WebApi.Infrastructure/Governance/AuditAccessQuery.cs` | 平台审计及组织/项目/环境数据范围过滤 | 匿名认证事件保持平台范围，不能根据未验证用户名伪造归属 |

源码核对不是安全测试通过证据。

## 3. 方案选择

采用“Redis 共享限速 + 现有 AuditLog 持久审计 + 显式可信代理配置 + Gateway 入口统一清理”的组合。复用已有 PostgreSQL/Redis 和项目框架依赖，不新增限速基础设施。

备选方案：仅进程内限速改动较小，但多实例和重启会重置预算；数据库限速可持久保存预算，但会把攻击请求的计数和清理压力转移到业务库。本包选择 Redis，数据库只保存必要审计事件。

限速存储异常时，本地新登录返回 503，不退回不受保护的密码验证。已建立会话、业务网关请求和 SSO 回调不受这项存储异常直接影响。

## 4. 本地登录限速

### 4.1 规则和配置

对 `POST /api/v1/auth/login` 使用两维共享预算：可信来源 IP、提交的账号标识。账号预算跨 IP 共享；IP 预算跨账号共享。计数的是通过基础请求校验后进入认证的尝试，不是只计错误密码。

建议默认值：每来源 IP 60 次/60 秒；每账号 10 次/300 秒。SecuritySettings 新增 `loginIpMaxAttempts=60`、`loginIpWindowSeconds=60`、`loginAccountMaxAttempts=10`、`loginAccountWindowSeconds=300`；次数范围 1–10000、窗口范围 1–3600 秒。数字是本项目建议的初始值，不是 OWASP 规定的阈值。上线前按实际共享出口规模校准。

成功登录也占用本次预算，不重置账号或 IP 预算。没有永久锁定用户状态的操作。固定窗口从首次获得预算开始，达到阈值后等待剩余窗口；窗口边界可能允许短时双倍流量，需在手册中说明，不能标为严格滑动窗口。

两维预算使用同一次 Redis 原子操作：全部可用才同时消耗；某维耗尽时不消耗另一维，也不延长耗尽键的有效期。Redis 时间决定窗口剩余时间，避免实例时钟差异产生不一致。响应为 429、`login_rate_limited`、整数秒 `Retry-After`、`Cache-Control: no-store`；响应不暴露哪一维耗尽。

### 4.2 标识与故障处理

IP 统一处理 IPv4 和 IPv4-mapped IPv6。账号标识遵守当前精确匹配语义，不擅自改为大小写不敏感或修改用户记录；不同输入大小写不会绕过真实账号的认证规则。无效/超长字段在基础校验中拒绝，不为任意超长内容创建 Redis 键。

Redis 键只包含用途域、部署命名空间和 HMAC 标识，不包含原始用户名、IP 或密码。使用专用 32 字节秘密文件，启动时验证权限与长度；不复用业务 API Key、Cookie 密钥或观测 IP HMAC 秘密。限速 Lua 脚本支持 Redis Cluster：同一部署的两维键使用相同 hash tag，明确该部署计数集中于一个 slot。

Redis 操作超时预算为 1000ms；超时、连接错误、脚本结果非法时返回 503 `login_protection_unavailable`，不执行密码校验或发 Cookie。客户端取消则正常取消，不转成新认证成功。

限速键有有限 TTL，不依赖扫描清理。配置仅使用四个配额字段的规范化指纹构建预算命名空间，调整配额会开启新预算；调整密码长度等其他设置不会重置预算。该效果在安全设置预览中明确展示。

### 4.3 配置兼容

扩展现有 SecuritySettings、服务端校验、预览和安全设置表单。读取历史四字段 security JSON 时补充上述四个默认值；历史行不通过启动自动覆写。新保存要求完整字段且拒绝重复/未知字段，沿用 revision、CSRF、幂等与审计。四个配额字段的生效类型为 `LoginRequest`，在下一次本地登录读取，不能标为 Snapshot 发布生效。升级后的新格式不兼容旧版严格解码器，回退必须使用兼容桥接版本或明确恢复方案，不能直接切旧镜像并保留新格式冒充可回滚。

## 5. 来源 IP 与代理 Header

限速和审计只使用服务端解析后的 `RemoteIpAddress`，不能直接取客户端 `X-Forwarded-For` 首项。

ConsoleHost 与 Control Plane 的可信代理各自配置。默认清空框架隐式可信代理，只信任显式登记的 IP/CIDR；空可信清单时不启用 Forwarded Headers 中间件，避免框架将空集合解释为接受任意来源。不得把所有来源、全部私网或 `0.0.0.0/0` 当作默认可信代理。第一包只处理转发 IP，保留现有 OriginalHost、Scheme、CSRF 与 SSO 回调构造语义；生产 HTTPS 代理设置另做真实拓扑验收。

ConsoleHost 解析可信链后，删除外部输入的 `Forwarded`、`X-Forwarded-*` 和 `X-Original-*`，由代理重新产生来源信息。Control Plane 仅在直接连接方属于明确可信 ConsoleHost/入口代理时消费转发 IP；不可信连接的转发值不能影响登录预算或审计来源。多跳解析从直接连接方向外逐跳验证，遇到不可信或非法链停止，设置有限跳数，禁止无界解析。

本机运行工具从自身 owner/project 所属的运行中 Console 容器检查实际网络 IP，再生成 Control Plane 的精确可信代理配置。不能按服务名盲目信任任意容器或采用网络整段信任。Console 重建后必须重新核对并更新配置；若地址发生变化但未登记，退回直接连接方的 IP，不能接受未验证转发值。`up/start/restart/configure` 的就绪检查需报告代理登记是否一致。

生产部署由运维提供真实入口代理清单。缺少清单时仍运行账号限速，来源 IP 使用直接连接方，状态明确提示可能聚合多个用户；不得声称已实现按终端用户 IP 的准确配额。容器 NAT 前的终端 IP 无法仅靠应用恢复，需真实入口配置。

## 6. 认证审计

沿用 AuditLog，不为不可信账号输入查询并填充组织或用户归属。认证事件范围：

| Action | 记录时机 | 用户与范围 |
| --- | --- | --- |
| `auth.login` | 密码及账号状态再次确认后，沿用现有成功事务 | 已验证 UserId；平台范围 |
| `auth.login.failed` | 本地认证统一失败 | UserId 和组织/项目/环境均为空 |
| `auth.login.throttled` | 限速拒绝 | 匿名平台范围；记录安全聚合摘要 |
| `auth.logout` | 已认证账号主动注销 | 已验证 UserId；平台范围 |

安全摘要允许：固定结果码、服务端 TraceId、来源 IP、HMAC 账号标识。禁止密码、Cookie、API Key、Bearer、原始请求体、未经限制的 User-Agent/用户名/请求 Header。BeforeJson 为空；AfterJson 通过固定字段 DTO 序列化，禁止把异常或请求对象直接写入日志。

失败认证完成后才追加失败审计，不能把审计写入将被回滚的认证事务。审计存储故障时返回 503，不以 401 或 200 冒充已审计结果；服务日志仅记录固定事件码和 TraceId。成功登录保持“审计事务提交成功后才签发会话”。

限速拒绝审计按“来源 IP HMAC + 60 秒桶”聚合，每桶至多一条，并记录首个拒绝时间；其余拒绝计入无用户标签的总量指标，不能把聚合记录描述成每次拒绝的完整明细。聚合标记仅在审计提交后确认，使用短期租约避免多实例重复洪泛。持续压测需要证明数据库写入上限随攻击请求量有界。

注销优先清除会话 Cookie；审计异常不能阻止用户退出，也不能把旧 Cookie 保留为活跃会话。记录脱敏的审计失败信号，文档说明注销审计在存储故障时可能缺失。SSO 原有成功/失败审计和一次性 attempt 规则不重写。

审计查询和 CSV 导出继续通过 AuditAccessQuery。平台匿名事件仅平台管理员具备相应审计权限时可见，组织级审核员不能因此获得其他组织或全平台登录事件。

## 7. Gateway 内部 Header

在三种认证模式的共同入口，对所有大小写形式的 `X-WebApi-*` 请求 Header 统一删除，不只覆盖 JWT。删除后仅由 Gateway 注入现有 `X-WebApi-Trace-Id`、`X-WebApi-Deployment-Sequence`；其值来自本次固定 generation 和服务端请求上下文。

继续移除业务调用输入的 `X-API-Key`，认证前保存待验证值，仅在请求内存中使用。不得在错误响应、转发请求或审计中重新暴露平台凭证。

非 JWT 路由继续保留既有业务 `Authorization`，这是当前兼容性测试明确要求的行为。JWT 路由只有 `ForwardBearer=true` 时转发已验证的 Bearer，否则删除 Authorization。第一包不通过 Header 清理改变外部服务认证方式。

上游响应中的 `X-WebApi-*` 也不能被调用方误认为平台生成：统一剥离上游同名前缀，在响应最终阶段生成上述两个平台诊断 Header。覆盖普通代理、认证拒绝、限流/熔断拒绝、缓存命中和重试结果；没有取得 generation 的健康端点和未就绪响应不伪造发布序列。

Gateway 对外部转发 Header 采用同样的明确代理信任原则，转发到上游的链必须基于已验证来源重新产生。本包不新增 X-WebApi-Application-Id/Subject 等身份 Header，不将现有 Trace/Sequence 当作可跨网络验证的身份凭证。

## 8. 组件和改动边界

- Domain/Contracts：登录保护配额、决策结果、固定认证审计摘要；兼容 security 设置读取。
- Infrastructure：Redis 原子预算、HMAC 标识及秘密加载、认证审计写入和拒绝聚合。
- ControlPlane：SessionEndpoints 接入；代理解析位于限速与审计之前；保持认证、CSRF、权限及会话失效检查。
- ConsoleHost/Gateway：共享明确代理配置语义、外部 Header 清理；Gateway 平台 Header 的请求/响应来源唯一。
- Console：现有安全设置增加配额字段、效果提示；登录显示安全错误和重试信息，不展示预算维度。
- Runtime：新增专用秘密的创建、挂载、权限、冷备恢复和既有部署升级准备；精确 Console 代理登记与就绪核对。

新增秘密只在本包安装预检或隔离测试环境显式准备，不在缺失已有部署秘密时自动轮换其他秘密。Redis 前缀隔离，不清空现有业务限流、缓存或会话数据。本包预计无需业务表结构变更；若实施时发现需要新增持久表，必须更新本设计并明确迁移兼容性。

## 9. 安全回归与交付证据

| 测试组 | 必须覆盖 |
| --- | --- |
| 登录配额 | 两维各自耗尽；轮换 IP/账号；两实例共享；并发精确预算；Redis TIME/TTL；窗口恢复；正 Retry-After；无 Cookie；非法字段有界；秘密与原始标识不出现在键/日志 |
| 登录故障 | Redis 中断、超时、非法结果；认证不被执行；既有会话仍可读；审计写入失败；取消请求；成功登录期间账号停用竞争 |
| IP 信任 | 直接伪造 XFF；可信 Console 单跳；多跳边界；重复/非法链；IPv4-mapped IPv6；未知代理；Console 重建后地址漂移；容器 NAT 的证据限制 |
| 审计 | 成功、错误密码、不存在、停用、SSO-only 本地尝试、注销；无秘密；平台和组织可见范围；CSV 脱敏；多实例拒绝聚合及写入有界 |
| Gateway Header | ApiKey/JWT/Anonymous；大小写、未知前缀、多值；上游响应伪造；缓存命中与重试；非 JWT Authorization 保留；JWT ForwardBearer 两种设置；历史快照、回滚及重启 |
| 配置与部署 | 历史 security JSON 读取；新保存与冲突；阈值边界；秘密恢复；缺失/错误权限阻断；独立测试项目升级；当前账号与原业务数据保全 |

先在隔离测试运行环境验证，再准备当前 4192 部署的预检、冷备和升级方案。真实登录测试不得使用猜测密码或向当前 admin 连续发送失败请求；不得用测试限速配置锁住现有管理员。当前 4192 部署要保持配置和数据，取得固定源码、测试及安装证据后再标记部署完成。

完成标志：以上定向用例、既有 Session/PersistentSession/SSO/SystemSettings 回归及 Gateway 定向兼容性回归通过；真实双实例登录计数、双网关 Header 转发和历史回滚证据齐全。代码通过、已合并和已部署分别记录，不以单元测试替代真实部署或企业生产验收。

## 10. 自检与参考

已按本轮源码核对现有登录、Cookie、CSRF、审计范围、非 JWT Authorization 和代理模式，避免新增规则与原业务冲突。尚未运行本包测试，不给出通过数量。内部 Header 清理的源码差异和登录失败审计缺口已确认；容器真实来源 IP 必须按实际拓扑验证。

设计参考：

- [OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)：登录节流、统一认证错误和监控。
- [OWASP Logging Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html)：认证事件及敏感数据排除。
- [ASP.NET Core 10 代理与负载均衡配置](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)：显式可信代理及转发 Header 处理。

以上是设计依据，不代表本包已满足标准认证或完成生产安全验收。
