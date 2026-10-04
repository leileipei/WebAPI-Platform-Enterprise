# 真实监控与告警实施记录

计划：docs/superpowers/plans/2026-10-04-observability-alerting-implementation.md。基准f085857。用户已确认设计和计划，沿用Native。

Task 1进行中：查询范围7d / 100条 / 30秒时钟偏差；先观察7项失败，再观察领域全量23项通过。原真实API90项及前端9项基线通过。尚未接入采集源，尚未完成6页真实链路。

Ruling: 沿用此前确认的独立enterprise功能分支就地实施，保留原型；如需拆分可按基准提交分离。测试只读模式沿用核心真实值read，不引入read_only。

Task 1验证：新增7项RED失败源为未实现校验；GREEN全量领域23通过。原网关12通过；共享测试支持已随网关项目编译。新增契约与校验仍不代表外部采集已完成。

## Task 2：隔离 OTLP 协议栈

固定 SDK 实测为 10.0.401、ASP.NET / .NET Runtime 10.0.12、aarch64。OTel SDK / Hosting / Exporter 1.19.1 与两个 Instrumentation 1.19.0 锁定还原成功，解决方案编译 0 warning / 0 error。四种新镜像不可变摘要在 images.lock.json。

部署行为测试 4/4 通过；新增配置前实际 RED 2/2，端口校验补充先 RED 后 GREEN。最终 protocol-smoke.json 记录真实 counter=1、日志标记找到、对应 Trace 找到，Loki / Tempo 实际容量各 1 GiB；结束精确项目 0 容器 / 0 卷 / 0 秘密文件。此证据是合成 OTLP 协议验证，尚不是网关 E2E。

决策：官方 Collector 0.162.0 通用标签不可用，固定实际拉取的 ARM64 发行摘要，AMD64 未验收；此 Docker internal 网络不映射主机端口，采用独立普通 bridge + loopback 发布，生产需出口网络策略；隔离 Loki/Tempo 使用有界临时卷，持久生产存储需配额及备份；SDK 自观测不涵盖导出失败，后续接入增加稳定平台诊断。

## Task 3：网关有界采集

六项指定测试实际RED后GREEN：旧请求跨版本切换仍保留原序号/配置/实际目标，401未知应用与W3C兼容字段，已发送200头的断开不计成功，三种信号敏感字段缺失，有界队列满仍转发成功，精确健康请求不进业务量。导出失败恢复及实际YARP状态读取组件检查通过。

额外发现两项边界并观察RED后修正：70000字节Collector响应原先无内存边界，已改HeadersRead和64KiB限制；空Destination pool原先出现并未实际调用的client span，现仅实际ProxiedDestination UUID可导出client span。最终网关全量22/22通过，含原12项路由/切换/LKG检查，0 skipped。

请求记录只做SDK计数与拥有独立存储的JSON快照入队，默认容量2048、每批最多512，I/O仅在后台。健康状态来自YARP；字段、秒histogram buckets与诊断口径见contracts/observability.md。此阶段测试使用内存sink作组件观察，未宣称网关至三源E2E或六页UI已经验收。

决策：仅注册受控Gateway ActivitySource并对字段采用白名单，省去自动URL/异常/events/baggage采集；保证Gateway server到实际代理client，不包含未插桩后端内部调用。SDK指标汇总后立即复制至显式JSON队列，不保留可复用MetricPoint/Activity/LogRecord；自定义OTLP JSON适配器需后续真实Gateway闭环证明。

## Task 4：受授权环境限定的指标查询

六项指定指标测试及五项边界测试先RED后GREEN；完整真实CP/PostgreSQL集成101/101通过，0 skipped。测试覆盖环境grant不能读取兄弟环境、项目全部仅返回授权环境、撤销下一GET拒绝、histogram合并后算分位数、缺节点及缺采集环境保持Partial/null、来源失败503及不泄露地址、API路径与query冲突422、Destination环境归属二次校验。受控provider协议响应只作查询/权限组件证据，真实Prometheus重启计数器reset仍待Task 14。

metric-contract-smoke.json记录独立真实Collector/Prometheus/Loki/Tempo栈：counter=1、histogram_count=1、0.05秒bucket成立、节点观察gauge为有效秒时间戳；日志与Trace可查。精确项目清理0容器/0卷/0秘密文件。合成协议验证不是Gateway E2E。

决策：覆盖要求整个查询窗口有节点观察证据，新栈不会伪报完整历史；每个授权环境需采集节点，空集合不能逻辑上视作全覆盖。NodeName沿用原全局唯一约束，详情补充可选EnvironmentId明确节点所属环境，不更改核心数据库架构。API筛选显式FromQuery防止Minimal API将路径参数隐式覆盖查询冲突检查。

## Task 5：监控总览及 API 监控

前端新7项行为先RED后GREEN，完整前端16/16和类型检查/生产构建通过。查询参数保留Back/refresh的范围和截止时间，原导航未保存guard继续有效。Scope改变立即隔离旧结果、Abort旧请求，重置URL游标及页码；IP不入URL。KPI空值与已知0分开，SVG包含真实数据点时间/单位/窗口样本标题及同源表格，不增加图表库。

