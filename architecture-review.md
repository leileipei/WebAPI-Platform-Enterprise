# WebAPI Platform Enterprise 架构评审报告

> **2026-11-10 复核说明**：下文保留 2026-11-07 的评审基线及后续局部补记，不作为当前交付状态的唯一依据。固定主干 `7626115` 已包含 G01–G13 合并，G14 台账两项补验分别记录 7/7 与 4/4 通过；旧文“未合并”“G14 未闭环”已被这些后续证据更新。安全检查点/租约准入仍未接线，当前凭证撤销依赖再次审批发布，合并不能代表即时撤销已上线。本地CI两批配置已提交为 `536efe1`、`8b75524` 并完成对应本机验证，远程主干仍无这些工作流、Actions记录仍为0。另纠正 §6：“.NET 10 非 LTS”不准确，.NET 10 为 LTS；SDK 与补丁更新仍需固定版本复验。[Microsoft 支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)。本次状态与证据入口见[当前项目状态](docs/deployment/current-project-status.md)。

| 项 | 值 |
|---|---|
| 评审对象 | `leileipei/WebAPI-Platform-Enterprise`(main @ `369139d`,273 commits)+ 凭证治理 G01–G13 工作树变更(未合并) |
| 评审日期 | 2026-11-07 |
| 评审方法 | 源码通读(核心机制逐文件)+ 仓库文档交叉验证 + 商用网关(Kong/Higress)能力对照 |
| 代码规模 | C# 141,938 行(904 文件,含测试);测试 20,596 行(233 测试文件);React 控制台 1,566 行 |
| 维护形态 | 单一维护者,公开仓库 0 star / 0 fork,无 CI 流水线 |

---

## 1. 执行摘要

这是一个自研的**企业级 API 管理平台**:.NET 10 控制面 + YARP 数据面(双节点)+ PostgreSQL + Redis + React 管理控制台。功能覆盖契约管理、审批发布、流量策略、SSO、凭证治理与可观测性,定位为 Kong / Higress 一类的控制面+网关一体化产品。

**核心结论:**

1. **设计深度显著超出其交付形态**。三条核心链路(发布协调、凭证安全账本、契约兼容性证明)都采用了"不可变事实 + 哈希/签名绑定 + 每阶段重验 + 诚实能力边界"的同一方法论,在单人维护的公开项目中极为罕见。
2. **治理面是差异化强项**:两级审批 + 冻结队列全有或全无发布、凭证撤销密码学账本、三值兼容性证明——这三项在商用网关中**没有内置等价物**。
3. **数据面与商业化是主要差距**:托管语言网关无公开容量承诺、协议覆盖窄(仅 HTTP/1.1)、无 K8s 原生部署、无插件扩展体系、无多区域容灾。
4. **最大的工程负债是交付状态**:G 系列约 250 个文件未提交未合并、无 CI、G14 故障演练未闭环。当前 main 上运行的系统**不具备**紧急撤销能力。

**评审判定**:适合作为内部企业网关起点与"控制面/网关治理"架构学习样本;生产化前置条件见 §6 优先级表与 §7.3 差距清单。

---

## 2. 系统概览

### 2.1 拓扑

```
┌─────────────────────────────────────────────────────────────────┐
│  React 控制台(1.6k 行, Cookie 会话)                             │
└──────────┬──────────────────────────────────────────────────────┘
           │ /api 反代
┌──────────▼──────────────┐        发布快照(Outbox + Redis pub/sub + 2s 轮询)
│  ControlPlane(管理面)    │ ────────────────────────────────────────┐
│  SSO/OIDC · 契约 · 审批   │                                         │
└──────┬───────────────────┘                                         │
       │ 同一事务: desired 指针 + 冻结目标 + 审计                      │
┌──────▼──────────────────┐                    ┌─────────────────────▼──────────┐
│  Worker(7 个后台服务)    │                    │  Gateway ×2(YARP 数据面)      │
│  构建/超时/告警/通知/Outbox│                    │  22 个策略中间件 · generation 租约 │
└──────┬───────────────────┘                    │  LKG 文件自愈 · 准入闸门        │
       ▼                                        └────────────────────────────────┘
┌──────────────────────┐
│ PostgreSQL(事实源)    │  Redis(快照/限流/缓存/通知)
└──────────────────────┘
```

