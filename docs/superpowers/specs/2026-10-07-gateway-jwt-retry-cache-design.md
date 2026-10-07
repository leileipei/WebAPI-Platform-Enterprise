# 功能补全第二批：网关 JWT、重试与缓存详细设计

日期：2026-10-07（Asia/Shanghai）。状态：书面设计草案，待用户审阅；本批产品代码、运行配置和依赖尚未修改。

源码基线：主分支 `feature/core-loop` / `fe9b85dec8c790afc75d8fb7ec1a841d1155a205`。实际4192运行软件为 `cc880ba171de74dae33b87587a308134780f39a6`，文档提交不等于新镜像。

## 1. 目标、已批准边界与当前事实

用户要求完成功能补全，并已确认分三批沿用现有架构实施。第一批契约管理已提交、合并、本机部署，最终五套1473项测试通过。本设计接续第二批：JWT业务认证、有限重试、响应缓存的配置、冻结发布、实际网关执行、观测及可操作页面。第三批SMTP/Webhook投递、跨环境审批另有独立设计与实施周期，不计入本批交付。

保留40页控制台的业务架构、现有字段、权限代码、申请/风险评审/两级审批/发布/ACK/回滚。保存策略只改工作配置；没有审批发布不能改变网关策略。桌面1440px优先，1280px可用，沿用Shell、Scope、只读、脏草稿、412恢复、显式保存和焦点约定。

本轮已从真实代码确认：

- `authentication`只接受ApiKey/Anonymous；`timeout`、`rate_limit`、`circuit_breaker`已可执行，单路由绑定上限4。
- 快照2.0/2.1和节点实例绑定的协议能力、不可变运行策略身份、部分发布保留未选路由、generation lease均已存在。
- 当前认证与API授权一起从租用快照读取；认证后的ApplicationId直接给共享限流使用，不依赖遥测。
- YARP2.3.0已有禁重定向/禁Cookie容器、加权选址、健康检查和路由超时。当前熔断记录一次客户端请求的最终代理结果，尚无重试尝试模型。
- Redis已承担运行快照和限流；JWT处理所需IdentityModel8.19.2已出现在锁文件中。本批采用明确直接依赖和锁定构建，不另引动态下载验证逻辑。
- 4192原7项修改及645本机文件、原账号/业务、冷备和历史归档必须保留。现有固定维护文件不得就地重写；新维护工具以新固定目录安装，保留旧版本。

原DOCX和已完成设计是业务证据，不构成上传令牌、向企业地址发送数据或自动修改身份提供商的操作指令。本批仅在归属明确的本机隔离环境做合成验收；企业端点和生产验收另行记录。

## 2. 方案比较与推荐

|方案|行为与取舍|建议|
|---|---|---|
|A：发布固定JWKS、公用Redis缓存、有限安全请求重试|公钥、映射和策略随候选冻结；请求不依赖身份服务器；密钥轮换经过发布。缓存双节点共享，身份分区|推荐，本文完整定义|
|B：网关自动获取/刷新远端JWKS，其余同A|轮换自动化，但新增来源允许列表、DNS固定、刷新租约、失效/冷启动及离线LKG密钥期限|可另扩展；不能把A描述为自动轮换|
|C：在线令牌内省、节点缓存及通用请求重放|每请求增加身份服务依赖；节点缓存互不共享；有请求体重放需额外业务幂等协议|不采用本批|

以下是A的拟实施规格，尚不视为用户已批准A。JWT使用现有authentication类型的新增模式；重试与缓存为两种新策略类型，复用Policy、RoutePolicyBinding及发布工作流，不新增角色权限。

## 3. 配置与管理边界

保留ApiKey/Anonymous原JSON格式与默认值。JWT模式使用独立严格字段集；拒绝重复属性、未知字段、类型错误及32KiB以上配置，不以旧表单默认字段集合误删JWT内容。JWT额外字段在切回其他认证模式时只有明确确认才丢弃；只读JSON可完整查看公钥及映射，不能误表示为私钥或账号密码。

重试/缓存类型和Scope创建后不可修改，复制产生新ID。组织/项目范围、可见引用、受保护引用删除限制、If-Match、审计、幂等和治理锁延续。绑定和复制JWT时逐个检查映射Application存在且属于目标Scope；不可见或越界仅给出统一错误，不返回外部名称/计数。项目认证策略可引用同项目或同组织的组织应用；组织策略只能引用同组织的组织应用，不能藏入某一项目应用再共享至其他项目。

