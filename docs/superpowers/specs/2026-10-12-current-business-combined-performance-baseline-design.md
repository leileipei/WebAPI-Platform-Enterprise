# 当前业务组合性能对照设计

日期：2026-10-12，Asia/Shanghai。状态：书面设计待用户审阅；本轮只读分析和文档准备。推荐 A 的方向已认可，S 档模板探针已通过；本设计的产品与共享夹具修改尚未实施，实施计划在书面设计批准后编写。

## 1 目的与验收范围

在当前业务版本上建立可复现的专用未优化对照，衡量 Gateway 查找、管理列表读取及四个性能索引的组合效果。保留当前全部字段、权限、状态码、分页排序、审批发布和历史快照语义，以完整响应和独立功能断言证明业务一致，再比较吞吐、P95、SQL、读取行量、分配和内存。

沿用原第 2 包目标、资源配额和 144 对／288 窗口矩阵。新对照是下文精确列出的当前业务反事实路径；发布与用户角色的关联批量装配仍为两侧共同逻辑，不能据此报告这些关联的旧 N+1 改善。组合结果也不能拆成各项独立贡献。

本轮不修改源码、创建产品分支、启动运行环境或新增提交。后续实现必须在新的隔离分支完成，保留现有主工作区 649 个文件及所有旧证据；提交、推送、main 合并、4192 部署和正式矩阵仍遵循各阶段已明确授权的范围，不从设计确认自动推导。

## 2 固定版本与现有证据

| 身份 | 本设计的定义 |
| --- | --- |
| 当前业务共同底座 C760 | `760493da5a5bc0f0674e7e6398260c8a5ac4a6c0`，src tree `1aa393776ac219f43c7a9de48a63632dce1c82bf` |
| 现有共享夹具 H417 | `417f67cfc0b833093f17de104ebf4c882388f7f9`，54 文件指纹 `8b28b3400ef40a5ca5f67e26fde03a5e91a90431285cac274f6ca9164179c13f` |
| 候选 C_current | 产品源码保持 C760；只覆盖之后审阅并固定的新共享夹具 H_align |
| 对照 B_current | 从 H417 的相同业务 src tree 派生；只有第 3、4 节列出的反事实源文件变化，之后固定实际 40 位 Git 提交 |
| 新共享夹具 H_align | 从 H417 派生，双方使用同一份完整夹具文件及指纹；第 5 至 7 节是待实施的新增能力 |

B_current 和 H_align 是后续交付身份的名称，现在没有创建或赋予虚构 SHA。每次构建由固定 Git archive 导出产品及夹具，核对真实 src tree、提交、文件清单、镜像标签、image ID 和程序集摘要；工作树验证不得关闭固定版本门禁。

已完成探针使用一个全新 owner 和 PostgreSQL 17.11，两次克隆严格保留 71 张业务表、786 字段、4,826 行、326 索引、19 迁移。配置和权限的直接克隆遗漏及显式恢复均验证。该证据只覆盖 S100 普通夹具；S／M／L 和 scope／reference 等场景需新固定版本分别验证。[探针报告](../../evidence/gateway-query-performance/template-clone-s100-c760-h417/report.md)

33 个固定源码文件、方法行号、字节摘要和四个实际索引契约在 [source-evidence.json](../../evidence/gateway-query-performance/current-business-combined-baseline-design/source-evidence.json)。旧 B／C 发布列表完整 DTO 阻断、旧 pg_restore 索引定义差异及两项延期 Minor 原样保留。

## 3 Gateway 反事实查找

### 3.1 只替换七个查找容器

保留 `RuntimeGeneration.Indexes` 及 `RuntimeLookupIndex` 的七个公开 `IReadOnlyDictionary` 属性，以保持当前 `ApiKeyMiddleware`、租约、流量执行上下文及观测接口。B_current 的属性由新的 `RuntimeLinearLookupMap<TKey,TValue>` 实现，引用本代已经冻结的 Snapshot，按请求枚举查找，不持久保留七种哈希索引。C_current 继续使用现有冻结字典。