### 2.2 技术栈

| 层 | 选型 |
|---|---|
| 运行时 | .NET 10,锁定 SDK 10.0.401(`rollForward: disable`) |
| 持久化 | EF Core 10 + Npgsql(PostgreSQL),迁移版本化 |
| 数据面 | YARP 2.3(反向代理)+ 自研策略中间件管线 |
| 缓存/消息 | StackExchange.Redis 3.3(快照存储、令牌桶限流、响应缓存、pub/sub) |
| 安全 | OIDC(OpenIdConnect 8.19,自研 strict token handler)、Cookie 会话、Data Protection |
| 可观测 | OpenTelemetry 1.19(三源:Prometheus/Loki/Tempo)+ 应用内遥测 |
| 前端 | React + Vite(仅 react/react-dom/tabler-icons,自研导航) |
| 部署 | Docker Compose 14 套编排(本地/E2E/观测/性能/双网关等) |

### 2.3 项目结构(12 个 src 项目)

| 项目 | 职责 |
|---|---|
| `WebApi.Domain` | 纯领域逻辑,零基础设施依赖:状态机、校验器、解析器、JSON Pointer 映射 |
| `WebApi.Contracts` | 按功能域组织的 DTO/契约(16 个功能目录) |
| `WebApi.ControlPlane` | 管理面 HTTP API(审批收件箱、交付、管线、发布、验证报告) |
| `WebApi.Infrastructure` | EF 持久化、Outbox 分发、SSO 协调、发布协调、安全账本(G 系列) |
| `WebApi.Gateway` | YARP 数据面:配置切换、LKG、22 个策略中间件、节点客户端 |
| `WebApi.Worker` | 7 个后台服务(Outbox、告警、发布构建/超时、通知、投影、清理) |
| `WebApi.Migrator` | 迁移 + 引导账号 |
| `WebApi.ConsoleHost` | 静态托管 React + YARP 内存反代 `/api` 到控制面 |
| `WebApi.HttpSecurity` | 可信代理边界、转发表头清洗(共享) |
| `WebApi.TestBackend` / `SsoFixtureHost` / `NotificationFixtureHost` | 测试夹具 |

### 2.4 交付阶段

1. **核心环**:SSO、契约导入 + 兼容性检查、JWT/重试/缓存、外部通知、跨环境审批发布、CORS/IP/Transform(含 JSON 正文映射)
2. **凭证治理(G01–G13)**:撤销账本、网关拒绝、轮换、用量证据、职责分离、到期提醒——**未合并**
3. **可观测**:三源接入、告警、运行手册、数据字典
4. **流量策略**:限流/熔断/重试/缓存/IP/CORS/Transform/加权目的地

---

## 3. 核心机制详析

### 3.1 发布协调:冻结队列的全有或全无协议

**设计**:发布分三段,全部围绕"冻结事实"与"单调序列号"。

1. **发起**(`PublishCoordinator.StartAsync`):环境级 + 逐 API 权限重查;审批基线未变;版本 revision 未变(审批后修改 → 409);**节点队列冻结**——≥2 个启用节点且**全部**在线(120s 心跳宽限),"缺节点不降低 ACK 目标";记录申请人与发布人(分离留痕)。
2. **构建**(线性化点):PostgreSQL 事务 + 全局 advisory lock + `FOR UPDATE SKIP LOCKED`;**在事务内以发布人身份重验全部权限**(撤权 → 发布失败而非静默发布);编译快照(回滚/重试复用历史 artifact);**同一事务推进 `DeploymentSequence+1` 与 desired 指针**;为每个节点写含 instanceId 的冻结 `ReleaseTarget`;Outbox 投递。
3. **ACK 聚合**(`AckService`):instanceId/configVersion/deploymentSequence/payloadHash 四重匹配;AppliedAt ±5 分钟时间卫生;**任一节点负 ACK → 整个发布立即失败**;全成功才 `Succeeded`;重复 ACK 幂等、冲突 ACK 拒绝;**节点重启 = 新 instanceId,不能顶替旧 ACK 席位**(只能在发布成功后记录为幂等的"运行回执")。