一条路由最多六种类型，各类型最多一个。执行顺序固定，Priority用于展示排序，不能把缓存放到认证前。保存、绑定、候选预览、冻结、编译、网关激活都校验有效组合。ApplicationRoute限流接受ApiKey或JWT，Anonymous仍拒绝此维度；JWT不能被折叠成Anonymous。

JWT映射不授予新API权限。映射修改只能确定由哪个已有应用进行授权检查；应用仍需Active、当前环境/API授权且在有效期内。发布资源修订全集包含映射应用和授权，不仅包含公钥策略；申请后变更不影响已冻结候选。映射应用禁用/授权到期在运行快照中的既有状态与时间窗生效；工作中撤销仍须按既有流程发布，不能声称令牌实时撤销。

## 4. JWT模式

### 4.1 配置字段

|字段|校验/行为|新表单值|
|---|---|---|
|mode|固定JWT|JWT|
|issuer|精确绝对HTTPS URI，最长512字符；禁止userinfo/query/fragment。HTTP仅允许部署明示的本机fixture origin|无默认issuer|
|audiences|1–8个不重复、非空、最长256字符的精确字符串；匹配任一|用户填写|
|allowedAlgorithms|非空RS256/ES256子集|RS256|
|allowedTokenTypes|非空JWT/at+jwt子集，精确匹配typ；不自动接受缺失typ|JWT、at+jwt|
|clockSkewSeconds|整数0–120|30|
|maxTokenLifetimeSeconds|整数60–86400，限制exp-iat|3600|
|applicationClaim|简单单层claim名称，1–64 ASCII字符；不允许sub/iss/aud等保留字段或JSON路径|azp|
|applicationMappings|1–128项{claimValue,applicationId}，claimValue精确字符串≤256、无重复；ApplicationId服务端检查Scope|用户显式绑定|
|jwks|1–8把带唯一kid的公钥；总策略≤32KiB；RSA2048–4096或EC P-256，alg/kty/crv一致，use=sig|用户粘贴/附加本地JSON|
|forwardBearer|boolean，显式控制验证后Bearer是否转发；发布Review说明风险|false|

JWKS只接受必要的公共字段及有限可忽略公共元数据，拒绝oct密钥、私钥参数、jku/x5u、远端解析和无法理解的关键扩展。策略不保存client secret；kid用Ordinal唯一，不能试签全部钥匙代替kid匹配。公开公钥不是密码，但令牌、声明原文及私钥均不得进入公共材料。

### 4.2 请求验证、应用授权与转发

1. 与现有请求一样先租用路由generation，并从该generation取得认证策略、应用及授权；从头至尾不查询当前工作配置或PostgreSQL。
2. JWT路由只接受一个Authorization头中的Bearer。拒绝重复/合并多个值、非Bearer、空token、超过16KiB、超过3段JWS、非规范base64url、重复JSON属性和过深/过多声明；不从query或Cookie读取token。X-API-Key不成为JWT失败后的后备认证。
3. 使用锁定IdentityModel执行签名验证，固定算法、公钥类型及kid。不接受none/HS*、算法混淆、加密JWE、未知crit或令牌指定的网络密钥。验证后才能消费声明。
4. 严格issuer/audience/type；使用专用API资源audience，发布Review明确不得复用控制台SSO client的用途。exp、iat及非空字符串sub必需，nbf可选；NumericDate应合法，iat不能在允许偏移以外的未来，exp>iat且在最大生命周期内，nbf≤exp。没有aud、错误发行方/签名、过期等返回401 `invalid_jwt`及通用WWW-Authenticate Bearer挑战，不输出实际token/claim/kid/异常堆栈。
5. 从已验证的单一字符串applicationClaim精确查映射。不映射、非字符串或多个值均401，不允许客户端直接传ApplicationId。映射到非Active应用或有效API授权不足时403 `api_not_granted`。成功后记录ApplicationId并沿用API授权时间窗。
6. JWT路由移除入站X-API-Key和伪造的X-WebApi身份/部署头，重新生成内部头。forwardBearer=false时移除Authorization；true时只转发原已验证Bearer。缓存决策始终知道原请求是JWT认证，不能因头已移除视为匿名。旧ApiKey/Anonymous路由的业务Authorization透传保持原行为。

原始sub/issuer只在本次内存身份上下文中用于缓存HMAC，不输出日志/Trace标签。公开错误只区分JWT无效与应用授权不足，不把命中哪条映射暴露出去。

