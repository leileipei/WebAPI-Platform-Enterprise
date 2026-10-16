# 第3包：CORS / IP Filter / Transform 实施前核对与设计提案

日期：2026-10-13。状态：源码核对完成，设计提案待审阅，产品实现尚未开始。

源码基准：`293b37f2dbd59427cd2d40f6783dc441345528ef`。本文中的“现有”指此版本源码；“建议”是待批准的设计，不能当作已交付功能或运行验收证据。

## 1. 本轮目标与边界

用户要求“2包先暂停，完成3包”。第2包已按用户要求暂停，保留133对 / 266个完整配对窗口及原始、私有诊断；原144对 / 288窗口矩阵没有完成，不能验收为通过。专用负载已停止，数据库卷与网络保留，4192现有服务保持运行。

第3包的完成标志沿用用户清单：三类策略的编辑、校验、绑定、审批发布、双网关执行、观测、历史快照兼容与回滚全部通过。原权限、组织 / 项目 / 环境边界、业务审批流程和现有数据继续保留。

已有“API新建向导整合”建议此前被用户要求先不处理，不纳入本包。第2包性能矩阵也不纳入本包验收。

当前尚待明确的业务范围只有一项：Transform是否包含JSON请求体 / 响应体字段映射。原产品文档列出了Transform类型，没有规定正文映射字段和语义。已向用户提出A/B选择；本文以A作为推荐提案，不视为已选定。

## 2. 源码核对结论

| 环节 | 已有实现 | 本包实际缺口 |
| --- | --- | --- |
| 策略数据与接口 | 通用Policy实体、JSON配置、修订、校验、复制、引用查询 | 校验器仅支持authentication、timeout、rate_limit、circuit_breaker、retry、cache |
| 路由绑定 | 同一路由每类型一个策略，最多6个绑定；作用域和并发控制 | 新增三类解析和组合规则，绑定上限需覆盖9类 |
| 冻结发布 | 候选冻结、源修订、运行策略内容身份、审批、双节点能力检查、ACK、历史回滚 | 编译器与校验器仅接受2.0 / 2.1 / 2.2；没有新三类运行语义 |
| 请求身份 | 内部Header清理、可信代理边界、ApiKey / JWT / Anonymous、原API授权 | 当前认证中间件持有运行代际租约；IP与预检提前执行需复用同一租约 |
| 网关路由 | YARP按原路径、方法、优先级匹配 | GET-only路由不匹配OPTIONS预检；没有业务策略Transform配置 |
| 缓存与重试 | 已验证身份分区、部署序列分区、缓存命中短路、实际YARP转发器重试 | 新三类必须覆盖缓存命中与每次物理重试，不能仅覆盖普通代理分支 |
| 可观测性 | 5类受控决策，日志 / Trace / 指标查询和投影 | 新三类决策、过滤及容量未实现 |
| 跨环境制品 | 可拆分冻结字段与环境参数，再按目标作用域建立私有策略 | ArtifactPolicyTemplates的类型分支不接受新三类 |
| 控制台 | 6类表单、绑定、发布Review、修订差异 | 缺少新三类结构化表单及Review语义 |

主要源码入口：

- `src/WebApi.Contracts/Policies/PolicyContracts.cs`、`AdvancedPolicyContracts.cs`、`PolicyObservationContracts.cs`
- `src/WebApi.Domain/Policies/PolicyConfigurationValidator.cs`、`PolicyBindingRules.cs`、`RuntimePolicyIdentity.cs`
- `src/WebApi.Domain/Runtime/SnapshotValidator.cs`
- `src/WebApi.Infrastructure/Releases/SnapshotCompiler.cs`、`ReleaseCandidateBuilder.cs`、`ReleaseService.cs`、`PublishCoordinator.cs`、`RollbackService.cs`
- `src/WebApi.Infrastructure/Gateway/SnapshotSchemaCapabilities.cs`
- `src/WebApi.Domain/Delivery/ArtifactPolicyTemplate.cs`
- `src/WebApi.Gateway/Configuration/RuntimeGenerationStore.cs`、`EnterpriseProxyConfigProvider.cs`
- `src/WebApi.Gateway/Security/ApiKeyMiddleware.cs`、`GatewayPlatformHeadersMiddleware.cs`
- `src/WebApi.HttpSecurity/TrustedProxyExtensions.cs`
- `src/WebApi.Gateway/Policies/TrafficExecutionContext.cs`、`CacheKeyBuilder.cs`、`CacheEligibility.cs`、`CacheResponseMiddleware.cs`、`BoundedResponseCapture.cs`
- `src/WebApi.Gateway/Forwarding/GatewayForwardingAdapter.cs`
- `console/src/policies/`、`console/src/pages/Policies.tsx`、`console/src/pages/PolicyEditor.tsx`

