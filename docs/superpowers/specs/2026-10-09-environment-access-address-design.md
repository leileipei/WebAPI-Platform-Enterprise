# 环境访问地址设计：首期 A 方案

日期：2026-10-09。核对源码：`4ceda71ef2b8c5eabb42a882de6734fc3422a530`，另有既存本地修改。

状态：用户先回复“都安装推荐继续”，按“都按推荐继续”理解采用地址元数据方案；随后回复“确认设计”，本正式规格已批准，进入实施计划审阅。产品实现、部署和验收尚未执行。与同日发布晋级规格配套，先完成本规格，再建设晋级闭环。

## 1. 目标和成功条件

管理员在“组织与项目 → 环境管理 → 新建/编辑环境 → 环境访问地址”维护各环境 Gateway 入口。平台从真实 Route 生成客户端地址、环境文档和无凭证调用示例；发布详情保留发布时的入口上下文。DEV、TEST、UAT、PROD 是示例编码，环境来自实际项目数据。

首期保存的是地址元数据。DNS、TLS、LB、Gateway 监听与前缀剥离由部署配置负责；本功能不安装证书、配置代理、不发起连通性请求、不修改 Gateway Snapshot，也不建设在线调试代理。地址配置成功与入口实际可用分别表达。

成功条件：合法地址可保存并持久读取；空配置不生成误导链接；前缀拼接一致；跨环境不会沿用上一环境地址；公开与内网地址按权限投影；旧环境及现有发布闭环保留。

## 2. 已核对的实现边界

- `EnvironmentRecord` 和 `GovernanceContracts` 当前均无访问地址字段。
- `GovernanceService.SaveEnvironmentAsync` 已实施 environment.write、实际 Scope、RevisionTag 和审计；当前环境保存未使用发布命令的 IdempotentCommandExecutor，不能宣称环境保存已有幂等回执。
- `Governance.tsx` 已有环境编辑入口，可延伸专用地址区域。
- `ApiDetail.tsx` 展示保存的 OpenAPI 内容，当前没有完整在线调用调试流程。
- Runtime Route 的 Path 是 Gateway 实际匹配路径；本规格不调整它。

## 3. 数据模型

在 environments 增加以下字段：

| 数据库字段 | 类型与默认 | API 字段 | 语义 |
| --- | --- | --- | --- |
| gateway_public_url | varchar(2048)，可空，默认 NULL | gatewayPublicUrl | 客户端对外 Gateway Origin |
| gateway_internal_url | varchar(2048)，可空，默认 NULL | gatewayInternalUrl | 内网 Gateway Origin；不是业务上游地址 |
| base_path | varchar(512)，非空，默认 `/` | basePath | 外部入口前缀，由反向代理剥离 |
| access_address_revision | bigint，非空，默认 1 | accessAddressRevision | 仅上述三项规范化值实际变化时递增 |

保留环境 Revision：任何实际保存的环境变更继续递增，用于 If-Match。accessAddressRevision 用于区分入口变化与排序、名称等变化。清空地址使用 JSON null；空字符串规范化为 null。

新增 release_access_contexts：一条 Release 最多一个上下文，release_id 唯一外键；包含 environment_id、access_address_revision、public_origin、internal_origin、base_path、captured_at。记录不可原地更新，读取内网地址须额外授权。旧发布没有上下文，不回填为当前地址；页面显示“历史未记录”。

本规格不向原候选正文或旧 Snapshot 增加字段，避免改变历史候选摘要及运行快照协议。

## 4. 地址与前缀规则

### 4.1 Origin

地址使用标准 URI 解析器，必须为绝对 HTTP/HTTPS Origin，主机非空、端口合法；禁止 userinfo、query、fragment 和除根路径以外的路径。允许用户输入结尾 `/`，保存时删除。域名规范化为小写 ASCII 主机，IPv6 使用方括号，默认端口规范化移除。拒绝控制字符、反斜杠和解析歧义。

生产环境的 public URL 如已配置必须 HTTPS；非生产环境 public URL 允许 HTTP 以兼容本机部署。internal URL 允许 HTTP/HTTPS，代表真实内网部署条件。旧空配置不影响旧发布。变更 IsProduction 时同步验证已配置的 public URL。

地址仅作展示和生成文本，保存不解析 DNS、不连接 URL。后续若增加服务端请求，须独立建设目标地址、DNS/连接绑定和 TLS 策略；不能将本字段当成现有上游允许列表。

### 4.2 外部前缀

basePath 为空规范化为 `/`；必须以 `/` 开头，非根前缀去除结尾 `/`。首期允许 ASCII 字母、数字、`-`、`_`、`.`、`~` 组成路径段；拒绝空段、`.` 或 `..` 段、百分号、反斜杠、查询和片段字符。超长输入拒绝，不截断。

前缀是外部代理增加并剥离的路径。Route.Path 仍是 Gateway 收到的路径，不靠“路径恰好以相同前缀开头”猜测或去重。代理若不剥离，管理员应使用 `/` 并把完整匹配路径配置在 Route 中。

唯一拼接规则：Origin +（basePath 为 `/` 时为空，否则为 basePath）+ Route.Path。使用受控拼接函数，不使用会把 `/route` 解析为覆盖 basePath 的通用相对 URL 合并。

示例：Origin=`https://api-test.company.com`、basePath=`/gateway`、Route.Path=`/mes/orders/{id}`，输出 `https://api-test.company.com/gateway/mes/orders/{id}`；代理向 Gateway 转交 `/mes/orders/{id}`。

## 5. 服务与接口