### 4.3 密钥轮换和边界

管理员导出/粘贴公开JWKS，先同时加入新旧公钥、正常发布并确认双ACK，再按发行方完成签名切换；等待最长令牌寿命后发布移除旧钥。紧急移除也必须发布。未知kid直接401，不网络刷新。本机验收读取独立Keycloak fixture的公开JWKS和合成业务token；不修改现有控制台SSO client/realm或其Secrets。

不宣称支持自动OIDC discovery、在线内省、任意JWT算法、JWE、刷新令牌签发或实时逐token黑名单。业务token与控制台Cookie/SSO会话相互独立。

## 5. 重试

|字段|取值/语义|新建值|
|---|---|---|
|maxAttempts|整数1–3，含首次调用；1表示不重试|2|
|perAttemptTimeoutMs|整数10–300000，且绑定时≤有效路由总超时|3000|
|baseDelayMs|maxDelayMs范围内整数0–1000|50|
|maxDelayMs|整数0–5000且≥baseDelayMs|500|
|jitterPercent|整数0–100|20|
|retryStatusCodes|0–3项不重复502/503/504|502、503、504|
|retryConnectionFailures|boolean；仅请求未送出的连接建立故障|true|

至少一个重试原因启用，或maxAttempts=1。按指数退避加有界抖动；只接受有效、非负且可在剩余预算内等待的Retry-After，无法满足时返回最终原响应，不超过最大次数/总超时。所有尝试、退避及响应读取共用有效路由总期限；客户端取消立即取消等待和尝试，不算后端故障。

运行仅对无请求体的GET/HEAD启用。POST/PUT/PATCH/DELETE、含body或不能证明无body、Upgrade/WebSocket、gRPC/CONNECT、客户端Range/条件请求全部跳过重试，正常走既有代理。跳过原因展示在观测与绑定预览。不得仅因收到Idempotency-Key就重放写操作；重试本身不能宣称业务恰好一次。

YARP响应transform在发送任何最终头/body之前判断候选502/503/504；可重试响应不写入客户端并及时释放上游资源。已开始响应、响应body中断、总超时、客户端取消和不确定是否已发送的连接错误不重试。perAttemptTimeout只在能够证明没有发送请求时可进入连接故障重试，否则终止，避免把超时等同可安全重放。最后一次失败响应完整转发；传输错误依现有YARP错误约定返回502/504，不凭空生成上游成功。

每次尝试从同一generation的健康Destination候选中选择；优先不同可用Destination，仅一个时可再次选同一目标。保持现有权重/负载均衡与主动/被动健康检查；不把未调用的目标标为失败。重试不再次扣限流令牌。

熔断仍按一次入站调用的最终上游结果记一个样本，不让重试放大样本数。HalfOpen探测最多一个物理上游尝试，重试策略显式记录ProbeBypass。额外尝试只在同一熔断准入内执行；并发期间熔断已变Open时停止后续重试，但首次在途响应可完成。半开名额和旧epoch在取消/异常时仍释放。

## 6. 共享响应缓存

### 6.1 配置及默认语义

|字段|校验/语义|新建值|
|---|---|---|
|ttlSeconds|整数1–3600，实际新鲜期受上游更短限制|60|
|maxEntryBytes|整数1024–1048576，≤部署上限；body和允许头总计|262144|
|varyHeaders|0–8个不重复的允许请求头；各值≤1024，总≤8KiB。白名单Accept/Accept-Language/Accept-Encoding和部署明示业务头，禁止Cookie/Authorization/X-API-Key/Host及内部头|Accept、Accept-Language、Accept-Encoding|
|identityPartition|固定VerifiedIdentity，不能在表单关闭身份分区|VerifiedIdentity|
|redisFailureMode|固定Bypass，不伪造Hit|Bypass|

首次为JSON/普通HTTP GET的200响应提供缓存；HEAD、含body、Upgrade、gRPC/SSE、Range、客户端条件请求和Cookie请求直接跳过。ApiKey/Anonymous路由若带业务Authorization也跳过，避免将未经本网关验证的后端身份按应用/匿名域共享。GET与HEAD不共享条目。不做304合成、代理重验证、stale-if-error、请求合并或写请求自动删除，页面明确采用TTL/每次发布新命名空间失效；不得把它说成完整HTTP缓存代理。