**评估**:
- 优点:全有或全无语义清晰;重启安全;事实冻结 + 每阶段重验使"审批后偷改"在多个层面不可能;回滚复用历史 artifact 保证一致性。
- 关注点:`pg_advisory_xact_lock(8901202)` 同时串行化构建与普通 ACK(且 G 系列合并后还会加上安全 ACK),是控制面写吞吐的单一瓶颈(决策文档自认);ACK 依赖节点主动上报,ACK 丢失只能等 120s 超时判败,无 ACK 重试队列;所有启用节点必须在线的 fail-closed 策略在节点扩容/退役时需要显式运维动作。

### 3.2 网关 generation 切换与 LKG 自愈

**设计**:
- **分发三路冗余**:控制面 Outbox → Redis pub/sub 即时唤醒 + 2s 轮询兜底;HTTP 失败降级读 Redis desired——任何单通道故障不丢更新。
- **激活协议**(`SnapshotActivation`):串行锁 → 五道验证(环境 ID 防投毒、规范化 JSON、策略配置、**上游地址 SSRF 策略**、YARP 原生校验器)→ 序列号裁决(stale/conflict 不回传负 ACK)→ **先原子写 LKG 再切换** → 10s 等待 YARP `ConfigurationApplied` 真实确认 → 失败则用全新 token 恢复上一代 + LKG 回滚。
- **generation 模型**:每代配置完全不可变(策略预解析、IP 名单与 JSON 映射预编译、索引预建、快照深拷贝冻结);请求路径零解析;**租约驱动 GC**——在途请求持有 `GenerationLease`,旧代在途结束后回收;熔断器状态按代按路由独立,回滚后旧代熔断计数保留。
- **YARP 集成**:Route/Cluster ID 加部署序列前缀实现新旧共存;`Interlocked.Exchange` 换指针 + 取消旧 token 触发重载;监听器回调完成"已应用"确认。
- **LKG**:文件双代轮换(`runtime.lkg` + `.previous`),临时文件 + WriteThrough + fsync + rename + **目录 fsync(Linux P/Invoke)**,0600 权限,检测外部只读锁,32MB 上限;启动时按序恢复,两代皆坏才 NotReady。

**评估**:
- 优点:把配置切换做成带补偿路径的事务;LKG 持久化细节(目录 fsync、只读检测)达到生产级;generation 租约干净地解决了"切换瞬间新旧混用"竞态;熔断状态按代隔离避免回滚误判。
- 关注点:准入闸门(单 SemaphoreSlim)在切换瞬间串行化全部新请求——吞吐代价随请求量线性,但窗口受 YARP 应用时间限制(10s 上限);切换期间旧请求继续走旧代,故障后恢复依赖 LKG 有效(若两代都损坏则 NotReady 直到控制面重推)。

### 3.3 凭证治理与"安全账本"(G01–G13,未合并)

**设计**(工作树 `merge-governance-24-20261104`,约 250 个文件未提交):

