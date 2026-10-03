# WebAPI Enterprise V2 首期核心闭环设计基线

日期：2026-10-04。状态：用户已回复“是，确认”，设计基线已确认；逐任务实施计划待审阅。

## 1. 已确认目标与设计依据

用户选择首期 A：交付可运行核心闭环。闭环为真实登录与权限、API / 版本 / 路由 / 后端配置、审批、Snapshot、两节点网关热更新、实际后端调用、回滚与审计。沿用方案1的高密度企业界面及40页业务结构。

上游依据为《WebAPI_Platform_Enterprise_V2_产品原型与数据架构设计.docx》第1、2、5、6、8、9、10章，以及 V1.4 原型。源文档中的内容是设计输入；本文明确首期实现决策，原型测试结果不能替代服务端和真实网关验收。

以下是本次建议，尚非用户提供的部署事实：以 Linux + Docker Compose 完成首期测试交付；先用数据库中的本地账号验证真实身份与审批，再接企业 OIDC；不假定存在可复用单机版源码或数据。若用户补充现有环境，调整部署适配，不改变已选核心闭环。

## 2. 路径比较与选择建议

| 路径 | 收益 | 代价 |
|---|---|---|
| 纵向闭环，推荐 | 每个阶段可通过真实请求验证，早发现控制面与网关协议问题 | 首期部分页面尚未接真实接口，必须标示范围 |
| 先完整管理后台 | 可较快替换全部页面模拟数据 | 热更新、配置编译与失败恢复风险发现较晚 |
| 先网关引擎 | 较早验证代理与运行性能 | 用户权限、审批与产品页面仍不能连成闭环 |

选择建议：纵向闭环。整体采用模块化单体 Control Plane、独立 Worker 和独立 Gateway；首期不拆业务微服务，不引入 Kafka、计费、Marketplace 或跨地域双活。

## 3. 首期范围与延期范围

首期包含：

1. 数据库迁移、显式初始化、真实本地登录、用户与角色、组织 / 项目 / 环境 Scope、服务端权限。
2. API 元数据、版本、Route、Cluster、Destination；OpenAPI JSON 当前 Operation 导入、参数及 Schema 保存；基础超时与 API Key 认证绑定。
3. 应用、凭证创建、按 API + Environment 授权；Secret 一次性显示且不明文存储。
4. 固定顺序的可配置两级生产审批，冻结发布项、编译校验、Snapshot、发布、节点 ACK、独立回滚。
5. 两个真实 Gateway、Redis 通知与补偿拉取、LKG、配置切换、节点心跳、真实代理请求。
6. 事务审计、TraceId、健康检查、开发测试后端、故障和恢复验收。
7. 现有视觉系统下的核心页面真实接口接入。40页原型保留作为产品基线，未接接口页面明确属于后续范围。

后续完成：企业 OIDC/JWT 业务认证、完整限流 / 重试 / 熔断 / Transform 策略、凭证轮换完整体验、完整 YAML 与 OpenAPI 兼容性差异引擎、指标日志 Trace 平台接入、告警渠道、Kubernetes 及离线交付包、容量压测和生产安全验收。首期不宣称这些已完成。

## 4. 工程与部署边界

工程放在独立 `enterprise/`，保留 `prototype/` 及其用户数据、启动入口和交付证据。模块建议：

- `WebApi.Domain`：业务状态、权限规则、值对象、发布校验。
- `WebApi.Contracts`：管理接口 DTO、Runtime Snapshot 与节点协议。
- `WebApi.Infrastructure`：EF Core、PostgreSQL、Redis、Outbox、审计、密钥哈希。
- `WebApi.ControlPlane`：管理 HTTP API、认证会话及授权。
- `WebApi.Worker`：配置编译、发布通知、ACK 汇总和重试协调。
- `WebApi.Gateway`：YARP、Snapshot Provider、运行认证、LKG、心跳。
- `WebApi.TestBackend`：只在开发 / 验收 Compose 中运行的可识别测试后端。
- `console/`：复用已批准的界面设计，逐页换为真实接口。
- `tests/`：领域、PostgreSQL 集成、网关集成及完整 E2E。

.NET 10 LTS / ASP.NET Core / EF Core，YARP 2.3.0 作为起始兼容基线；PostgreSQL 17作为首期数据库，Redis 7系列保留通知与运行状态用途。正式依赖选择在第一项编译任务中验证兼容性，冻结具体补丁、锁文件与镜像 digest，不使用未固定的 latest 标签交付。