上游必须显式允许共享缓存：public且有正max-age，或正s-maxage；缺失显式共享声明不存。无论身份是否分区，no-store/private/no-cache、冲突或不可解析Cache-Control、Set-Cookie、Vary:*、未登记Vary头、trailer、SSE/压缩语义不能准确保留、非200及中断响应均不存。请求no-store/no-cache/max-age=0等需新鲜来源的指令走旁路，不用缓存替代验证。缓存只保留有限头白名单（Content-Type/Content-Encoding/Content-Language/ETag/Last-Modified/Cache-Control/Expires/Date/Vary），不保存hop-by-hop、Set-Cookie、Trace和内部认证头。

读取/存储依据RFC9111计算包含Date/Age和传输时延的当前年龄，实际期限=min(策略TTL,上游剩余新鲜期)。Hit返回正确Age，并重新生成本次请求/Trace/部署标识；不得重放首次请求Trace或把Date改成伪造的新来源时间。

### 6.2 身份键与容量

Key覆盖部署缓存命名空间、环境、deploymentSequence、路由/版本、运行Cluster身份、认证和缓存策略身份、原始方法/path/query及登记Vary值。路径/query保持准确原始语义，不排序重复查询参数，不忽略未登记query；组合输入超过16KiB跳过。新发布或回滚使用新的sequence，因此不会命中历史响应。部分发布亦使该环境缓存重新填充，此影响在发布Review显示。

身份分区：Anonymous固定匿名域；ApiKey使用已授权ApplicationId，同应用多把API Key共享；JWT使用ApplicationId与已验证iss/sub的HMAC，因此同应用不同用户不共享。forwardBearer=false时令牌刷新但身份相同可共享；true时额外纳入完整已验证token的HMAC，防止同一用户不同scope/role的转发令牌共用后端响应。没有通过当前认证和API授权，不能读取缓存。HMAC密钥是部署私有32字节文件，两节点使用同一值，不在Policy/Snapshot/环境变量/日志/交付包出现。部署轮换该密钥使缓存冷启动，不影响认证凭证。

部署默认共享总量64MiB、2000条，单条不超过1MiB；项目不能提高部署上限。Redis专用前缀以原子脚本维护容量/条目/到期索引，所有环境总计受该部署预算约束，不能每次新sequence另建无界配额。每次有限清理最多128条到期元数据；计数不能准确恢复时拒绝新增缓存并记录Bypass，继续正常代理。读写100ms独立预算，超时不自动重试写入，不降级为节点本地无限缓存。

响应采用有界旁路捕获，不先无界读完整响应。超过条目预算立即停止保留缓存副本，客户端继续流式接收；不会截断原响应或因存储故障变为500。JWT+重试+缓存并用时，只存最终成功完整响应；不存中间尝试。没有本批缓存管理API或秘密Key展示。TTL不保证业务立即一致；有严格即时一致要求的路由应不绑定缓存。

### 6.3 顺序

同一generation：认证/授权 → 一次限流 → 缓存资格/读取 → 未命中时熔断准入 → 有界重试/代理 → 最终熔断样本 → 合格成功响应缓存写入 → 请求观测。

Hit不调用上游、不取得HalfOpen名额、不贡献熔断恢复成功；认证/限流不可跳过。熔断Open时可返回仍新鲜且已重新授权的Hit；Miss仍503，无过期数据救援。Redis故障Bypass后仍遵守熔断/总超时。没有缓存/重试绑定或不合格请求继续既有YARP流式路径。

## 7. 快照2.2与兼容

2.2新增可选RuntimeRoute.AuthenticationMode（ApiKey/Anonymous/JWT），同时保留RequireApiKey旧字段；JWT模式RequireApiKey=false，但必须有JWT认证绑定，两者严格核对，绝不能让2.1网关按Anonymous执行。JWT映射引用的应用即使零授权也冻结为有效应用记录（零授权→403），不丢掉其映射或当作未知主体。运行快照不包含发行方client secret或缓存HMAC密钥。

2.0/2.1读写保持原字段/字节/哈希规则，不回写历史快照；旧协议拒绝JWT、retry、cache及2.2新字段。只要最终快照含任一新增模式/类型，输出2.2；否则继续已有2.0/2.1分支。部分发布混合新旧冻结策略仍可编译2.2，未选路由不被隐式改成新修订。认证类型解析需按快照协议限制，不能全局放宽旧2.0 validator。