| 属性 | B_current 请求期行为 | 必须保持的语义 |
| --- | --- | --- |
| Routes／Clusters／Applications | 扫描对应本代数组，按 ID 查找 | 返回同代冻结对象；不存在键的字典契约一致 |
| Credentials | 扫描应用及凭证 | AccessKey 使用 Ordinal；不跳过撤销、未来或过期记录 |
| RouteAuthentication | 找到路由后枚举其绑定及同代策略 | 唯一有效认证绑定，保留真实来源策略和观测身份 |
| JwtApplications | 在匹配 runtime policy 内枚举映射 | 组合键为 policy ID 与 Ordinal claim，不串策略命名空间 |
| PermissionWindows | 找到应用，再筛选目标 API 的全部窗口 | 保留过期、未来、重叠窗口；返回不可修改的列表 |

`Count`、键和值枚举、索引器、`ContainsKey`、`TryGetValue` 的契约完整。线性 map 不缓存成功、失败或上次查找；不得把字典结果藏在静态集合、闭包、租约外或跨代对象中。临时验证集合不由 map 捕获。查找先比较键，只对命中的对象组装值；RouteAuthentication 不能为扫描中的全部路由逐个解析配置。PermissionWindows 返回目标应用的只读筛选视图，不在每次请求重做全代 GroupBy 或生成全代数组，以免人为加入原扫描路径不需要的额外工作。

### 3.2 构建时验证仍完整执行

B_current 构建时使用可回收的临时集合检查：路由、Cluster、应用、策略 ID 和 AccessKey 唯一；认证配置与策略一致；路由 Cluster 和绑定策略存在；每路由最多一个认证绑定；JWT 配置存在、应用存在、组合映射唯一；授权 API 存在。验证完成后创建扫描视图，临时集合不进入稳态生成代。

重复键、缺失引用和无效认证映射必须与当前版本一样拒绝生成代，不可用 `FirstOrDefault` 静默选取一个非法对象。直接构造 RuntimeGeneration 的测试也必须拒绝，不能仅依赖外层 SnapshotValidator。错误仍走当前新代失败、旧代保留、LKG 和 ACK 路径。

冻结复制、认证配置解析、现有策略字典、Circuit lease、deploymentSequence 租约和退休释放保持当前实现。请求期状态、ValidFrom／ExpiresAt 边界、秘密验证、JWT 验签和 forwardBearer、Header 清理、重试与缓存不改。代构建分配和双代峰值必须包含临时验证集合的真实成本；稳态保留量与构建峰值分别报告。

## 4 管理列表反事实读取

所有列表保留当前参数校验、入口授权、筛选和状态码。数据库字符串排序继续在 PostgreSQL 上使用当前 `OrderBy/ThenBy`；B_current 取未分页、已排序的主表候选，在完成可见性过滤后内存分页。不能用 .NET 字符串排序替换数据库排序。路由维持当前已批准的 CurrentCulture 与 ID 次排序。

对逐资源权限检查继续调用当前 `CollectionAuthorizationQuery.Scopes` 的同一 SQL 谓词；不复制旧授权服务近似实现，不新增管理员放行或父范围扩张。先过滤全部可见资源，再计算 Total 和页；撤权必须在下一请求生效。普通列表不新增跨请求一致性承诺。