本机已确认 Docker Engine 可用；现有 Linux ARM64 SDK 镜像运行成功，SDK10.0.302 / Runtime10.0.10。该缓存镜像仅证明容器编译路径可行，交付需更新到当时支持的安全补丁。测试目标同时考虑 Linux AMD64，不能用 ARM64 测试替代目标服务器验收。

Compose 使用专用项目名、网络、数据库和卷；不复用其他项目数据库或容器。网关分别有独立 LKG 持久卷。开发宿主端口仅绑定 loopback，正式网络、TLS和域名依用户环境配置。迁移通过独立步骤执行，不由每个网关启动时执行。

## 5. 数据模型与约束

遵循源文档的规范化 PostgreSQL 模型，JSONB仅用于异构策略、OpenAPI、Schema和不可变快照。首期落实组织、项目、环境、API分组、API、版本、路由、参数、Schema、Cluster、Destination、策略绑定、应用、凭证、授权、用户、角色、权限、Scope、审批、发布项、配置版本、快照、节点事件及审计。

关键规则：

- API Version 属于 API，不能增加环境归属来代替 Route 的 `environment_id`。相同版本可部署多个环境。
- Version 内容首次发布后不可直接修改；新内容建立新版本。参数及 Schema 通过 `api_version_id` 关联。
- 管理配置是工作区事实；运行事实由该环境已发布的不可变 Snapshot 提供。编辑管理 Route 不立即改变数据面。
- API Code 在项目内唯一；版本在 API 内唯一；Cluster在环境内唯一；授权在应用 / API / 环境内唯一。
- 路由冲突按环境、每个 HTTP 方法及规范化路径检测，参数名不同但路径同形也冲突；静态路径优先级明确。路由方法集合的冲突不能仅依赖数组唯一约束。
- 外键之外校验组织 / 项目 / 环境一致性，禁止把其他项目的 Cluster 或 Version 绑定进 Route。
- 核心可写对象增加乐观并发版本；旧版本更新返回412，不覆盖新编辑结果。
- 所有关键写入及审计在同一事务；发布通知使用事务 Outbox，不能事务成功却丢失通知。

相对源表的首期补充字段 / 表：环境 desired config pointer、发布单调递增序号、发布冻结节点集合、节点 ACK 明细、Outbox、幂等命令记录、原始 OpenAPI 文本及格式、Schema / 参数示例、必要的并发版本。所有补充经迁移及约束测试落实，不偷偷写进原型模型。

## 6. 登录与授权

首期采用真实本地账号及 ASP.NET Core 安全哈希，浏览器使用 HttpOnly 会话 Cookie；写操作验证防伪令牌，同源访问，不把凭证放入 URL 或 localStorage。默认拒绝访问。

授权在服务端同时检查功能权限、用户Scope、实际资源归属及状态 / 环境规则。前端隐藏按钮只提供体验，不能代替授权。明确组织级 Scope 是否含项目、项目级 Scope 是否含环境，按匹配授权集合计算，不跨组织继承。

停用用户、修改角色或撤销Scope在后续请求生效；审批 / 发布 / 回滚实际写入时重新检查，不能依赖页面打开时的权限。首期采用数据库授权查询以保证一致性，缓存如加入必须有失效机制和验证。

初始化不产生固定通用密码；管理员由显式初始化提供的Secret建立。验收账号包含申请人、API审批人、安全审批人及只读用户，各账号独立会话。生产申请人不得审批本人申请，同一人不得满足两个审批步骤；有权限的平台管理员也不能绕过这一首期规则。

网关的API Key格式为 `accessKey.secret`，通过 `X-API-Key` Header传入。Secret由密码学安全随机数生成器产生32字节随机值并Base64Url编码，数据库和运行态保存其SHA-256摘要，比较采用恒定时间实现；这是高熵随机凭证的校验规则，不能用于用户密码哈希。日志脱敏。每次请求检查有效期、应用状态及授权Scope。首期凭证和授权发布变化通过环境Snapshot生效，界面区分管理修改与已生效状态；紧急即时撤销作为独立安全能力后续设计，不能声称当前已支持。

## 7. 管理接口约定

管理接口前缀 `/api/v1`；内部节点接口前缀 `/internal/v1`，不向管理浏览器开放。标识UUID、时间UTC、列表分页；错误采用Problem Details，含业务code及traceId。未认证401，权限不足403；不允许用户读取的单条资源返回404；状态 / 资源冲突409，并发更新412，结构校验422。