- **安全账本**:追加式事件表 + **SHA-256 哈希链**(创世哈希 + PreviousHash 链接,规范化 JSON,PG 精度时间参与哈希);事件/墓碑不可改删,generation 不可变、epoch 不可回退;每次追加验链,传播前 Repeatable Read 全链校验;独立 SecurityOutbox(DB 租约 + fencing);升级守卫——存在旧式撤销而无账本记录则 DDL 前中止;Down 迁移在有安全事实时拒绝。
- **加密协议**:ECDSA P-256 对三类载荷签名(checkpoint/node lease/usage receipt),域名分隔。**节点 ACK 一个"已应用撤销"须过 7 重验证**:签名 → 交叉绑定(lease↔checkpoint 哈希)→ **完整权威证明**(节点出示的 head hash 必须等于该 epoch 链头)→ 撤销专项证明 → 租约单调性 → 5s 验证预算(过期判定用 DB 时钟)→ **DB CHECK 约束兜底 TOCTOU**(映射 409 而非 500)。失败报告走独立窄路径:ObservationSequence 精确 +1、challenge nonce + 短时效 revalidation、DB 时钟、响应只回 `duplicate` 防信息泄露。
- **网关执行**:`SecurityStateStore` 本地状态机(VerifiedCheckpoint + 冻结撤销证明),三值准入 `Allow/KnownRevoked/Unavailable`,独立磁盘 LKG 与写闸门。
- **轮换(G08)**:新建凭证、旧 hash 不动;双钥窗口(默认 24h / 15m–72h);**硬截止独立于操作者**——中止、暂停 worker、保留旧 LKG 都不能无限双钥;切换前验证所有消费方真实用量证据。
- **用量证据(G07)**:网关累积 → 批次封印 → 签名回执落库;保留策略与清理 worker。

**评估**:
- 优点:把"撤销"从管理面状态升级为**密码学可验证的全舰队安全事实**;防篡改(哈希链)、可证明(签名检查点)、有界(硬截止)三要素齐备;失败路径与成功路径的非对称设计(失败廉价但有界,成功昂贵且可证明)是成熟的安全协议形态;DB 约束兜底应用层 TOCTOU 是少见的纵深防御。
- 关注点:**未合并是最大的交付风险**——当前 main 的撤销要等下次正常发布快照才在网关生效;协议复杂度极高(单 ACK 验证 192 行),solo 维护者即唯一评审者,密码学代码错误的成本远高于普通 bug;G14(混合负载/锁竞争/容器故障)未闭环;8901202 锁将同时承载发布、普通 ACK、安全 ACK 三类流量。

### 3.4 契约导入与兼容性证明

**设计**:

- **导入**:"预览不是授权票据"——Commit 在同一事务内重验范围、逐目标权限、目标互斥;三种目标互斥(新建 API / 已有 API 新版本 / 已有 Draft 版本);版本发布后不可变(仅 Draft 可改,乐观并发 revision);新建路由默认绑定 API Key 认证;**每个持久化字段记录来源**(resourceUri + JSON Pointer + 文档哈希);明确的能力边界(仅 JSON、不抓 URL、callbacks 与 `$ref` 相邻属性拒绝)。
- **兼容性证明**(`compatibility-v2`):不是启发式 diff,而是**接受域包含证明**,三值结果 `Compatible/Breaking/Unknown`:
  1. 双侧先过真实 schema 评估器(非法 → Unknown)
  2. 有限域(enum/const ≤256)用**真实评估器穷举**双向验证
  3. 结构归纳(记忆化):空 schema 不满足检测、类型子集、对象/数组/组合规则
  4. **反例见证生成**:从两侧约束提取边界候选(数值 ±1/±0.5、长度 ±1、必选字段缺失…),提交真实评估器双向复核——旧侧接受且新侧拒绝 → Breaking + **见证哈希**(可复现证据)
  5. 既未证明又无反例 → **Unknown(诚实的不完全性)**;未登记 schema 关键字 → 不生成证明;硬预算(10s/节点数/字节/256 见证)耗尽 → 整体 Invalid,绝不产出部分结果;数值用精确有理数算术。
- **风险评审 → 发布冻结**:评审绑定 inputFingerprint + reportHash + engineVersion;Breaking/Unknown 必须显式 `AcceptedRisk` + 10–2000 字说明;发布构建时重新验证整条证据链。

**评估**:
- 优点:这在 API 兼容性领域是罕见的严谨度(业界工具多为启发式二元判定);sound-but-incomplete 哲学与证据链设计一致;评审→发布证据链断链即拒绝。
- 关注点:`AcceptedRisk` 是有留痕的人工旁路(一个有权限的人 + 10 字说明);256 枚举/见证上限使大型契约频繁落 Unknown,自动化率待实测;legacy-v1 引擎证据仍被接受,存在历史尾巴。

---

## 4. 工程实践与质量