浏览器1440px检查：原两个路径RED落在覆盖页；本次正常、NoData、Partial、503、管理员空Scope、环境切换、下一GET撤权清除、API跳转和返回/刷新保留筛选均观察。正常/API详情及4种状态截图在ui/。浏览器使用真实CP/PostgreSQL随机数据库与可控provider协议响应，明确是UI与权限证据，不替代真实Gateway-Collector三源闭环。最终新浏览器会话控制台0 error/0 warning；开发热更新曾重复createRoot，已复用HMR data中的root并重新检查最终会话。临时随机DB test_080b0e20ad8f49dd93d139d3e1925980精确检查0，容器0、cookie文件删除、预览停止、浏览器临时tab关闭和视口恢复。原管理员及4180/4181/5090服务未扩权。

批准spec第151行要求状态分组，而Task4白名单遗漏Status。Task5补充Status：先观察422 RED，再全量集成102/102通过。HTTP状态按真实请求计数分组，时延桶未按状态采集，因此状态行的延迟保持null。请求量及分位延迟标注采样估计。日志/Trace入口按权限检查，后续页在Task8接入；规则预填与入口在Task13接入实际规则创建，当前不暴露无效编辑操作。

## Task 6：日志分页与安全导出

六项计划测试先RED404，再GREEN。附加真实Loki展平metadata及纳秒差边界RED无行后修正；URL原IP原先被忽略RED200后改422；极早时间Ns溢出RED500后改422；来源安全sourceType和Retry-After、CSV采集状态header均先RED后补齐；Destination忽略RED200后补授权与二次过滤。最终真实CP/PostgreSQL全量115/115通过，0 skipped，其中日志13项；网关共享契约回归22/22通过，协议行为6/6通过。过期/篡改游标和大同时间边界补充测试验证已有防护。

独立真实Collector/Prometheus/Loki/Tempo协议栈确认logContract.metadataFound / normalizedNames / filterFound=true，shape=flattened-stream-labels；三源仍有实际counter=1、日志标记及Trace结果，histogram/gauge成立。日志字段与筛选证据在log-contract-smoke.json；结束精确项目0容器/0卷/0秘密文件。此证据不替代实际Gateway→CP→浏览器链路，后者Task14验证。

游标绑定身份/授权环境集合/过滤/固定范围，保留源纳秒并稳定去重；可辨识截断停止下一页。IP POST内存输入→HMAC，不入URL/DTO/CSV。CSV每源分页及提交前重验授权，8MiB/10000行有界buffer、公式中和，读取失败不发成功文件；行数、截断、采集状态在headers发出前已知。契约与决策成本见contracts/observability.md。

## Task 7 — Trace 查询与跨 Scope 投影

- 新增 Trace search/detail HTTP 端点，独立 trace.read、固定范围、受限 TraceQL、源超时/大小限额和响应后再授权；404 未找到与 503 源故障分开。
- 可信 Scope 只从 Resource 环境属性解析；未知环境/span资源/缺父节点裁剪，返回 partialTrace；返回 DTO 仅安全属性白名单。汇总起止/时延/Success-Error 由授权 spans 推导，不复制跨环境 root 元数据。真实 start/parent/duration 直接来自源，不生成后端内部调用。
- 五指定 HTTP 用例 RED 缺路由→GREEN；Base64 全零 parent 边界 RED 空 spans→GREEN。真实固定 Tempo 3.1.0 返回 batches/Base64 IDs/枚举字符串；捕获合成响应又经适配器投影得到 200ms server、140ms client、+20ms 开始偏移及真实父关联。
- Provider cap 明确 truncated/noNext；非法零 ID/legacy ID 不访问源。共享签名 codec 缺配置最初误报 logs，增加链路专属用例 RED→修正为 traces。
- 真实隔离 Collector/Prometheus/Loki/Tempo 合成协议检查验证 server/client、parent/resource/API search、安全属性，7/7 检查通过，清理后 0 containers/volumes/secret files。见 trace-contract-smoke.json；这不是业务网关/查询 API 端到端验收，Task 14 才验收后者。
- Ruling：为避免泄漏跨 Scope root 时间，最多 200 候选重取详情，再按授权 span 时间+TraceId 签名分页；超过源候选上限明确截断无下一页。代价：忙碌环境须缩小时间窗，不能声称全量历史可翻页。

## Task 8：日志与调用链调查页面

日志完整筛选、固定截止时间、50条游标分页、当前查询CSV导出、详情抽屉以及链路双向跳转已接入。IP仅放内存并通过受CSRF保护的POST过滤。Trace展示真实span时间与父关系、安全标签、表格回退、采样与部分链路提示；来源错误清空旧列表与瀑布图，NoTrace和503分开。复制具备浏览器标准剪贴板失败时的临时选择回退。

前端新8项行为先RED再GREEN，全部24/24通过，类型检查及生产构建通过。1440px浏览器检查分页50→5→50、固定时间、CSV55行及masked IP、跳转、真实剪贴板、瀑布图0/100与10/70百分比、键盘选择、抽屉Escape和焦点恢复、环境切换、空查询、Partial、NoTrace及503均有证据；真实PostgreSQL撤销随机账号权限后，下一GET清空两个页面旧数据。最后会话控制台0 error/0 warning，没有全页横向溢出。

