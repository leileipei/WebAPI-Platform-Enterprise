# 第3包：CORS / IP Filter / Transform 含JSON正文映射设计规格

日期：2026-10-13。状态：**用户新增JSON请求体 / 响应体字段映射范围；本扩展规格待审阅，产品实施尚未开始。**

版本关系：此前Transform A规格已获用户审阅批准；本规格保留已批准的CORS / IP / Header / 路径 / Query规则，明确更新正文契约和有关组合规则。新范围的审阅不会被此前A批准记录替代。

源码基准：`293b37f2dbd59427cd2d40f6783dc441345528ef`。

## 1. 意图、范围与完成定义

用户要求“2包先暂停，完成3包”，先选定Transform A，随后明确要求“同时加入JSON请求体／响应体字段映射”。最终范围包括请求 / 响应头、路径、查询参数及两个方向的JSON字段映射，完成发布、双网关执行、观测及回滚闭环。本包为现有企业策略引擎的架构扩展，保留组织、项目、环境、权限、源修订、审批和数据模型。

交付范围包括三类策略的结构化编辑、严格校验、复制、路由绑定、发布Review、冻结候选、审批、双节点能力与ACK门禁、运行执行、日志 / Trace / 指标、历史快照兼容、回滚和现有制品跨环境晋级兼容。每一环节须有实际验收证据。

任意脚本 / 正则表达式 / 动态模板、认证方式新增、Destination主机改写、API新建向导重组不在本包范围内。JSON映射使用有界缓冲；无正文映射规则的流式传输和既有安全重放条件保持。第2包保持暂停，本包不启动或续跑其性能矩阵。

实现与验收使用独立工作区和资源owner。主工作区原有改动、4192账户和运行数据不作为夹具、不被覆盖。书面规格审阅只批准进入实施计划阶段；本文件不是提交、推送、main合并或4192部署授权。

## 2. 架构选择与兼容契约

选用推荐接入方式：复用Policy实体、策略接口、源修订、内容身份、发布和历史回滚引擎，新增快照协议2.3。对比维持2.2另加策略能力矩阵，此方式让目标协议门禁更明确；对比部署配置直接启用，能够保持业务审批和历史回滚闭环。

新增类型名为`cors`、`ip_filter`、`transform`。现有6类类型名、配置语义和API请求外层字段保持。Policy通用字符串类型和JSON字段不预设数据库迁移；实施计划须验证模型差异，有实际结构变化时才提出迁移设计。

JSON映射执行器位于域层，不依赖HTTP或工作配置数据库；Gateway处理正文读取 / 编码 / 预算，管理预览复用同一执行器，避免浏览器JSON数值转换与真实转发语义不同。新2.3能力声明涵盖本规格完整正文功能；当前实际源码仅支持2.0–2.2，不存在需要沿用的已部署A版2.3。

所有配置使用现有严格JSON校验：对象根、未知字段拒绝、重复属性拒绝、最大深度16、最大32768 UTF-8字节。数组和操作均受以下明确限额约束，不能用增加总限额绕过。规范化结果确定、幂等；运行身份继续由源PolicyId、源修订、类型和规范化配置计算。

每路由每类型最多一个绑定，绑定总数上限由6扩展至9。现有Priority 0–1000保持；阶段次序由策略语义确定，Priority不能把IP移到认证后或把Transform移到原API授权前。停用策略仍校验配置，但不执行、不计入目标快照协议需求。

## 3. 配置与业务行为

### 3.1 CORS

以下六个字段均必填。首次表单默认`allowCredentials=false`、`maxAgeSeconds=600`，不预填全来源通配；来源和方法须由用户填写。

```json
{
  "allowedOrigins": ["https://portal.example.com"],
  "allowCredentials": false,
  "allowedMethods": ["GET", "POST"],
  "allowedHeaders": ["Authorization", "Content-Type", "X-API-Key"],
  "exposedHeaders": ["X-Upstream-Service"],
  "maxAgeSeconds": 600
}
```