| 维度 | 现状 |
|---|---|
| 构建纪律 | `TreatWarningsAsErrors` + 包锁文件 + 中央包管理 + SDK 钉死;NuGet 源单一锁定 |
| 测试 | Domain 单测(纯逻辑)+ Integration + Gateway + E2E;约 2.1 万行测试(≈15% 代码占比);E2E 覆盖真实审批、A→B→回滚、故障注入、容器停机租约接管 |
| 验证文化 | 每阶段独立 `check-*.sh`,随机隔离 owner/DB/端口,退出校验归属后清理;`docs/evidence/` 按工作包存证据;UI 级 RED→GREEN 截图留档 |
| 决策留痕 | `docs/decisions.md` 记录全部实施裁定,每条含"裁定/理由/若错代价" |
| 交付诚实 | 明确无 5k RPS/生产就绪承诺;需求台账保留部分/缺失/待复验项;ARM64 本机验证,AMD64/TLS/备份恢复待目标环境 |
| 缺失 | **无 CI 流水线**(质量全靠本地脚本);单一维护者 |

---

## 5. 优点清单(按重要性)

1. **治理面差异化**:审批发布、凭证生命周期、契约兼容证明三项在商用网关无内置等价物(§7.4)
2. **架构一致性**:Domain 纯逻辑可测;契约/发布/安全三条链路共享"冻结事实 + 哈希绑定 + 重验"方法论
3. **安全工程深度**:SSRF 上游策略、准入闸门、LKG 持久化细节、DB 约束兜底 TOCTOU、信息泄露面防御
4. **重启/故障语义完整**:instanceId 席位、LKG 双代、ACK 迟到处理、schema 能力地板校验
5. **证据驱动交付**:决策日志、阶段手册、数据字典、复验台账,交付声明与实际能力严格对应
6. **边界诚实**:能力边界(OpenAPI 子集、负载承诺、多架构)显式声明,不夸大

## 6. 风险与改进建议(按优先级)

| 级别 | 风险 | 建议 |
|---|---|---|
| **P0** | ~~G 系列 250 文件未提交未合并;main 无紧急撤销能力~~ → **已处置(合并部分)**:2026-11-08 G01–G13 评审合并入 main(`57f2ef2`,tag `governance-g13-57f2ef2`);G14 最小子集(D1)演练 3/3、P2-4 独立密码学评审 PASS 附条件(274 项安全测试实跑) | 剩余:完整 G14 协调式故障矩阵(合并后 2 周)+ 目标环境部署验证(Phase 5);**部署验证通过前仍不应对外宣称紧急撤销已上线**。安全准入路径(检查点/租约)已合入但未激活,激活须按 `docs/deployment/credential-security-operations.md` 维护窗口引导 + 同深度复审 |
| **P0** | 无 CI,验证文化无法在仓库层面自我维持 | 把 `check-contracts.sh`(domain/integration/gateway)接入 GitHub Actions 最小集(Linux x64);E2E 因固定端口单独调度 |
| **P0** | 控制面容量未验证;8901202 全局锁串行化构建+ACK(+安全 ACK) | 目标环境做容量基线测试;评估 ACK 路径按环境分锁(advisory lock 按 envId 派生) |
| **P1** | 数据面仅 HTTP/1.1:无 WebSocket/gRPC/HTTP2、无 TLS 终结与客户端 mTLS 代码 | 按业务范围定夺:若只需 HTTP/1.1 REST 可声明边界;否则补 YARP 上游 HTTP/2 与 WebSocket 代理 |
| **P1** | 无 K8s/Helm 部署形态 | 提供 Helm chart(控制面/网关/worker 三工作负载)+ 健康探针与资源配额;Compose 保留为开发形态 |
| **P1** | 单一维护者(bus factor 1)+ 高密度密码学代码 | 关键路径(ACK 验证、哈希链、签名)引入独立评审——**已于本次合并完成**(P2-4 非作者评审,`docs/evidence/governance-merge/p2-4-crypto-review.md`);补齐威胁模型文档(待办) |
| **P2** | 控制台薄(1.6k 行),需求台账有未闭环项 | 按台账补齐;优先级低于数据面与合并 |
| **P2** | .NET 10 非 LTS,SDK 钉死 | 制定 .NET LTS(如 10→下一个 LTS)迁移预案;锁策略保留 |
| **P2** | 代码风格极紧凑(单行 500+ 字符 DI 注册),可读性偏低 | 不阻塞合并;对核心安全路径适度展开,降低评审成本 |