浏览器使用真实CP和随机PostgreSQL库、受控provider协议响应，不是业务网关三源端到端证据。截图31-source-unavailable-red.jpg保留修复前错误空表文案的RED；GREEN来源错误状态见30-source-unavailable.jpg，Trace另经页面检查验证无旧瀑布图。浏览器下载事件等待超时，但实际合成CSV文件和服务器行数headers验证55行导出；不把超时等待作为成功证据。

临时数据库test_1e93b36f99ef4749accef1a8f98b2e63查询确认0，精确临时容器0、两个会话密钥文件不存在；预览停止、临时浏览器页关闭、视口恢复。原管理员权限未扩展。仅保留用户下载的合成CSV和持久QA截图。

## Task 9：告警领域契约与新增数据迁移

四个新表保存完整规则、冻结事件、带单调租约token的评估状态及追加流转事实。Scope复合外键和目标约束、nullable资源的非空resource_key、单活动事件部分唯一索引、logic_revision occurrence唯一键和Scope规范化名称NULLS NOT DISTINCT唯一索引已建立。所有时间用timestamptz，实体并发revision为EF并发token。

限制表达式只接受四指标、六操作符、三个token和有限非负数字，比例为0至1；unknown值返回null。领域缺parser RED后40/40通过；真实PG三个约束用例缺表RED后通过，两个实际事务竞争只保留一活动事件。额外验证旧核心迁移升级后原记录与Snapshot字节不变、无规则seed。全量集成曾出现部署脚本缺新迁移：128通过/1失败；重新生成幂等脚本后129/129通过，0 skipped。未修改原核心持久数据库或账号权限。

事件冻结Scope按实际Project/Environment祖先关系和rule组织外键约束，不与之后可编辑的rule具体Scope作永久相等外键；规则执行服务负责在事件创建时校验当时定义。下一任务实现规则治理端点，当前不代表真实告警已触发。

## Task 10：规则治理与只读实时测试

创建、详情、列表、编辑、启停、Scope预览及只读测试端点已实现；完整写范围授权在治理事务内重读，ETag与稳定组织范围的幂等身份保护Scope修改后的重试。名称及通知编辑保留逻辑修订和Pending、人工抑制；逻辑变更关闭旧事件、清空旧租约并追加流转及审计。宽范围当前及未来Active环境独立匹配，任何环境未知则汇总Unknown；全Known时成功时点取最早值。

六指定用例先RED404后GREEN。附加范围修改重试RED412、来源状态丢失、API健康误借环境值、删除后端导致规则无法修复、源故障只读测试未返回Unknown均先复现再修正。已明确规则不能跨组织迁移，API健康需选环境或后端，详情管理与新目标验证分开。专项17/17及完整真实CP/PostgreSQL集成146/146通过，0 skipped；规范化宽范围名称、字段边界、撤权保留规则、无metrics.read和无幂等键的只读数值查询，以及无规则、事件、通知、审计副作用得到验证。

来源查询使用受控provider协议响应，是服务及权限组件证据；实际三源网关闭环与真实持续告警仍待后续任务。通知三个渠道仅存意向，不包含密钥或URL。边界与成本详见contracts/observability.md。

## Task 11：持续窗口与带隔离令牌的评估租约

默认15秒槽位、30秒查询延迟、30秒租约；槽位和有效期取PostgreSQL时间，不追补旧槽位。评估键先按持久规则和当前环境建立，默认每批最多4个并发查询、每个使用独立DbContext；源查询不持有治理事务。提交重新检查规则修订、启停、实际范围、资源、租约token/有效期及已提交槽位。触发、恢复、流转和system审计原子保存，system UserId为空且审计指向实际事件和冻结环境。

纯领域7个新用例先RED后GREEN，领域全量47/47通过。真实PG集成验证双Worker同槽位单事件、重复提交拒绝、真实过期token接管、查询进行中禁用、查询期间重命名释放旧租约且保留Pending、重启和源中断不虚假恢复、未来环境建键、资源实际删除关闭事件、范围暂停保留Firing/人工抑制。补充RED修复较旧false解除人工抑制、Worker停机仍显示Known及无业务请求时健康gauge误报Unknown。最新完整真实服务集成159/159通过，0 skipped。

独立Worker实际启动后，在告警源503期间发布任务仍推进到Publishing；测试专用Redis键在停止后精确删除。组件集成显式使用1秒槽位、0秒查询延迟和30秒租约，provider为受控协议；领域300秒窗口用受控时钟检查。本阶段尚未宣称真实业务网关持续300/600/120秒告警或三源端到端完成，后续Task 14验证实际链路和配置。

规则读取超过两倍槽位间隔未评估则Unknown，最近有效观测仍保留各当前目标的最早真实成功时点。健康gauge有独立定义，节点覆盖完整且健康样本有效时可在零业务流量下Known；比例和时延缺样本仍Unknown。范围停用只暂停既有业务状态，不当成Recovered。