- `allowedOrigins`为1–64项，每项最多512字符；只接受HTTP / HTTPS的scheme + host + port，或单独一项`*`。拒绝用户信息、路径、查询、片段、`null`、子域通配和控制字符。scheme / host大小写及默认端口规范化，规范化重复项拒绝；来源不得含尾随路径斜线。
- `*`不得与其他来源并列，不得与`allowCredentials=true`并用。明确来源匹配后回写已验证的当前来源；`*`模式回写`*`，不回写任意未验证文本。
- `allowedMethods`为1–7项，只接受GET、POST、PUT、PATCH、DELETE、HEAD、OPTIONS，规范化为大写并拒绝重复。预检所请求的方法同时受原路由Methods和策略列表约束。
- `allowedHeaders`为0–64项，`exposedHeaders`为0–32项；名称最多128字符，符合HTTP token，忽略大小写去重，禁止Header通配。Authorization和X-API-Key可明确列入请求列表；禁止内部X-WebApi-*、Forwarded / X-Forwarded-*、Host、Cookie / Set-Cookie、hop-by-hop字段和Trace传播字段进入这两份列表。
- `maxAgeSeconds`为0–86400整数；0时不发送Access-Control-Max-Age。credentials启用时仅输出值`true`，关闭时省略该响应字段。
- 未绑定CORS时保持原处理语义。绑定后，无Origin的普通调用继续现有认证和转发；带Origin的普通调用校验来源和方法，不把浏览器自动附加的所有实际Header错误地当作预检列表逐项拒绝。
- OPTIONS同时带Origin和Access-Control-Request-Method才是预检。Origin和请求方法各须单值；请求Header列表须合法、无重复且在允许列表内，最多64项。合法预检返回204，不认证、不访问缓存或后端；IP策略仍先执行。已命中策略路由但来源、方法或Header不允许时返回403、`cors_rejected`。
- 未匹配原业务路由方法的预检沿用未匹配路由的404 / 405，不通过其他API的授权或策略转发。普通业务OPTIONS保持原方法路由行为。
- 合法Origin的普通调用在401、授权403、429、上游错误及缓存命中时保持相同CORS响应。IP在CORS前拒绝时不披露允许来源；CORS拒绝响应不发送允许来源字段。
- 绑定CORS时平台统一控制响应Access-Control-*，清理上游同类Header后生成本策略响应。明确来源合并`Vary: Origin`；预检还合并Access-Control-Request-Method和Access-Control-Request-Headers。保留其他既有Vary项，不能覆盖为单一Origin。

### 3.2 IP Filter

两个字段均必填。首次表单采用AllowList，要求明确填写网络段，不自动信任全网。

```json
{
  "mode": "AllowList",
  "ranges": ["192.0.2.0/24", "2001:db8::/32"]
}
```

- `mode`仅AllowList / DenyList。`ranges`最多256项，每项最多80字符；AllowList至少1项，DenyList空列表表示允许全部已知客户端地址。
- 单地址转成/32或/128，CIDR掩码后的网络地址作为规范值，规范化重复项拒绝。拒绝DNS名称、端口、IPv6 zone ID、非十进制IPv4写法、非法前缀和控制字符。
- IPv4-mapped IPv6单地址转IPv4；mapped CIDR仅接受/96–/128，转成IPv4的/0–/32。更短mapped前缀拒绝，避免混合地址族语义。
- `/0`允许明确配置；表单及发布Review显示它允许 / 拒绝全部该地址族，但它不产生可信代理权限。
- 客户端IP只来自既有TrustedProxyBoundary处理后的Connection.RemoteIpAddress。政策不得读取原始转发Header、不得修改部署可信代理列表。
- AllowList命中允许、未命中拒绝；DenyList命中拒绝、未命中允许。未知客户端IP始终拒绝。拒绝返回403、`ip_filter_rejected`，不执行后续认证和转发。
- 解析和网络段编译在RuntimeGeneration创建时完成；请求阶段不解析配置、不做DNS查询，规则按冻结代际执行。

### 3.3 Transform Header / 路径 / Query

六个顶层字段均必填：requestHeaders、responseHeaders、path、query、requestBody、responseBody。数组可为空，path与两个正文配置可为null；所有字段均无操作时拒绝。Header / 路径 / Query规则独立启用，正文规则的契约见3.4。Set操作必须含value，Remove操作不得含value。Header和Query操作按照配置顺序保持，不能为规范化而改变操作次序。