---

## 7. 与商用网关(Kong / Higress)差距对比

### 7.1 定位对照

| | Kong(OSS/Enterprise) | Higress | 本项目 |
|---|---|---|---|
| 数据面引擎 | Nginx/OpenResty(C/Lua) | Envoy(C++) | YARP(C# 托管) |
| 控制面 | Admin API + Manager/Deco(GUI) | K8s CRD + Console | 自研管理面 + React 控制台 |
| 配置分发 | DB 模式 / DB-less 推送 | xDS(Istio 架构) | Outbox + Redis pub/sub + 轮询 |
| 发布治理 | 无内置审批(企业版审计日志) | 无内置审批 | 两级审批 + 冻结队列全有或全无 ACK |
| 容量承诺 | 公开基准,商业 SLA | 公开基准,社区/厂商支持 | **明确无承诺** |
| 目标形态 | 通用 API 网关 + 商业套件 | K8s 原生/AI 网关 | 企业内部治理优先的网关 |

### 7.2 能力矩阵

| 能力 | Kong | Higress | 本项目 | 评估 |
|---|---|---|---|---|
| 原生吞吐/基准 | 公开(万级 RPS/核),SLA | 公开(Envoy 级) | 无承诺,ARM64 本机验证 | **差距(最大)** |
| 协议覆盖 | HTTP/1.1/2、gRPC(Ent)、WebSocket、TCP stream | HTTP/1.1/2/3、gRPC、WebSocket、TCP/UDP | **仅 HTTP/1.1**(无 WebSocket/gRPC/L4) | 差距 |
| TLS 终结/客户端 mTLS | 内置 / Ent | 内置(Envoy) | 网关代码未体现;无客户端 mTLS | 差距 |
| 认证方式 | key-auth/HMAC/JWT/OAuth2/OIDC/basic/mTLS | key-auth/JWT/OIDC/HMAC/mTLS | API Key + JWT 策略;控制台 SSO OIDC | 部分差距(无 OAuth2 发行、无 mTLS) |
| 限流 | local/Redis/集群,多维(consumer/route/service) | local/Redis | Redis 令牌桶(路由级策略绑定) | 大致相当(维度较窄) |
| 熔断 | 无原生(第三方插件) | Envoy outlier 检测 | **原生,按代按路由独立状态** | **本项目领先(Kong)** |
| 重试 | 有 | 有(Envoy) | 有(协调器 + 幂等分类) | 相当 |
| 响应缓存 | proxy-cache 插件 | 无原生(Wasm) | Redis 响应缓存(键构造 + 资格判定) | 相当 |
| 负载均衡 | RR/Random/LC | RR/Random/LC/**一致性哈希/RingHash/Maglev** | RR/LC/Random/PowerOfTwo/FirstAlphabetical | 部分差距(无一致性哈希族) |
| 健康检查 | 被动(OSS)/主动(较新版本) | 主动 + 被动 | 主动(ConsecutiveFailures)+ 被动 | 相当 |
| IP 限制 / CORS | 插件 | 插件 | 内置(编译后评估) | 相当 |
| 请求/响应正文转换 | request-transformer(头部/基础体) | transform(有限) | **JSON 请求/响应深度映射(JSON Pointer,预编译)** | **本项目领先** |
| 请求超时 | 有 | 有 | 有(中间件级) | 相当 |
| 插件/扩展生态 | 75+ Lua 插件 + 生态 + Wasm(3.x) | **Wasm 热加载**(Go/Rust) | 12 种固定策略类型,扩展需改代码 | **差距(重大,或为有意边界)** |
| 发布/变更治理 | 无内置审批;企业版审计 | 无内置审批 | 两级审批 + 冻结 ACK + 回滚 + 超时判败 | **本项目领先** |
| 凭证生命周期 | 创建/删除(DB 行) | 同 | 轮换窗口 + 硬截止 + 用量签名回执 + 撤销账本(**未合并**) | **本项目领先(待交付)** |
| 契约管理 | openapi-to-kong(基础转换) | 有限 | 导入 + 来源溯源 + **三值兼容性证明引擎** | **本项目领先** |
| 可观测性 | Prometheus/OTel + 现成看板 | Prometheus/OTel + Console | OTel 三源(Prometheus/Loki/Tempo),无预置看板 | 相当(缺看板) |
| 多租户 | 组织/consumer groups(Ent) | K8s 命名空间 | 组织/项目/环境 | 相当 |
| 部署形态 | Docker/K8s/Helm | **K8s 优先 / Helm** | 仅 Docker Compose(14 套) | 差距 |
| 管理控制台 | Kong Manager/Deco(成熟) | Console | React 薄控制台 | 差距 |
| 多区域/容灾 | 多区域部署方案 | 集群联邦探索 | 单区域 fail-closed(全节点在线才发布)+ LKG | 差距(哲学不同) |
| AI/MCP 网关 | Kong AI Gateway(热方向) | **MCP/AI 网关(核心卖点)** | 无 | 差距(视业务范围) |
| 社区/支持 | 大社区 + 商业支持 | 阿里开源 + 社区 | 单一维护者,0 star | **差距(形态问题)** |

### 7.3 差距清单(按生产化影响排序)

| # | 差距 | 影响 | 建议动作 |
|---|---|---|---|
| G1 | **数据面性能与容量**:托管网关、无公开基准、控制面单锁 | 无法回答"能否承载 X RPS";商用网关有 SLA 背书 | 目标环境容量基线;ACK 分锁;若容量是关键选型因素,考虑数据面换 Envoy(控制面保留) |
| G2 | **协议覆盖**:无 HTTP/2、gRPC、WebSocket、L4;无 TLS 终结/客户端 mTLS | 现代微服务栈(gRPC/WS)与内部零信任 mTLS 场景不可用 | 明确声明边界或补齐(YARP 上游 HTTP/2 + WebSocket 成本可控;mTLS 需 Kestrel 配置) |
| G3 | ~~**G 系列未合并**~~(2026-11-08 已并入 main `57f2ef2`,G14 D1 演练 + P2-4 独立评审完成)**+ 无 CI**:紧急撤销已合入但未部署;验证无法自动回归 | 安全能力纸面化(合并后风险降为部署前窗口);验证文化依赖手动跑脚本 | P0 剩余:完整 G14 矩阵 + Phase 5 部署验证 + Actions 最小 CI(`check-contracts.sh` domain/integration/gateway;E2E 固定端口单独调度) |
| G4 | **无 K8s 原生部署**:无 Helm/CRD/探针 | 无法进入 K8s 标准运维体系(商用网关均为 K8s 优先) | 出 Helm chart;Compose 降级为开发形态 |
| G5 | **无扩展机制**:12 种策略硬编码 | 新策略需发版;商用可 Wasm/插件热加载 | 声明能力边界(企业内控可接受);或预留策略插件接口 |
| G6 | **认证广度**:无 OAuth2 发行、无 basic、无 mTLS | 对接已有 OAuth2 消费方需改造 | 按实际消费方需求补(优先级取决于流量) |
| G7 | **管理控制台深度**:1.6k 行薄壳,需求台账未闭环 | 运营效率;对发布/审批等复杂流程的可用性 | 按台账补齐,优先审批与凭证治理 UI |
| G8 | **一致性哈希/会话粘滞**:无 RingHash/Maglev/consistent hash | 本地缓存型后端的会话亲和场景 | 视业务需要(YARP 支持自定义 selector,已有加权选择器基础设施) |
| G9 | **多区域/容灾**:fail-closed 单区域 | 与商用"多活 + 最终一致"哲学不同;节点离线窗口影响发布 | 属设计取舍而非缺陷;若需,引入区域级降级策略 |
| G10 | **AI/MCP 网关**:无 | 若组织在 MCP/LLM 流量治理方向有需求则缺失 | 视范围 |
| G11 | **无现成看板/告警模板**:三源已接但无预置 Grafana | 上线运维体验 | 交付 Grafana dashboard JSON(成本低) |
| G12 | **社区/支持形态**:单人、无 SLA | 企业采购/选型时的信任成本 | 非代码问题;内部使用可接受 |

### 7.4 差异化强项(领先商用网关的能力)

1. **发布治理**:两级审批 + 冻结队列全有或全无 ACK + 回滚 + 超时判败 + 申请/发布分离留痕——Kong/Higress 均无内置等价物(企业版仅有审计日志,变更直接生效)。
2. **凭证生命周期治理**:撤销安全账本(哈希链 + 签名检查点 + 节点 7 重验证)、轮换双钥窗口 + 硬截止、用量签名回执、到期提醒与职责分离——商用网关的凭证是"可删的 DB 行",无生命周期语义。
3. **契约兼容性证明引擎**:三值判定 + 穷举/结构归纳/反例见证 + 见证哈希 + 风险评审证据链冻结到发布——商用网关依赖外部工具(oasdiff/Postman 等)做启发式二元判定,且不与发布流程集成。
4. **LKG 自愈**:文件双代 + 目录 fsync + 启动自动恢复 + 切换失败自动回滚——Kong DB-less 只拒绝无效配置,无"已应用坏配置"的回滚通道。
5. **JSON 深度正文映射**:请求/响应 JSON Pointer 映射(预编译、预算控制)——Kong request-transformer 与 Higress transform 均不支持同等深度的结构化映射。
6. **诚实的能力边界与证据驱动交付**:每项能力的支持/不支持、验证方式、边界条件均有文档化声明——商用产品的承诺以 SLA 形式存在,本项目以证据形式存在,二者性质不同但同样严格。

---

## 附录 A:评审方法与证据来源

- **源码通读**:发布协调(`PublishCoordinator`/`AckService`/`ReleaseTimeoutWorker`)、网关切换(`SnapshotActivation`/`RuntimeGenerationStore`/`EnterpriseProxyConfigProvider`/`ConfigWatcher`/`LkgStore`/`AdmissionGate`/`RoutingGenerationMiddleware`)、安全账本(工作树 `CredentialSecurity/` 14 文件)、契约体系(`ContractComparisonEngine`/`CompatibilityProof`/`ProofWitnesses`/`VersionRiskReviewService`/`ImportBatchWriter`)
- **文档交叉验证**:`README.md`、`docs/decisions.md`(12+ 实施裁定)、`docs/openapi-credentials.md`、`docs/governance-security.md`、`docs/acceptance.md`、G01–G13 工作包记录(`outputs/`)
- **事实核查**:无 `.github/workflows`;`git` 单一贡献者(289 提交);工作树 G 系列 250 个未提交变更;网关代码无 HTTP/2/WebSocket/mTLS 引用;负载均衡策略枚举 5 种(`SnapshotValidator`)
- **限制**:未执行构建/测试(评审为静态);Kong/Higress 能力基于其公开文档与社区资料的一般性认知,对比项以"架构层面是否内置"为准,具体版本差异需以目标版本复核

## 附录 B:关键术语

| 术语 | 含义 |
|---|---|
| generation | 网关上一代完整运行配置(不可变,含预编译策略与索引) |
| DeploymentSequence | 环境级单调递增部署序列号,贯穿控制面/gateway/LKG |
| LKG(Last-Known-Good) | 网关本地磁盘上的最近健康配置快照,双代轮换 |
| 冻结队列(frozen cohort) | 发布瞬间冻结的节点 ACK 目标集(含 instanceId),不可缩减/换人 |
| 安全账本 | G 系列:追加式哈希链事件 + 签名检查点/租约,使撤销成为可验证的全舰队事实 |
| 三值兼容性 | Compatible(已证明)/ Breaking(有见证)/ Unknown(诚实的不完全) |
| 接受域 | 一个 schema 接受的全部值集合;兼容性 = 旧接受域 ⊆ 新接受域 |