节点注册/心跳明确声明支持2.0/2.1/2.2，绑定当前InstanceId；不能凭AppVersion推断。预览、提交、Worker发送、重试发布/回滚再次验证全部启用目标节点在线且支持目标协议。能力不足409 `gateway_schema_unsupported`，不更新desired、不增加sequence、不删目标。热替换旧能力实例如实待恢复/失败，不宣称跨节点原子切换。

升级软件不自动发布JWT/缓存/重试，原4192业务4/4继续。软件回退前必须确认当前运行快照可被旧软件解析；2.2发布后先按现有流程回滚至兼容快照、双ACK，再降镜像，不拿旧冷备覆盖新业务数据。

## 8. 页面、权限与观测

策略中心开放Retry/Cache，JWT在认证编辑器内。表单提供issuer/audience/public JWKS/kid摘要/算法/映射表、受限重试参数、TTL/容量/Vary/身份分区。显示工作修订、发布生效条件、固定公钥轮换边界、重试跳过条件与缓存一致性；不能用空字段默认创建匿名策略。JWKS导入只读取用户选定本地JSON，不自动访问URL。所有下拉只给当前可读应用；不把客户端列表过滤当服务端授权。

Route绑定最多六类，显示有效认证和执行顺序；批量绑定、复制、停用、应用映射变更显示受影响的可见路由/环境、未选发布仍用旧修订。发布Review展示公钥kid增删（无令牌）、JWT映射摘要、Bearer转发开关、重试最多物理尝试及缓存TTL/环境冷启动，沿用风险评审和原审批人数，不自动审批。

只读角色可查看/验证结构但无保存、绑定、清缓存或发布入口。Scope切换晚响应不覆盖新Scope；412保输入并需显式采用新修订；脏草稿退出/切模式需确认；Enter不隐式保存，Tab/ShiftTab焦点可操作。1440/1280用真实DOM尺寸与CUA交互证据核验。

Policy决策白名单由2类扩至authentication/retry/cache及既有两类，最多5条最终决策，每类仅一次；尝试细节独立最多3项，不用递增任意标签。日志/Trace/DTO/CSV/源投影同时更新，旧记录缺字段仍可读。决策包括JWT验证/授权拒绝、RetryRetried/Exhausted/Bypass、CacheHit/Miss/Bypass/Stored；CacheMiss并最终Stored记录成同类结构而非挤掉别的策略。

一次入站请求只计一个HTTP总量/耗时；每个真实上游尝试增加一个client span和attempt计数，记录实际Destination/状态/耗时，最终请求记录attemptCount与cacheDisposition，Hit的Destination为None。HalfOpen与缓存返回明确区分，不把本地Hit当上游成功。指标标签限环境/节点/源PolicyId/类型/受控decision，不添加token/sub/query/kid/缓存Key/发行方任意字符串。关闭遥测策略仍生效，源缺失显示Unavailable/Partial，不补零。

## 9. 模块与实施约束

|模块|拟变更|
|---|---|
|Domain/Contracts|严格JWT/retry/cache配置；六类组合；有效认证与2.2协议校验、受控观测值|
|Infrastructure Policies/Releases|应用映射Scope校验/引用保护、冻结资源修订、协议编译/能力门禁、原幂等审计|
|Gateway Security|签名验证器、JWT上下文、统一认证/API授权，generation生命周期|
|Gateway Policies/Forwarding|共享有界缓存、有限重试协调器；复用YARP2.3 forwarder/transform/健康行为，不手写HTTP代理|
|Gateway Observability与查询投影|五类决策和真实attempt spans/计数，有限标签、旧记录兼容|
|Console|动态表单/映射/绑定/Review/日志Trace扩展与Scope晚响应保护|
|Deploy/runtime|32字节cache-HMAC私有文件挂载、缓存预算与2.2能力；新维护工具固定目录与恢复门禁|

不增加业务表或绕过发布。公共JWKS及映射保存在现有Policy.Config、候选及运行快照；须补应用删除/Scope迁移引用保护。实现前针对锁定YARP2.3 API做最小测试夹具，证实重试响应抑制、默认transform、health与资源释放；不能仅依据最新文档假设版本接口。若证明需改变本设计的代理语义或资源预算，记录具体差异再确认，不偷偷换成另一种转发器。

新依赖必须中央锁定并离线/固定构建验证；优先使用已有IdentityModel8.19.2和StackExchange.Redis3.3.1，不在请求时动态加载组件。单元、集成、Gateway、Console及Runtime全回归后，精确提交本批、合并、本机冷备、UUID原库恢复、候选/旧/候选演练，再安装4192。原本机修改/账号/数据/旧固定工具/历史包逐SHA保护；新fixture/业务QA不写原4192。