| 资源 | B_current 的精确变化 | 共同业务逻辑与边界 |
| --- | --- | --- |
| API | 当前项目筛选与 SQL Code／ID 排序，全量主表后内存分页 | 当前入口授权和 Dto |
| Cluster | 全量 SQL Name／ID 排序后分页；本页逐 Cluster 读取 Destination | Destination 必须 Name／ID 排序，不能直接复用缺少 ID 次排序的旧详情 helper |
| Route | 全量完整记录；逐条调用当前有效配置逻辑后按 MatchOrder／CurrentCulture Path／ID 排序分页 | 当前默认认证、超时及 JWT 字段；候选继续只读取全量三列轻量键及页内数据的 O(N) 例外 |
| Application | 全量 SQL Code／ID 排序；逐条执行当前 app.read 范围谓词，再分页 | Active actor、精确项目范围；不能以空列表吞掉 401 |
| Policy | 当前组织／项目和筛选 SQL，全量 Name／ID 排序；逐条执行当前 policy.read 谓词，再分页 | 共享策略只通过真实授权后代可读，项目策略仍要求项目范围；保留 422 筛选错误 |
| Release | 当前环境全部 Release 按 CreatedAt／ID 降序，内存分页 | 本页仍一次调用当前 ReleaseListReader，不修改批量审批资格、风险脱敏、访问地址与交付轨迹 |
| User | 全部 User 按 Username／ID 排序后分页 | 本页 UserRole 查询、数组构造及写治理保持当前批量实现 |
| Node | 当前环境所有节点及环境元数据 join，按 NodeName／ID 排序后分页 | Dto、心跳过期及 Snapshot capabilities 不改；性能夹具心跳须处于稳定区间 |
| Node Event | 先维持 ID 降序最近 10,000 条上限，再取该集合并内存分页 | Total 只统计截断集合，不能改为全部历史 |
| Snapshot | 全部版本元数据按 VersionNo／ID 排序后分页；本页逐版本查大小和最新发布 | 不读取 Payload／PayloadBytes；最新发布沿用当前最大 deploymentSequence 谓词及重复异常，不用 First 隐藏非法重复 |
| Alert Rule | 主表全量当前 SQL 排序；逐条使用当前范围谓词，再分页 | 本页关联逐规则读取，单次请求的数据库时间只取一次 |
| Alert Event | 当前可见环境、严重度和状态 SQL 筛选排序，全量后分页 | 本页逐事件读取 Transition 和状态；Transition 的 OccurredAt／ID 排序与单次数据库时间保持 |
| Policy Reference | 当前可见来源、解码、保护和完整合并逻辑不改；将当前批量载荷读取改为每描述符单独读取 | 保留负大小拒绝、单个合法大对象、缺失／损坏载荷 409、SourceId／Position 次序以及写保护的完整不可读引用范围 |

发布 Target 和用户 Role 等当前数组未全部声明关系排序；通过保留它们现有批量装配，避免基线逐条查询改变完整响应字节。这两类的报告只归因于主表全量读取与分页变化。ReleaseListReader、ApprovalEligibilityService、DeliveryReleaseGuard 和 CollectionAuthorizationQuery 是共同语义底座。

告警基线 `AlertListReader` 内部拆开页内关联查询，但先通过 `AlertEvaluationLeaseStore.DatabaseTimeAsync` 获取一次数据库时间并在本页所有 DTO 中复用。不能逐项调用当前 ReadRulesAsync／ReadEventsAsync，因为它们各自获取时间。普通性能数据当前状态为稳定 Unknown；Known、Disabled、Resolved、过期与将来 slot 的真实功能回归另行明确覆盖，不为了字节比较关闭真实时钟或改变配置上限。

### 4.1 产品源码范围

下列路径相对产品仓库，仅在 B_current 分支允许改动；共有 13 个已有文件及 1 个新文件：