| 单元 | 职责 |
| --- | --- |
| EnvironmentAccessAddressValidator | 规范化并验证 Origin、basePath 与生产标识；不联网 |
| ClientApiAddressBuilder | 拼接路径模板、按参数生成调用示例；前后端相同规范 |
| EnvironmentApiDocumentService | 从授权环境的实际 Route 和 API 契约生成环境文档 |
| ReleaseAccessContextService | 在执行启动事务中冻结入口上下文；不影响运行编译 |

环境 POST/PUT 保持原路径和权限，新字段按属性是否出现处理：旧调用方未提交新字段时保留原值；显式 null 清空 URL；新建未提供时采用默认值。不能仅用可空 DTO 区分遗漏与清空，必须使用字段存在性表达。

环境查询、Scope Tree 和列表只投影 public URL、basePath、accessAddressRevision。internal URL 仅在环境详情且调用者具备 environment.write 和该环境 read_write Scope 时返回；普通读取不返回该属性。列表和计数不因内网字段权限变化而扩大访问范围。

新增只读 GET `/api/v1/environments/{environmentId}/apis/{apiId}/access-addresses?versionId=...`：要求实际环境可读、API/version 可读及同组织项目；从该环境对应 Route 生成每条路由的 method、pathTemplate、publicTemplate、runningConfigVersion 和 configured 状态。查询 working 与 running 必须显式分开，参数 `view=working|running`，默认 running；running 从已存快照解析路由，工作 Route 不冒充已运行入口。路由占位符展示为模板，不直接执行。内网模板采用相同额外投影权限。

新增 GET `/api/v1/environments/{environmentId}/apis/{apiId}/openapi?versionId=...&view=working|running`：具备 environment 可读及 api.read、api.version.read、api.schema.read；默认 running。从已有规范化契约生成副本，以该环境 Origin+basePath 设置 servers，paths 使用实际所选视图的 Route，不更改存储的 openapiSource/openapiDocument、契约 Revision、Artifact 或比较结果。无入口地址返回 409/access_address_unconfigured；不能伪造默认 localhost 或当前控制台域名。

本期文档仅支持实际 Route 的无歧义映射：同一方法路径冲突、契约与 Route 无法匹配时返回明确错误。不能用不完整文档冒充完整运行接口。响应 no-store。调用示例不含 API Key、Authorization、Cookie 或内部 Origin；路径参数值按段编码，不把值中的 `/` 当作分隔符。

## 6. 页面与保存流程

环境编辑页保留全部原字段，增加公开入口、内网入口（仅允许编辑者）、外部路径前缀及解释。编辑的是已选环境，不在弹窗中切换环境并覆盖草稿。

页面提供示例路径预览，明确“示例路径”；API 详情、路由页面和发布详情才使用真实 Route 生成访问地址。多 Route 分行展示方法、模板及运行/工作标识。配置为空时显示“尚未配置”，复制按钮禁用。

保存由服务器完成全部验证，沿用现有 If-Match/412、会话和 CSRF、授权及审计。地址变更不自动发布或执行请求。普通冲突保留草稿并允许比较最新值；撤权或会话失效清除受限内网数据。保存后显示“地址已配置，连通性未验证”，不显示 DNS/TLS 成功。

## 7. 发布与晋级的关系

普通新发布在 PublishCoordinator.StartAsync 获得环境锁后捕获当前入口上下文并创建不可变记录；重试返回原记录，不重新捕获。旧发布继续无上下文；回滚创建新的入口上下文，回滚的是运行 Snapshot，不恢复旧 DNS、LB 或环境地址。

历史详情分别显示“本次执行记录的地址”和“当前环境地址”，发生变化时明确标记。不能将后改地址展示为历史实际入口已经验证。

晋级规格中，目标 public URL 必须配置。审批绑定 accessAddressRevision；预检和业务验证引用该冻结上下文。执行前发现地址已变更则拒绝继续，重新预检、生成候选并审批。执行期间入口变化使旧验证结果不再适用于当前入口，交付状态进入需要重新验证的状态。历史结果保留。

## 8. 迁移、验证与交付

迁移增加列和上下文表，不改旧 API、Route、Destination、凭证、快照或部署序列。历史数据空地址可读；不从本机容器、节点地址或浏览器域名推断公开入口。新规格的列存在，不代表旧镜像能够无条件读写；升级与恢复在实施计划中固定源版本并验证。

验收矩阵：

| 层次 | 必须验证 |
| --- | --- |
| 规则 | HTTPS/HTTP、端口、IPv6、结尾斜杠、非法字符、路径段、长输入、参数编码与前缀拼接 |
| 数据库/API | 新旧请求字段存在性、清空与遗漏、空数据迁移、Revision/412、生产标识变化、跨组织项目拒绝、内网字段投影 |
| 文档/示例 | 实际 Route、working/running 区分、多路由、空地址、原始契约不变、示例无凭证、无联网副作用 |
| 发布 | 一次捕获、重试不改历史、旧记录未知、当前/历史差异、快照摘要与现有发布行为不受元数据影响 |
| Console | 环境编辑、草稿冲突、权限撤销、预览复制、1440/1280、键盘与错误提示 |
| 本机升级 | 固定源码构建，原账户/SSO/数据保留、双网关与旧业务回归、原实例入口说明和截图 |

文件自查关注字段遗漏兼容、前缀剥离语义、历史记录、内网投影和不修改契约。上述是验收要求，不是已执行测试结果。

## 9. 后续范围

多公开域名、多 Gateway Group 入口、真实连通性测试、在线调试代理、平台管理 DNS/TLS/LB 和 Gateway 前缀执行均需独立规格。本期不以隐藏开关提前包含这些能力。