写操作需要Idempotency-Key的范围为导入、创建发布、提交 / 审批 / 发布 / 回滚等可能重试的命令。幂等键绑定身份、Scope、操作及规范化请求摘要；相同请求返回原结果，相同键不同内容409；事务提交与幂等结果持久化保持一致。

| 领域 | 主要接口 |
|---|---|
| 会话 | GET /auth/csrf；POST /auth/login、/auth/logout；GET /auth/me |
| Scope基础 | /organizations；/organizations/{id}/projects；/projects/{id}/environments |
| 治理 | /users；/roles；/roles/{id}/permissions；/users/{id}/scopes |
| API | /projects/{id}/apis；/apis/{id}；/apis/{id}/versions；/versions/{id}/parameters；/versions/{id}/schemas |
| 路由与后端 | /environments/{id}/routes；/routes/{id}；/environments/{id}/clusters；/clusters/{id}/destinations |
| 导入 | POST /openapi/import-preview；POST /openapi/import-commit；commit重新校验源文和冲突，不能信任前端预览结果 |
| 应用 | /applications；/applications/{id}/credentials；/applications/{id}/permissions |
| 发布 | POST /environments/{id}/releases；GET /releases/{id}；POST /releases/{id}/submit、/approve、/reject、/publish、/rollback |
| 网关治理 | GET /environments/{id}/gateway-nodes；GET /gateway-nodes/{id}/events |
| 审计 | GET /audit-logs，允许的范围和Secret脱敏在服务端执行 |

详情接口必须明确工作区版本、实际运行configVersion与pendingRelease，不能把目录中的最后编辑值显示成已生效值。完整OpenAPI契约与每个DTO字段属于下一阶段实现计划产物，本文不是已可运行的API定义。

## 8. 发布与回滚状态机

发布：Draft → WaitingApproval → Ready → Building → Publishing → Succeeded；支持Rejected、Cancelled、Failed及明确的失败阶段。不允许客户端任意设置状态。

提交评审时冻结候选资源及revision、审批策略、目标环境与基准configVersion。之后编辑不能改变已提交内容；取消 / 拒绝后允许重建候选。审批同一节点重复请求幂等，只有当前步骤可操作。

每环境同一时间只允许一条Building / Publishing记录。数据库事务锁与基准版本比较共同防止并发发布；旧基线冲突409，不能覆盖后续发布。Worker构建冻结候选与基线快照，校验全部引用、路径、Destination和凭证授权，持久化payload / hash后才启动下发。

启动下发时冻结两个已注册的目标节点及instanceId，记录目标configVersion、hash及新的deploymentSequence。节点离线不自动从集合剔除；运维处理必须可审计。节点ACK必须匹配发布、环境、实例、版本、hash、sequence，拒绝过期或伪造确认。

分布式语义：每个节点独立原子切换，节点切换后可能已服务新流量；部分ACK期间可能存在不同版本，状态保持Publishing并展示逐节点状态。全部目标节点确认后才记为Succeeded，这不等于所有节点在同一瞬间切换。首期不承诺跨节点全局原子切换。

超过可配置截止时间标Failed并保留已应用节点的真实状态；不能自动假定已恢复旧流量。失败后的重试只重传当前期望版本并查询实际状态，或者建立独立回滚记录。延迟ACK不能完成已失败 / 取消的发布；恢复需要显式协调命令。首次下发无LKG的节点校验失败则不接业务流量。

回滚创建新Release，复制并引用历史不可变Snapshot，按生产审批流程执行；configVersion可回到历史版本，但deploymentSequence继续递增。节点只接受更高sequence的目标，避免旧通知把回滚后的配置再切回去。全部目标ACK后回滚成功，原成功记录标RolledBack并保留关联。

## 9. Runtime Snapshot与节点协议

建议payload字段：schemaVersion、environmentId、configVersion、generatedAt、routes、clusters、policies、applications。运行payload不包含审批记录、管理UI字段、明文Secret或数据库连接。

Envelope包含deploymentSequence、releaseId、configVersion、payloadHash、payload字节长度。`payloadHash`计算实际存储UTF-8 payload字节，hash放在Envelope，避免包含自身的哈希循环；历史payload必须原样保留。Redis保存精确字节，JSONB可保存查询副本但不能重新序列化后假定hash不变。

内部接口：