```json
{
  "requestHeaders": [
    {"action": "Set", "name": "X-Partner-Version", "value": "v2"}
  ],
  "responseHeaders": [
    {"action": "Set", "name": "X-Upstream-Service", "value": "inventory"}
  ],
  "path": {"removePrefix": "/gateway", "addPrefix": "/internal/v2"},
  "query": [
    {"action": "Remove", "name": "debug"},
    {"action": "Set", "name": "region", "value": "cn"}
  ],
  "requestBody": null,
  "responseBody": null
}
```

请求`/gateway/stock?sku=A&sku=B&debug=1`变为后端`/internal/v2/stock?sku=A&sku=B&region=cn`；原API身份、授权范围和日志路由模板仍为原路由。响应Header按配置生成。

- 每个Header方向最多32项，名称最多128字符并符合HTTP token，值最多1024 UTF-8字节，禁止控制字符；同方向同一名称忽略大小写不得重复，Set替换全部原值，Remove删除全部原值。
- 两个方向均禁止变动Authorization、X-API-Key、Cookie / Set-Cookie、Host、所有X-WebApi-*、Forwarded / X-Forwarded-*、traceparent / tracestate / baggage、hop-by-hop字段及由Connection声明的动态hop-by-hop名称。
- 静态hop-by-hop名称在保存时拒绝。实际请求或上游响应的Connection若声明某个已配置业务Header为动态hop-by-hop，该方向跳过这一Header操作，不重新生成它；其他合法操作继续执行。该Header值不写入诊断。全部操作因这一保护被跳过且没有其他实际变换时记录Bypass / hop_by_hop_protected。
- 两个方向均禁止Content-Length、Transfer-Encoding、Content-Type、Content-Encoding、Trailer、Vary、Cache-Control、Age、Expires、ETag、Last-Modified、Date、Content-MD5、Digest、Content-Digest、Repr-Digest、Signature、Signature-Input和Access-Control-*。这些字段归协议、身份、CORS、正文引擎或缓存机制所有，不用于本包静态Header改写。
- 静态值不作为密钥存储机制。表单提供普通业务Header用例，不提供认证Header录入入口；没有任意脚本或动态值插值。
- path非null时必须包含removePrefix和addPrefix，值为null或绝对路径前缀；至少一个非null。前缀最多512字符，只接受以`/`开头的字面量路径段，拒绝百分号、查询、片段、控制字符、反斜线、重复斜线、完整`.` / `..`段和路由占位语法。除根`/`外去除尾斜线作为规范值。
- 移除前缀按完整路径段匹配：`/gateway`匹配`/gateway`和`/gateway/stock`，不匹配`/gatewayx`。移除后空路径变成`/`；移除根`/`视为无操作。添加前缀时连接一个斜线：`/stock`加`/internal/v2`得到`/internal/v2/stock`，根路径得到`/internal/v2/`。
- 配置了path改写的请求须拒绝路径中的控制字符、反斜线、重复斜线、点段、非法百分号转义及编码的分隔 / 点 / 百分号（%2F、%5C、%2E、%25，忽略大小写），防止多次解码改变目标路径。未绑定或只改Header / Query时不扩大原路径限制。
- 非法改写路径返回422、`transform_rejected`，拒绝原因`transform_path_invalid`；不得在错误正文回显原始路径。
- 路径前缀未匹配返回422、`transform_rejected`，拒绝原因`transform_path_mismatch`；不继续发往未经预期的原路径。
- query最多32项，名称1–128字符、值最多1024 UTF-8字节，拒绝控制字符；名称区分大小写，同名称不得重复操作。Set替换该名称全部原值，Remove删除全部原值，其余参数顺序和重复值语义保留。名称和值都是解码后的字面量，按标准URL编码一次，不执行模板替换。
- 变换结果的路径加查询最多16384 UTF-8字节，超限返回414、`transform_rejected`，拒绝原因`transform_budget_exceeded`。该边界只针对启用Transform的改写结果，不调整全局正文或快照上限。
- 不变更scheme、host、HTTP方法或Destination。原路由身份、路径、Query、认证Header和权限不被改写；后端请求副本接收变换结果。JSON原正文通过有界读回租约保留语义，后端副本使用映射后的字节，按3.4执行。