## 3. 接入方式比较

| 方式 | 取舍 |
| --- | --- |
| A：复用业务策略与发布引擎，增加快照2.3能力门禁（推荐） | 保持现有治理模型；新三类配置随冻结版本发布和回滚；旧网关发布前明确阻断；需更新快照校验和运行链 |
| B：保持2.2，另加策略能力声明 | 表面减少协议编号变化，但要额外维护协议与策略能力组合；旧2.2声明不能代表支持新策略，门禁容易遗漏 |
| C：部署配置直接启用网关功能 | 修改范围较小，但策略变更脱离现有修订、审批、冻结和历史回滚，不满足用户本包完成标志 |

推荐A。Policy表的type为通用字符串，config为JSON，不因增加三类而预设新数据库迁移；实施时仍须核对EF模型和迁移差异，不自行移除或重建现有索引。

## 4. 推荐配置契约

以下字段与数值是供审阅的推荐设计，尚未获得批准。所有类型保持32 KiB配置上限、严格字段检查、重复属性拒绝、规范化与确定性运行身份。

### 4.1 CORS：`cors`

建议字段：`allowedOrigins`、`allowCredentials`、`allowedMethods`、`allowedHeaders`、`exposedHeaders`、`maxAgeSeconds`。

- Origin只接受明确的HTTP / HTTPS来源（scheme + host + port），或者单独的`*`。拒绝路径、用户信息、查询、片段、`null`来源和子域通配。
- `*`与`allowCredentials=true`互斥。明确来源规范化并去重，最多64项，每项最多512字符。
- 方法必须来自既有HTTP方法集合；请求的预检方法同时受原业务路由方法和策略允许方法约束。
- 请求Header名称按HTTP token校验和不区分大小写去重，最多64项；暴露Header最多32项。拒绝CR/LF和任意Header通配。
- ApiKey及Authorization可作为明确列出的浏览器请求Header；内部`X-WebApi-*`、代理身份Header不能借CORS获准伪造。
- `maxAgeSeconds`为0–86400，默认600。首次表单不预填全来源通配，也不隐式开启credentials。
- 无Origin的普通调用按现有认证转发。绑定策略后，非法或不允许的Origin返回受控403；合法预检返回204，不访问后端、不执行ApiKey / JWT认证，但仍受IP策略约束。
- 合法Origin的普通调用在认证拒绝、限流、上游错误和缓存命中时仍产生一致的CORS响应。IP在CORS之前拒绝的请求不披露CORS授权信息。
- 绑定CORS时由平台统一控制响应`Access-Control-*`，先清理上游同名字段，避免上游扩大允许范围。明确来源时合并`Vary: Origin`，预检同时合并请求方法和请求Header的Vary维度。
- 只针对真正带Origin和Access-Control-Request-Method的OPTIONS生成预检路由。普通业务OPTIONS保持原路由语义；同路径不同业务方法的预检必须确定地匹配对应原路由，不能依赖生成顺序。

### 4.2 IP Filter：`ip_filter`

建议字段：`mode`（`AllowList` / `DenyList`）、`ranges`（IPv4 / IPv6单地址或CIDR）。

- 只使用已有可信代理边界处理后的`Connection.RemoteIpAddress`；业务策略不能声明新的可信代理，也不能读取原始X-Forwarded-For作为客户端IP。
- 单地址规范化为/32或/128；CIDR规范化为网络地址；IPv4-mapped IPv6归一化为IPv4。拒绝DNS名称、端口、zone ID、无效前缀和重复规范化网络。
- 最多256项。AllowList必须至少一项；DenyList空列表合法，表示该策略不拦截任何已知地址。未知客户端地址始终受控拒绝。
- `/0`网络允许表达，但表单和发布Review必须显式显示“全部IPv4 / IPv6”的效果；不将其解释为可信代理。
- AllowList匹配后允许、未匹配拒绝；DenyList匹配拒绝、未匹配允许。拒绝返回受控403，认证 / 后端均不执行。
- 网络段在运行代际构建时编译，请求阶段不解析JSON、不做DNS访问。

### 4.3 Transform：推荐A范围，`transform`

建议支持请求Header、响应Header、后端路径与查询参数；变换是静态、确定性的配置，不引入脚本、正则表达式或任意模板变量。