- `src/WebApi.Gateway/Configuration/RuntimeLookupIndex.cs`
- 新增 `src/WebApi.Gateway/Configuration/RuntimeLinearLookupMap.cs`
- `src/WebApi.Infrastructure/Catalog/CatalogService.cs`
- `src/WebApi.Infrastructure/Applications/ApplicationService.cs`
- `src/WebApi.Infrastructure/Policies/PolicyService.cs`
- `src/WebApi.Infrastructure/Releases/ReleaseService.cs`
- `src/WebApi.Infrastructure/Governance/GovernanceService.cs`
- `src/WebApi.Infrastructure/Gateway/GatewayReadService.cs`
- `src/WebApi.Infrastructure/Routing/ClusterService.cs`
- `src/WebApi.Infrastructure/Routing/RouteService.cs`
- `src/WebApi.Infrastructure/Alerts/AlertRuleService.cs`
- `src/WebApi.Infrastructure/Alerts/AlertEventService.cs`
- `src/WebApi.Infrastructure/Alerts/AlertListReader.cs`
- `src/WebApi.Infrastructure/Policies/PolicyReferenceReader.cs`

已有服务只改变上表对应的列表读取，不改变详情、命令和写保护。Contract DTO、持久化模型、Migration、ControlPlane／Worker 的 DI、ApiKeyMiddleware、RuntimeGenerationStore 和全部其他 src 字节保持共同底座。若实现需要扩大列表，先形成具体差异和理由再审阅，不默许恢复原性能提交的全部 22 文件。

## 5 模板与索引实验

### 5.1 独立模板生命周期

在每个新 owner 的固定 PostgreSQL 17.11 中，用 C_current 的完整当前 schema 和固定共享 seed 创建数据。ordinary／scope／reference 各档分别创建模板；每次创建和切换先停止本 owner 的全部应用服务、确认其数据库无会话。模板名由经验证的 scene／scale 短键生成，源库关闭连接；工作库始终为 CLI 允许的 `webapi_performance`。

共享 helper 依次完成：先按当前持久化模型验证源库全部表列、类型、非空和主键，再捕获其行二进制摘要、真实迁移及 ProductVersion、全部索引定义和有效性、数据库元数据及配置／权限；冻结模板；创建工作库；恢复捕获的数据库级配置、角色在本库的配置和数据库 ACL；再次执行目标模型结构检查及严格契约复验；之后才能选择实验角色。只证明两侧相等不足以证明源库结构正确。

现有 `CaptureLegacyAsync` 的基础捕获不能替代完整目标类型检查。共享夹具新增只读 `MigrationProbe.CaptureCurrentContractAsync(WebApiDbContext, CancellationToken)` 和 CLI `migration-current-contract`，复用已有全目标类型／可空性验证及主键检查，返回其实际 LegacyContract；不执行迁移或修改数据库。入口继续限制固定 PostgreSQL Host、DB 和 Username。两角色的字段模型相同，基线四个索引缺失由独立角色门禁处理，不影响此字段结构检查；现有 migration-inspect／legacy／compatible／pure-index 入口不放宽。