## 10. 验收矩阵

|领域|必验正向/负向|
|---|---|
|JWT解析/密码学|RS256/ES256；kid/type/iss/aud/exp/iat/nbf/sub；none、HS混淆、重复属性、jku/x5u/crit、私钥及超限均拒绝|
|身份与Scope|映射同应用多凭证、无API权限/到期/Disabled403，未映射401；跨Scope保存/复制/绑定/冻结拒绝；原SSO/ApiKey/Anonymous不变|
|运行一致性|A在途请求跨B激活仍全程A认证/策略/应用/目标；当前generation不被重试读取；遥测关闭行为一致|
|重试|合成上游两失败后一成功、最多3次/权重健康选择；1次、unsafe/body/Upgrade/Range/条件跳过；已经发响应/不确定连接/取消/总超时不重放；HalfOpen1物理尝试|
|缓存|A填充B命中、TTL与Age、不同环境/路由/版本/query/Vary/应用/JWT用户隔离；forwardBearer=true同用户不同token不共享，ApiKey/Anonymous的业务Authorization旁路；无效/过期认证不能读Hit；policy及发布/回滚新sequence冷启动|
|缓存HTTP保护|private/no-store/no-cache/Set-Cookie/未知Vary/304/压缩异常/流式/中断/超容量均不存；大小超限仍完整流式代理；Redis故障Bypass|
|组合|一次限流、Hit无熔断样本、Miss受Open保护、Retried+Stored最终一致、最后失败透传、attempt计数真实|
|发布协议|旧2.0/2.1字节和哈希；2.2完整/畸形激活；目标能力不足不改desired/sequence；部分发布保旧修订、重试/回滚双ACK|
|权限/UI|1440/1280真实交互、只读、Scope迟到、412、Dirty、键盘与焦点、映射权限范围、有效绑定与发布Review|
|数据/材料|原保护文件/账号/表保持、固定源码镜像身份、无生产断言、无rawJWT/sub/私钥/HMAC/连接Secret/库转储；测试克隆归属清理|

本机端到端使用独立UUID克隆、合成发行方/Keycloak client、受控后端计数与真实Redis故障，证明实际次数/跨节点Hit/不同用户隔离及新2.2发布回滚。无企业真实issuer时不宣称完成企业认证接入，无容量基准不声称5k RPS达标。

## 11. 自查与下一阶段

自查覆盖：JWT失败不回退Anonymous/API Key；映射不授予API权限；所有决策同一generation；缓存不绕认证/限流；中间响应不发/不缓存；旧快照不接受新语义；所有预算有上限；没有自动变更SSO、原数据或外部通知；完整六项仍分批记录。自查修正了转发Bearer时同用户不同权限令牌的缓存分区，以及旧ApiKey/Anonymous路由业务Authorization的缓存旁路，不仅用sub或ApplicationId假定后端响应相同。

待用户决定：采用本文A，或要求自动远端JWKS的B。A得到书面规格确认后再编写逐任务实施计划、选择执行方式；现有本会话实施偏好可沿用，不要求重复授权已确定的本地提交/合并/备份范围。代码开发尚未开始，本次仅提交可审阅设计草案。

## 12. 一手依据

- [RFC8725 JWT最佳实践](https://www.rfc-editor.org/rfc/rfc8725.html)：算法、发行方/受众及不同JWT用途的验证。上述有界固定公钥/应用映射是本项目设计选择。
- [RFC9111 HTTP Caching](https://www.rfc-editor.org/rfc/rfc9111.html)：共享缓存、Authorization、Vary、Cache-Control和Age。本批采用比协议更保守的受限缓存子集。
- [Microsoft YARP middleware](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/middleware?view=aspnetcore-10.0)：代理特性、错误特性、响应尚未开始的处理条件。
- [Microsoft YARP transforms](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/transforms?view=aspnetcore-10.0)：默认转发头与transform边界；request/response body需自行采用有界中间件。
- 2026-10-07实际源码：`GatewayApp`、`ApiKeyMiddleware`、`TrafficPolicyMiddleware`、`RuntimeGenerationStore`、`SnapshotCompiler`、`SnapshotValidator`、`SnapshotSchemaCapabilities`、`PolicyService`、`RoutePolicyService`、`PolicyEditor`及锁文件。本轮公开网页验证不代替锁定2.3版本测试。