- `requestHeaders`与`responseHeaders`包含明确的Set / Remove操作；同方向同一Header不允许重复冲突。每方向最多32项，每个值最多1024 UTF-8字节，静态值不允许控制字符。
- 禁止修改或移除身份与信任字段：Authorization、X-API-Key、Cookie / Set-Cookie、Host、所有X-WebApi-*、Forwarded / X-Forwarded-*、Trace传播字段。
- 禁止修改协议、正文描述与缓存控制字段：hop-by-hop字段、Content-Length、Transfer-Encoding、Content-Type、Content-Encoding、Trailer、Vary、Cache-Control、Age、ETag、Last-Modified，以及Access-Control-*。
- 可用例：请求添加`X-Partner-Version: v2`，响应添加`X-Upstream-Service: inventory`。静态策略值不得用于存放凭证；现有认证机制处理身份。
- `path`允许可选`removePrefix`与`addPrefix`：按完整路径段边界移除前缀，再添加前缀。移除前缀不匹配原请求时受控拒绝，不继续发送未预期路径。禁止scheme / host / method变化、点段、反斜线及歧义编码；每项最长512字符。
- `query`允许显式Set / Remove：名称区分大小写，Set替换该名称全部值，Remove删除该名称全部值，其余参数保持原语义与重复值。最多32项；名称最多128字符、值最多1024 UTF-8字节。
- 上述变换只作用于后端请求副本，授权、路由命中、审计模板和缓存输入仍基于原请求。每次物理重试从原请求构造副本，不能重复追加前缀或Header。
- 本方案不读取或改写请求体 / 响应体，保留现有流式传输与正文重放边界。
- 若用户选择B（包含JSON正文），必须另补正文映射、大小限制、编码与压缩、失败行为、流式绕行、Content-Length重算、请求签名、重试和缓存处理契约，再进入该范围实现；不能将A的通过当作B完成。

## 5. 运行链与组合规则

建议在YARP业务管线中将代际 / 原路由上下文租约建立提取为单一组件，随后执行IP → CORS → 认证 → 现有流量策略 / 缓存 / 熔断 / 重试 / 转发。租约在预检、拒绝、异常、取消及正常响应均释放一次；配置切换期间，一次请求始终使用同一冻结代际。

绑定仍维持每类型一个、优先级0–1000、组织 / 项目作用域一致。绑定上限扩展为9；安全阶段固定，不通过数字Priority把Transform移动到认证前，UI需解释这一语义。9类全绑定必须可校验和发布。

Transform请求侧通过同一YARP变换管线覆盖首次与重试。响应Header变换在统一输出阶段执行，覆盖真实代理响应和缓存命中；没有后端响应的网关拒绝 / 预检不伪装为上游成功响应。

当前缓存键已包含environment、deploymentSequence、route、apiVersion、cluster与认证身份，新发布 / 回滚代际天然隔离。CORS允许的Origin需纳入内部缓存键维度，且最终CORSHeader逐请求生成，不能把首个Origin的授权复用给其他请求。

保留上游`Vary: Origin`的内容隔离语义。需要区分平台合并的Origin Vary与上游真实Vary，更新缓存资格判断及内部键配合，不能通过删除上游Origin Vary提高命中率。其他未允许的上游Vary维度继续旁路。

现有缓存存储Header采用白名单，未存储Access-Control-*。响应Transform放在统一输出阶段，也可确保未列入缓存白名单的业务Header在Hit / Miss两条路径上保持一致；不扩大Set-Cookie、缓存控制或正文变换能力。

## 6. 快照、节点与跨环境

- 仅快照包含至少一个启用的新三类绑定时编译为2.3；未启用或未被引用的新策略不触发协议升级。旧类型继续选择原2.0 / 2.1 / 2.2规则。
- 新网关注册和心跳声明支持2.0、2.1、2.2、2.3；保留当前能力列表最多4项限制。能力必须绑定当前节点InstanceId，不接受旧实例遗留声明。
- 2.3允许现有JWT / retry / cache及新三类，保留源PolicyId、修订和内容身份；将当前“仅2.2”的相关条件扩展为明确的版本能力集合，不简单移除协议限制。
- 候选创建、提交、真正发布、重试发布、回滚与ACK均检查实际目标快照版本。旧2.2节点不能接收2.3成功ACK，也不通过缩减目标节点数量绕过门禁。
- 保持16 MiB载荷上限、10000业务路由和40000运行策略限制。生成预检YARP条目不伪装成新的业务API或改变发布配额含义；内部条目数仍需有与业务路由成比例的明确界限。
- 历史2.0 / 2.1 / 2.2载荷原字节、签名及Hash不改写；旧历史回滚继续执行原业务语义。2.3回滚同样使用原历史载荷及新的部署序列，保留在途请求租约。
- 跨环境复用现有制品晋级流程：CORS的allowedOrigins、IP的ranges作为目标环境显式参数，其他行为冻结；Transform A的规则整体冻结。目标环境不得自动继承源环境Origin / CIDR，必须显式确认并按目标作用域校验。
- 目标策略映射与私有策略准备仍走已有作用域、资源修订和审批门禁。不能跨项目共享或悄然修改原共享策略。