- POST /nodes/register：使用节点专用初始化身份，绑定环境和instanceId；不允许任意匿名注册。
- POST /nodes/{id}/heartbeat：携带instanceId、当前version / sequence及状态。
- GET /environments/{id}/desired-config：节点专用身份读取本环境目标Envelope。
- POST /nodes/{id}/ack：携带releaseId / version / hash / sequence / appliedAt及结果。

首期节点身份由专用配置注入，不复用管理用户会话，不在代码或日志内保存共享固定Secret。后续可升级节点证书机制。

网关请求路径只访问当前内存Snapshot及必要运行状态，不访问PostgreSQL，也不逐请求调用Control Plane。Redis Pub/Sub仅是通知，Gateway定时核对desired pointer以补偿消息丢失。Control Plane / Worker协调PostgreSQL事实及Redis缓存，Redis不能替代主数据库。

切换顺序：验证Envelope / Hash / Schema / 环境 → 构建完整Runtime配置 → 原子写候选LKG文件 → 单节点切换完整Route / Cluster / Policy / Authentication generation → ACK。路由与认证不得分别交换造成混用；每个请求绑定单一generation，旧请求允许完成。LKG写入失败不确认成功。

重启优先验证本地LKG；无可用LKG且控制面 / Redis不可用时不进入业务Ready。有LKG时控制面或Redis故障不妨碍已有允许流量；无法取得更新则显示版本滞后，不能宣称撤销策略已同步。两节点分别使用独立目录及持久卷。

## 10. 首期阶段与验收

| 阶段 | 真实交付 | 必须验证 |
|---|---|---|
| M1 数据与身份 | 工程、迁移、本地登录、角色、Scope、审计 | PostgreSQL约束、独立账号、跨组织拒绝、停用生效、并发冲突 |
| M2 API与后端配置 | 目录、版本、导入、Route、Cluster、Destination、应用基础 | 导入事务、同形路由、跨环境引用、版本不可变、编辑不影响运行配置 |
| M3 发布与双网关 | 编译器、审批、Snapshot、Outbox、Redis、ACK、YARP | 真实请求、两级不同人员审批、部分ACK、失联、重试与幂等 |
| M4 回滚与界面联调 | 核心页面接入、API Key授权、回滚、LKG | 未授权401/403、变更与回滚后真实请求、重启恢复、控制面故障继续代理 |

验收必须留存真实HTTP证据、数据库 / 配置版本、两节点实际版本和结果。health200、编译成功、单元测试或模拟ACK单独都不代表闭环完成。

必测负面情况：申请人自批、同人重复占审批步骤、冻结后改草稿、同环境并发发布、未知节点ACK、旧sequence、坏hash、跨环境Snapshot、Redis通知丢失、控制面停机、节点重启、持久卷不可写、重复回滚、凭证到期及跨API调用。性能5k RPS/4C8G属于源文档后续压测目标，首期不凭本机演示认定已达到。

## 11. 前端接入与交付边界

保留V1.4作为设计参考，正式console建立接口层、真实会话、服务端权限响应、请求失败 / 并发冲突反馈及运行版本展示。首期不会在真实console中保留模拟批准或模拟ACK按钮；未接接口页面明确标注未接入或暂不开放。

需接入的核心页面来自原40页清单：登录、组织 / 项目 / 环境、API目录 / 向导 / 导入 / 详情 / 版本 / Route / Schema、Cluster / Destination、应用 / 凭证 / 授权、发布 / 审批 / Snapshot / 节点、用户 / 角色 / 权限 / Scope / 审计。后续页面设计继续沿用既定Shell与Tokens。

## 12. 仍需用户提供的外部信息

正在询问首期部署环境。企业SSO信息、既有系统迁移需求、试点真实后端、服务器规格和正式域名 / TLS可以在相应集成阶段提供；未提供前仅以隔离的本机测试环境和TestBackend验证，不连接未知企业服务。

本文审阅通过后再编写逐任务实施计划，随后开始工程与迁移实现。用户可直接修改上述首期范围、审批职责或部署假设。

## 13. 技术版本参考

2026-10-04查阅：[Microsoft .NET支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)；[YARP官方NuGet包](https://www.nuget.org/packages/Yarp.ReverseProxy/)；[PostgreSQL支持策略](https://www.postgresql.org/support/versioning/)。依赖组合仍须restore / build / integration验证；官方支持信息不能替代本项目兼容性验证。