所有配置错误沿用422、`invalid_policy_config`；绑定与作用域、ETag、幂等和发布错误保持现有错误契约。运行错误响应不得包含客户端Origin / IP原文、改写值、JWT或密钥。

### 3.4 JSON请求体 / 响应体字段映射

两个方向可独立配置，null表示关闭。每个启用方向1–32条有序操作；一次方向映射按全部操作原子完成，任一失败不发送部分结果，也不回退原文。

```json
{
  "requestHeaders": [],
  "responseHeaders": [],
  "path": null,
  "query": [],
  "requestBody": {
    "methods": ["POST", "PUT", "PATCH"],
    "maxBodyBytes": 262144,
    "operations": [
      {"op": "move", "from": "/user_id", "path": "/customer_id"},
      {"op": "add", "path": "/source", "value": "webapi"}
    ]
  },
  "responseBody": {
    "statusCodes": [200],
    "maxBodyBytes": 262144,
    "operations": [
      {"op": "move", "from": "/data", "path": "/result"},
      {"op": "remove", "path": "/debug"}
    ]
  }
}
```

请求`{"user_id":123,"items":["A","B"]}`输出`{"customer_id":123,"items":["A","B"],"source":"webapi"}`；上游200响应`{"data":{"stock":10},"debug":"internal"}`输出`{"result":{"stock":10}}`。JSON属性顺序及空白不作为字节保持承诺，数值 / 字符串 / null / 数组语义必须保持；未涉及数字不能通过double转换而丢失精度。

**操作与路径：**