## 7. 观测、UI与验收证据

新增受控策略决策：CORS（Allowed / Preflight / Rejected / Bypass）、IP（Allowed / Rejected）、Transform（Applied / Rejected / Bypass）。拒绝原因使用固定枚举，控制台显示已生效的源策略修订；不把IP原文、Origin原文、改写值、查询值、JWT或密钥作为策略指标标签。

现有5类决策上限扩展为8（timeout仍沿用超时观测，不重复加入决策），同步扩展收集器、投影、Trace / 日志过滤、指标合法值和UI；全部旧日志仍可读取。

控制台采用现有桌面企业表单和错误提示：类型页签、结构化配置、校验、复制、作用域、绑定、运行修订差异、发布Review。Review明确展示通配来源、全部IP网络、路径改变以及目标节点能力；保存工作配置不显示为运行生效。保留ETag、幂等键、只读权限和切换组织 / 环境时的异步隔离。

| 验收领域 | 必须通过的实例 / 边界 | 证据要求 |
| --- | --- | --- |
| 域配置 | 合法与非法Origin / CIDR / Header / 路径 / 查询、重复字段、限制边界、9类组合 | 正反例自动回归，错误代码及规范化结果 |
| 管理权限 | 组织共享、项目私有、跨范围拒绝、只读、复制、ETag冲突、幂等 | 真实管理接口与数据库回归 |
| CORS | GET-only预检、同路径多方法、普通OPTIONS、允许 / 拒绝来源、credentials、无Origin、认证拒绝和429 | 真实HTTP响应、预检后端调用计数为0 |
| IP | IPv4 / IPv6 / mapped、CIDR边界、未知地址、伪造XFF、可信 / 不可信代理、ApiKey / JWT / Anonymous | 真实网关HTTP与可信代理边界回归 |
| Transform A | 请求 / 响应Header、完整段前缀、查询重复值、危险字段拒绝、保留原API授权 | 回显后端与实际YARP转发回归 |
| 策略组合 | 两来源缓存Hit / Miss、原Vary、每次重试变换一次、熔断、错误 / 取消、9类同绑 | 真实缓存 / 多尝试计数、租约与代际回归 |
| 发布能力 | 冻结后工作修订改变不污染候选、旧节点阻断、ACK实例不匹配、失败发布不伪成功 | 控制平面与两个独立网关实例 |
| 历史兼容 | 2.0 / 2.1 / 2.2载荷加载、旧回滚、2.3发布与历史回滚、在途请求 | 原Hash保留、双节点版本 / 序列 / ACK |
| 跨环境 | Origin / CIDR显式映射、Transform冻结、目标权限与审批、失败重试 | 独立TEST / PROD夹具的制品晋级回归 |
| 观测 | 三类决策过滤、固定原因、无秘密标签、旧日志兼容 | 实际日志 / Trace / 指标及控制台截图 |
| UI | 1440px表单、字段错误、只读、修订差异、目标节点门禁、导航 | 页面交互回归与截图QA |

实现后的验收在独立资源owner中进行；保留每次失败和最终复验，不把Mock、单节点或源码审查当作双网关正式闭环。第2包负载不复启，4192运行数据不作测试夹具。

## 8. 后续阶段与授权状态

1. 用户选定Transform范围，审阅上述配置与接入设计。
2. 形成无未决业务项的书面规格，并按brainstorming技能要求审阅；随后编写实施计划。
3. 沿用用户已偏好的当前会话执行方式，在独立工作区逐项实现、测试和审查；保留主工作区原文件。
4. 三类全链路验收证据完成后，提供精确提交 / 合并 / 部署审阅包，按届时有效授权执行。

本提案没有改变产品源码、数据库或4192部署，也没有产生提交、推送或main合并。上述阶段说明不是批准记录。