PostgreSQL 的模板复制不复制数据库级配置和权限，且源库不能有其他会话；配置恢复使用 ALTER DATABASE 等独立操作。[CREATE DATABASE 官方说明](https://www.postgresql.org/docs/17/sql-createdatabase.html)及 [ALTER DATABASE 官方说明](https://www.postgresql.org/docs/17/sql-alterdatabase.html)

本专用 helper 只处理本 owner 的固定 webapi owner 和 ALL／webapi 本库设置；支持实际需要的 TimeZone=UTC、work_mem=16MB 及数据库 CONNECT／CREATE／TEMPORARY 权限和 grant option。遇到未知角色、其他设置或不支持的权限结构，在工作库破坏性操作前拒绝；不能忽略或猜测恢复。完整配置值留在 0600 私有契约，公开证据只展示非敏感固定值或哈希。对象 owner／ACL／默认权限由模板复制，必须复验；全局角色配置和授权不修改。

每次克隆有独立回执；连接检查、配置恢复或任何摘要不同都 fail closed。失败保存本 owner 的私有输入与诊断；清理仅使用双标签验证的 owner 资源。源模板与工作库均随本 owner 回收。旧 dump／restore helper 及严格门禁保留，新增模板 helper 不承接生产冷备份或在线复制。

### 5.2 四个且仅四个索引差异

| 名称 | 表 | 已存在的定义 |
| --- | --- | --- |
| `ix_api_routes_environment_order_path_id` | api_routes | environment_id、原路由优先级表达式、path、id |
| `ix_policies_organization_name_id` | policies | organization_id、name、id |
| `ix_alert_rules_organization_name_id` | alert_rules | organization_id、normalized_name、id |
| `ix_alert_events_organization_project_started_id` | alert_events | organization_id、project_id、started_at DESC、id DESC |

来源为当前 `20261010091247_GatewayQueryIndexes`。四个索引的实际定义摘要及状态以固定 C760 的源契约为准。候选保留全部四个，基线只在克隆工作库的一次事务中删除这四个；不修改 Migration 源码或历史记录，不删除其他约束索引。DDL 必须是审阅的精确清单，不能接受任意字符串或用宽泛 SQL 前缀授权。

先验证工作库与完整模板严格一致，再应用角色差异；事务前后记录列／主键／行／迁移／配置／权限一致和全部其余索引一致，基线缺少恰好四个，候选四个恰好存在且有效。S 普通探针当前有 326 索引，基线预计 322；其他档位按实际契约计算，不把此数量写成通用常量。

原始 `ModelMatched` 必须如实保留：基线可能为 false。另提供 `experimentRoleVerified`，只接受已批准的四个缺失项，并独立验证完整 schema、无待执行迁移／模型变化、其他索引和数据一致。不能将 ModelMatched 改写为 true，原纯索引迁移与业务兼容入口继续保持原要求。

完成角色验证后在计时外 ANALYZE；不通过运行 MigrateAsync 伪造或补回基线实验索引。管理及 Gateway 数据库切换都必须走上述流程，不能只改管理路径。每次角色应用是对从模板重新克隆的工作库执行，不在已有测量库上反复增删来累计不同状态。

## 6 共享夹具与真实查找证明

新增显式 `--experiment current-business-combined-v1`，仅允许固定产品／固定夹具的 compare，或带全部固定身份的 validate 比较诊断；旧模式默认行为保持。来源树差异清单和所有身份验证发生在建库前。管理预检使用既有 `--preflight management`，只能产生管理诊断，不能关闭完整性能门禁。

| 共享夹具单元 | 责任及待修改范围 |
| --- | --- |
| 新 `scripts/performance/template-fixture.mjs` 与测试 | owner／镜像／私有契约验证、连接检查、冻结与克隆、配置及 ACL 恢复、严格契约回执 |
| 新 `scripts/performance/index-experiment.mjs` 与测试 | 精确四个 DDL、事务、两角色校验、原始模型结果及独立实验结果 |
| `sources.mjs`、`comparison-runner.mjs` 与现有测试 | 显式模式、固定身份、same-source 镜像复用、模板流程、计时前角色门禁和失败保存 |
| `LookupMicroProbe.cs`、`PerformanceProbeTests.cs` | 实际查找结构分类与真实接口调用，错误混合结构拒绝 |
| `MigrationProbe.cs`、`Program.cs`、`PerformanceMigrationTests.cs` | 独立只读完整目标结构契约，源库及两角色都验证类型、非空和主键，旧迁移入口保持 |
| `completion.mjs` 及完成门禁测试 | 组合对照新增证据要求，旧完整性要求不降级 |
| 新 `current-business-parity.test.mjs` | 串联固定双方原生响应与源码／库角色证据；检查普通、范围、引用及错误契约 |

`LookupMicroProbe` 目前以 Indexes 属性是否存在判定 indexed，必须修正。新探针先验证七个实际容器：全部是当前冻结字典时标 indexed，全部是 B_current 的已审阅线性 map 时标 scan；混合或未知容器拒绝。两种都编译并调用实际实例的接口，不能因收到 baseline 标签就改用独立手写扫描。共享探针通过反射验证线性 map 的完整类型与接口，不能直接编译引用候选源码没有的 baseline 专属类型。类型分类还必须结合固定程序集来源、产品差异审阅和线性 map 访问次数的独立测试，类型名字本身不是算法证明。

保留七个操作、first／middle／last、每项 10,000 次与命中数要求；输出真实实现种类。线性 map 的独立测试用计数枚举器确认访问增长和 Ordinal 规则，不把计数器插入正式请求路径。当前 GenerationProbe 保持两侧实际构造器、旧租约、退休回收及构建分配测量。

同一 source、harness 和全部输入指纹的两侧，只构建一次并复用其真实 image ID，记录复用关系；身份不同仍独立构建。通过新的同 SHA 管理预检后，才单独评价旧双构建标签复用 Minor。既有失败不得删除；同实现预检只能证明协议，不能证明优化效果。失败缓冲测试的另一个延期 Minor 继续保留，未由本设计关闭。

SQL 分类必须从固定 B_current／C_current 加 H_align 重新采集，清单逐项绑定双方真实源码与行号，并固定公共清单哈希；所有查询包括权限及非读取命令都统计，未归类项不能关闭门禁。真实 CommandText 只在私有诊断窗捕获，不在性能窗口记录参数、秘密或原始 SQL。

## 7 功能、数据库与性能门禁

实施计划必须把下列检查分配到可独立验收的任务；没有计划或尚未执行的检查，不写为通过。

| 门禁 | 通过条件与失败处理 |
| --- | --- |
| G0 固定身份与源码范围 | 候选 src=C760；对照 src 只含第 4.1 节差异；同一 H_align 指纹；所有 build/source/image 摘要及真实版本一致，否则不建库 |
| G1 查找与业务等价 | 同代对象、七 map 契约、全部构建拒绝条件；匿名／ApiKey／JWT、401／403、Header、多授权窗口、撤权／轮换、旧代租约和回滚；独立期望断言与双方比较都通过 |
| G2 模板与角色 | S／M／L 的 ordinary／scope／reference 及 Gateway 管理库逐次完整契约相等、配置／权限相等；恰好四个实验索引差异；故意损坏字段、主键、行、历史、索引或 ACL 均拒绝，事务失败不留下部分删除 |
| G3 完整管理预检 | 所有档位的普通 13 类列表、scope、reference 覆盖完整；成功响应字节／SHA、ID 顺序、Total、页参数及嵌套字段一致，错误仅保留既有 traceId 例外；任何差异都在 Gateway 测量前阻断 |
| G4 固定回归与诊断 | 当前候选完整测试、构建、安全回归、真实 PostgreSQL 迁移／EXPLAIN 通过；基线业务断言同样通过；本设计每个读路径的反事实成本经独立测试证明；失败和原始日志保留 |
| G5 正式性能完整性 | 获得该阶段运行授权后，原矩阵 144 对／288 个真实窗口、管理／SQL、全部微基准和代生命周期、容器窗采样及清理／秘密门禁齐全；缺窗、超时、意外响应或不可解释瓶颈均不能关闭 |
| G6 收益与预算 | 按原第 2 包预算和退化阈值判断，不以完整性替代收益验收；查询预算、读取行量、字段和权限均有证据；4192 部署单独验收 |

候选原完整回归不得削弱。基线故意不满足候选优化预算，因此原完整测试仍执行并保留结果；只有实施计划事先精确登记的性能预算／投影断言可归为预期反事实差异，不能把整类功能测试忽略。基线必须另通过相同业务期望的功能测试、当前安全回归和跨版本完整 DTO 比较。任何其他失败都阻断；报告分别展示候选完整测试与基线测试结果，不能把预期预算失败重标为“全量零失败”。精确登记及反向成本断言必须在首次基线验证前完成。

权限与分页覆盖平台／组织角色、项目／环境只读、无功能权限、无 Scope、停用用户／祖先、跨组织角色；页大小 1／50／100，第一页／中间／末页／越界页，以及钳制、同名和多语言路径。复杂对象覆盖当前风险评论与内部地址脱敏、流水线创建者审批限制、空／完整交付轨迹、损坏可见冻结／运行载荷和不可读写保护。未声明关系排序的两类共同读逻辑也要做多关联数据的完整响应回归。

索引负向检查覆盖未知索引、唯一／约束索引误删、同名不同定义、无效／不 ready 索引、删除失败及第三个对象后失败回滚；未知数据库设置／角色、owner 标签不符、模板仍有连接或模板契约漂移，必须在下一步数据操作前阻断。不能强制终止其他 owner 的会话。

## 8 矩阵与结论规则

Gateway 档位仍为 S=100 路由／100 应用／200 凭证、M=1,000／1,000／2,000、L=10,000／5,000／10,000，每应用 8 个授权窗口，产品 Snapshot 上限仍为 16,777,216 bytes。管理列表各类资源按既有 S／M／L seed 与场景规则记录实际总量、可见量和关联量。

三档四个主认证场景 × 并发 1／32／128 × 重复 3，得到 108 对；L32 四个补充认证 × 重复 3 为 12 对；L32 两个单节点 × 四主场景 × 重复 3 为 24 对，共 144 对。双方交替次序、每窗预热 10 秒／测量 20 秒、双节点与单节点证据、客户端超时和资源配额均不变。

沿用两 Gateway 和 ControlPlane 各 2 CPU／2 GiB，PostgreSQL 2 CPU／3 GiB、backend 1 CPU／256 MiB、Redis 0.5 CPU／256 MiB、client 2 CPU／1 GiB；server GC、70% heap hard limit、池 32、数据库 max_connections=100／shared_buffers=512MB／work_mem=16MB／maintenance_work_mem=256MB／parallel workers per gather=0／UTC。实际容器限额、GC、运行时、宿主环境和指纹必须相等。

当前候选的 SQL 预算仍引用原设计第 5 节，Release ≤12、告警规则 ≤10、告警事件 ≤6 等不自动上调。若新业务字段使固定 C760 超出旧预算，应保留实际阻断，另提出具体改动设计，不在本实验中删字段、改分类或扩大候选源码范围。路由全量轻量键例外及策略引用批处理的特殊预算照旧。

保留原小档 >10% 中位吞吐／P95 退化调查阈值、稳态内存增加 >25% 的结构评估、资源窗口有效性及端到端瓶颈说明。四索引存在不证明它们实际被使用；应提供小／大档 EXPLAIN，当前路由轻量键查询若不使用排序表达式索引，必须如实报告。组合吞吐结果不用于单独声明某个索引或某种算法贡献。

## 9 阶段交付与未关闭事项

书面设计批准后，按当前会话逐项实现方式编写计划，顺序为：模板及角色门禁 → 线性查找与拒绝语义 → 管理反事实读路径 → 实际结构探针与固定分类 → 全功能／迁移回归 → 固定各档预检与诊断 → 具备门禁后准备正式运行审阅包。各任务的精确文件、签名、测试、预算失败登记和授权边界由计划承接。

最终审阅前固定 B_current、C_current、H_align 身份、源差异、分类清单、场景数据、模板／索引角色证明及全部失败归档；新本地提交按具体审阅包获准后执行。正式矩阵运行授权与推送、合并、部署边界分别记录。

旧 B／C DTO 阻断通过新的共同业务对照解决公平性问题；旧失败仍是历史事实，不用新对照擦除。S 模板成功不等于旧 pg_restore 修复、全档模板合格、固定 H417 全量正式验收或生产恢复通过。本设计也不关闭同 SHA 构建和失败缓冲两个延期 Minor，更不关闭第 2 包或五个工作包总项目。