- 字段路径采用JSON Pointer字符串，`/`分层，`~0`表示`~`、`~1`表示`/`，大小写精确匹配。每个path / from最多512字符，最多32层，拒绝控制字符、非法`~`转义及URI片段形式。此处百分号为属性名的普通字符，不按URL路径解码。[RFC 6901](https://www.rfc-editor.org/info/rfc6901/)
- 使用JSON Patch中的add、replace、remove、copy、move语义处理字段与数组，按顺序执行；这是平台配置的受限操作集合，不宣称实现完整JSON Patch接口。add / replace仅有op、path、value；remove仅有op、path；copy / move仅有op、path、from，额外字段和重复字段拒绝。value必须明确存在，可以为null；不得把缺失value与null混为一谈。[RFC 6902](https://www.rfc-editor.org/info/rfc6902/)
- add可新增 / 替换对象成员或插入数组元素；replace / remove及from必须存在，父路径必须存在，不自动猜测或创建父级。创建父对象应由前一条add显式完成。
- 数组索引为零基十进制且无前导零；add支持索引等于长度或末尾`-`追加，其他位置操作必须存在。copy / move的目标使用add语义；move先移除再解释目标，禁止移动到自身子路径。
- 空字符串path表示根，允许add / replace / copy / move覆盖根；remove根拒绝，因为输出仍须为有效JSON。from可引用根，但move到根的子路径拒绝；copy深复制，避免共享节点产生环。对象中名为`-`的属性正常按对象成员处理。
- 不提供test、JSONPath、通配数组、条件表达式、字符串脚本、跨请求状态或外部数据查询。value的规范化JSON最多8192 UTF-8字节，并受策略总32 KiB与深度16限制；这些是配置值限额，不提高正文限额。

**正文选择、媒体与编码：**

- requestBody三个字段均必填；methods为1–6项，从GET、POST、PUT、PATCH、DELETE、OPTIONS选取并去重；首次表单默认POST / PUT / PATCH。方法不在列表时只跳过请求正文方向，其他变换继续。HEAD不允许列入请求正文方向。
- responseBody三个字段均必填；statusCodes为1–32个互不重复的200–599整数，拒绝204 / 205 / 206 / 304这些不适用本映射的状态；默认[200]。其他上游状态的正文原样透传，只跳过响应正文方向，保留Header规则。
- 被选择方向要求application/json或具体application/*+json媒体类型，charset缺省或UTF-8；接受可选UTF-8 BOM，输出UTF-8无BOM。空正文、非法UTF-8、未配对Unicode代理字符、重复对象属性、尾随数据、JSON注释和超过32层的正文拒绝；JSON值不局限于对象，操作必须与实际节点类型一致。
- 支持identity、gzip、br的单层编码，编码缺省等于identity；拒绝其他编码或多层编码。输入编码字节、解码字节和每一步操作后 / 最终输出字节分别受maxBodyBytes限制，不能只靠Content-Length判断。解压错误与超限均终止，输出identity并重建相关Content Header。
- 当请求正文方向被当前方法选择或响应正文方向启用时，请求的WebSocket / Upgrade、gRPC及Accept包含text/event-stream均在转发前拒绝，不进行无界缓冲。实际被选择的上游SSE / gRPC / 非JSON或带Trailer声明 / 实际trailer响应拒绝，不能默默透传未映射敏感数据。未选择方向、未配置正文映射的路由继续现有传输行为。
- responseBody启用且HEAD响应状态被选择时，保留无正文响应并移除旧表示长度 / 验证器 / 编码元数据，不为求长度去读取上游正文；记录head_response旁路。未选择HEAD状态的正文元数据保持原行为。responseBody启用时Range、If-Range以及If-Match / If-None-Match / If-Modified-Since / If-Unmodified-Since请求在转发前拒绝，避免对原表示的206 / 304套用改写表示。
- 实际改写后，平台正文引擎重算Content-Length，设置UTF-8 JSON Content-Type，移除旧Content-Encoding、Content-MD5、Digest、Content-Digest、Repr-Digest、ETag、Last-Modified及Trailer；原缓存安全控制、Vary、Set-Cookie行为不放宽。用户Header规则仍不得变更这些受保护字段。
- 本映射不重新签名HTTP消息。被选择方向含Signature / Signature-Input时拒绝该方向，防止原签名被展示为改写正文的有效签名；其他自定义业务签名由接入配置者明确评估并处理，不宣称自动重签。

**预算与错误：**

- 每方向maxBodyBytes为1024–1048576整数，首次表单默认262144。部署端GatewayPolicies:Transform:MaxBodyBytes默认1048576，范围1024–1048576；控制平面保存 / 绑定 / 发布和每个网关激活均校验策略不超过各自部署上限，旧节点能力仍由2.3门禁处理。
- 部署端MaxConcurrentBodyTransforms默认16，范围1–64；同一节点一个请求仅持有一个正文租约，两个方向复用，至请求结束释放。配额耗尽立即返回受控503，不等待无界队列；无正文映射的流量不占该配额。该并发与字节限制不等同于整个进程的内存上限或第2包性能验收。
- 请求正文格式失败400、媒体 / 编码 / 流式 / 标准签名不支持415、输入 / 解码 / 中间 / 输出超限413、操作路径 / 类型不存在422、条件请求400。响应被选择后对应失败返回502，不能输出半份响应；本地正文配额不足503，路由超时504，客户端取消按现有取消语义处理。
- 错误code统一transform_rejected、附固定reason和Trace标识；不回显正文、字段值、动态路径、解压内容或输入样例。失败原因还包括transform_body_signature_unsupported；日志只保留固定操作序号与计数，不输出path / from原文。
- 请求缓冲分配位于认证和限流之后，不把无效正文的已发生限流扣减回退。正文租约、流和字节缓冲在成功、拒绝、取消、超时、配置切换或代理异常时全部释放，不能因响应慢或中途错误遗留配额。
- 读取、解码、操作和最终缓冲都绑定请求取消及既有路由总超时；响应处理也受选中物理尝试超时约束。正文缓冲不扩大POST等请求的自动重试条件。

**同引擎预览：**

- 新增管理端POST `/api/v1/policies/transform/preview`，输入显式scope、config字符串、direction（Request / Response）及sampleJson字符串。沿用会话、CSRF、policy.read与作用域校验，不保存策略或发起转发；预览响应Cache-Control: no-store。
- sampleJson解码后的UTF-8字节受所选方向maxBodyBytes及部署上限限制；使用该方向相同操作、Unicode / 重复字段 / 数字精度 / 深度规则。输出outputJson字符串及固定操作计数，避免经JS浮点解析后丢失大数精度。
- 预览只检验JSON映射，明确标注不包含真实HTTP媒体、压缩、请求方法 / 响应状态匹配、网关执行、后端连通、审批或ACK。样例输入 / 输出仅内存展示，不写日志、审计详情、localStorage或公开报告；错误只关联操作序号，不回显正文。
- 预览端点沿用已有请求大小限制并增加本端点有限预算，解码样例先受方向限额约束；使用独立有界执行配额和取消，配额不足503，不创建无界任务队列。原API字段 / Schema / 示例不会由映射自动改写，发布Review提示操作者核对对外契约；冻结候选仍使用所选API版本和策略修订。

## 4. 执行链、缓存、重试与资源生命周期

全局可信代理和内部Header信任边界保持。业务代理链建立单一运行代际 / 原路由上下文租约，然后按IP → CORS → 原API认证与授权 → Header / 路径 / Query计划校验及响应钩子 → 现有限流 → JSON请求正文有界映射 → 缓存 / 熔断 / 重试 / 加权后端选择与转发执行。正文缓冲在认证和限流之后分配；预检不进入正文映射。仅将当前ApiKeyMiddleware持有的上下文租约提取为专用组件，其他认证业务不重写。

原路由决定ApiId / ApiVersionId、应用授权和部署序列；Transform不能通过改写路径取得其他API授权。预检、拒绝、错误、取消和正常结束均释放租约一次；在途请求的旧代际在新配置切换后继续有效直到租约释放。

为绑定CORS的每个原业务路由增加最多一个预检YARP条目，匹配相同路径 / 优先级、OPTIONS、Origin存在及请求方法Header中的原Methods，保留原运行路由、Cluster和代际元数据。请求方法Header使用区分大小写的HTTP方法匹配，不通过大小写折叠改变实际业务方法。总内部条目最多2倍业务路由数；快照仍记录业务路由，不将预检条目变成新API。同路径不同方法和真正业务OPTIONS不能产生歧义或被错误遮蔽，必须用真实路由匹配测试验证。

首次转发和每次物理重试使用同一变换计划、同一YARP变换实现，从原请求构造副本。禁止多次追加前缀或Header。现有自动重试仅适用于无正文GET / HEAD；JSON请求缓冲不扩大这一条件，POST / PUT / PATCH和带正文GET不会因此被重试。JSON响应仅映射最终选择发送的那次上游响应，已被RetryResponseTransformer抑制的中间响应不映射、不缓存。保留最多3次尝试、总超时及熔断探测语义。

响应Header变换使用统一响应输出阶段，覆盖代理成功 / 上游错误响应及缓存命中；网关自身拒绝和预检不作为上游响应进行变换。缓存命中只有响应Header规则实际执行时记录Applied；仅配置请求改写的缓存命中记录Bypass / cache_hit。

现有缓存键已包含环境、部署序列、原路由、API版本、Cluster与验证后身份。新发布 / 历史回滚自然隔离缓存代际；不复用旧部署序列的内容。

绑定CORS时当前规范化Origin（包括无Origin的独立标记）加入内部缓存键，即使allowedOrigins为`*`也隔离不同Origin。最终Access-Control-*逐请求生成，不纳入缓存存储；不能复用首个Origin的授权结果。

缓存资格判断在该组合中接受Origin Vary，因为内部键已覆盖此维度；区分上游已有Vary与平台追加Vary，保留完整输出。未绑定CORS时Origin Vary继续遵守原部署允许列表；其他未被配置允许的Vary继续旁路。不得删除上游真实Vary提升命中率。

用户配置的Header Transform仍禁止缓存控制 / 验证器字段。正文引擎在实际改写后负责重算长度、移除旧正文验证器及编码 / 摘要，保留其他安全缓存语义。缓存只存映射完成的最终表示；Hit不再次进行JSON操作，其他业务Header在统一输出阶段生成。失败、部分或未完成映射不得进入缓存。Transform计划失败不能从缓存返回绕过失败的成功响应。

## 5. 快照、审批、节点门禁与跨环境

- 当最终快照存在启用并绑定的新三类策略时编译为2.3；来源包括本次候选和未选择API保留的基线配置。停用或未引用的新策略不触发升级，旧类型维持原协议选择。
- 新网关声明2.0 / 2.1 / 2.2 / 2.3，继续限制声明列表最多4项、绑定当前InstanceId。历史2.0 / 2.1 / 2.2的语义、严格解析、原字节和Hash保留。
- 2.3支持现有JWT / retry / cache及新三类，严格检查源PolicyId、修订、规范化配置内容身份、绑定与折叠认证 / 超时一致性。旧协议不得携带新三类，不把旧版本限制统一删除。
- 候选创建、提交、真正发布、重试发布、回滚和ACK均按实际目标协议检查节点能力；原所有已启用目标节点、在线、注册身份和MinimumNodes规则不变。旧2.2节点不能成功ACK 2.3，不缩减目标节点绕过门禁。
- 新策略保存和绑定只改变工作配置；冻结候选后继续编辑策略不得污染冻结内容。审批角色和步骤沿用目标环境流程。
- 保持16 MiB快照、10000业务路由和40000运行策略上限，不删除既有索引。历史2.3回滚读取原历史载荷，以新的部署序列下发，保留已有双节点ACK、失败恢复和在途租约语义。
- 制品晋级复用ArtifactPolicyTemplates：CORS的allowedOrigins、IP的ranges作为显式目标环境参数，其他行为冻结；Transform的Header / 路径 / Query及两个方向正文规则整体冻结。原共享策略不可被晋级悄然修改。
- 目标环境参数必须明确填写或由用户明确选择目标策略，不默认复制源Origin / CIDR；继续校验目标作用域、资源修订、私有策略绑定、审批和节点能力。

## 6. 观测与界面

每请求每类型最多一个决策，总容量由5扩展到8；timeout保持现有超时观测，不重复成为决策类型。早期拒绝未进入后续阶段时不伪造后续策略执行记录。源PolicyId和源修订继续表示实际冻结版本。

| 类型 | 决策 | 允许的原因 |
| --- | --- | --- |
| cors | Allowed / Preflight | 无 |
| cors | Bypass | no_origin |
| cors | Rejected | cors_origin_rejected / cors_method_rejected / cors_headers_rejected / cors_request_invalid |
| ip_filter | Allowed | 无 |
| ip_filter | Rejected | ip_not_allowed / ip_denied / client_ip_unavailable |
| transform | Applied | 无 |
| transform | Rejected | transform_path_mismatch / transform_path_invalid / transform_budget_exceeded / transform_json_invalid / transform_json_operation_failed / transform_body_too_large / transform_body_media_unsupported / transform_body_encoding_unsupported / transform_body_condition_unsupported / transform_body_stream_unsupported / transform_body_trailer_unsupported / transform_body_signature_unsupported / transform_body_busy |
| transform | Bypass | cache_hit / no_upstream_response / hop_by_hop_protected / body_method_not_selected / body_status_not_selected / head_response |

Transform只要一个后端尝试实际应用变换、一个最终响应实际完成映射，或一个缓存响应实际应用响应Header规则，就记录Applied；不能仅因计划创建成功就宣称已发送。正文失败优先记录Rejected，不被此前Header已应用覆盖。缓存Hit使用已映射正文，若本次无其他变换实际执行则Bypass / cache_hit。重试次数继续使用现有retry和物理尝试观测，不增加同类型决策条数。

响应映射失败必须在客户端正文开始前形成完整平台错误；网关最终状态和物理尝试的ProxyError不得被上游原200覆盖，也不能伪装为连接故障。retry已进入执行链时以受控Bypass / response_transform_rejected表示终止，不因本地映射错误增加后端尝试；其余实际上游重试 / 传输错误和现有熔断规则保持。无后端尝试的请求格式 / 配额拒绝不生成假的代理尝试。

同步扩展Gateway收集 / 输出、控制平面投影、日志 / Trace过滤、指标合法值及控制台。理由为固定枚举；不输出策略配置、完整IP、Origin、动态路径 / 查询值或Header值作为策略标签。旧日志 / Trace仍可读取，新理由不能与旧类型混用。

控制台保持1440px桌面企业布局，新增三个类型页签及结构化表单。包含明确字段帮助、逐项错误、服务端校验、规范化Review、复制、组织 / 项目范围、ETag冲突、只读态、路由绑定、运行修订差异和提交发布。静态改写示例和JSON输入 / 输出预览标为预览，不显示成实际转发测试成功。正文编辑器提供有序字段映射、路径与值类型、请求方法 / 响应状态选择以及大小限制，错误关联具体操作序号，不提供原正文持久化或秘密示例收集。

发布Review展示通配来源、credentials、全网CIDR、路径改写效果和目标节点2.3能力；晋级Review展示目标Origin / CIDR差异。保存成功不显示运行生效，生效以原发布状态及双节点ACK为准。导航、权限和作用域切换的异步隔离保持现有规则。

## 7. 验收矩阵与交付边界

| 编号 | 必须验收的内容 | 真实证据 |
| --- | --- | --- |
| P3-01 | 三类配置正反例、规范化幂等、重复 / 未知字段、大小 / 数量边界、9类组合 | 域回归及严格错误码 |
| P3-02 | 创建 / 更新 / 校验 / 复制 / 引用 / 绑定，跨组织项目拒绝、只读、ETag、幂等 | 管理接口、真实数据库与权限回归 |
| P3-03 | GET-only预检、同路径多方法、业务OPTIONS、Origin拒绝、credentials、无Origin、401 / 403 / 429 / 上游错误 | 实际HTTP，预检后端调用计数0 |
| P3-04 | IPv4 / IPv6 / mapped、CIDR边界、未知IP、伪造XFF、可信 / 不可信代理；三种认证组合 | 实际HTTP及既有信任边界安全回归 |
| P3-05 | Header Set / Remove、前缀 / Query / 编码 / 危险Header、原API授权；JSON两方向add / replace / remove / copy / move、根 / 数组 / null / 大数 / 原子失败，同引擎预览 / 权限 | 真实YARP + 回显后端 + 管理预览接口 |
| P3-06 | 两Origin缓存、JSON最终表示只映射一次、gzip / br、上限 / 深度 / 解压超限、HEAD / 条件 / 流式 / trailers、每次重试副本及仅最终响应映射、熔断 / 超时 / 取消、正文并发配额 | 真实缓存、后端尝试计数、编码字节 / 输出与租约回归 |
| P3-07 | 冻结后工作修订变化，审批、2.3旧节点阻断、当前实例ACK、失败发布保留原运行配置 | 控制平面 + 两个独立网关进程 |
| P3-08 | 历史2.0 / 2.1 / 2.2加载与回滚，2.3发布 / 历史回滚，在途旧请求完成 | 原Hash、两个节点版本 / 序列 / ACK |
| P3-09 | 跨环境Origin / CIDR显式映射、Transform冻结、目标权限 / 审批与失败重试 | 独立TEST / PROD制品晋级 |
| P3-10 | 新决策过滤、受控原因、无秘密标签、旧观测可读 | 实际日志 / Trace / 指标 |
| P3-11 | 1440px表单、错误、只读、冲突、修订Review、晋级映射、页面导航 | 控制台构建、交互回归和截图QA |
| P3-12 | 既有完整回归、EF模型差异、主工作区与4192数据保护、独立审查 | 固定源版本、命令日志与保护收据 |

每项在交付台账关联实现文件、固定提交或审阅补丁、执行命令、结果、运行owner、错误 / 复验及证据路径。缺少真实双节点、真实缓存、观测或回滚时必须标未验收，不能以Mock或单节点代替完整交付。

本规格不声称任何上述新增功能已完成。用户审阅本扩大范围规格后，再按writing-plans重写实施计划；沿用当前会话逐项实现与测试、完成后统一审查的已选偏好。精确源码提交和发布部署按届时用户明确授权执行，保留现有数据及本地改动。
