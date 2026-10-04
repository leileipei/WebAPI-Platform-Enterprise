# 监控查询与数据口径契约

公共DTO位于WebApi.Contracts/Observability，JSON字段为camelCase，SourceState为字符串枚举。时间统一UTC、范围[start,end)、最大7天；每页默认50、最大100，趋势最大600点。客户端时钟偏差允许30秒。

RPS单位req/s；请求数为时间窗口采样估计，不能作逐请求计费账本。成功率 / 4xx / 5xx比例为0–1；正常完成2xx / 3xx计成功，客户端中断不计成功；分位数为合并桶后估计，显示ms，底层histogram单位seconds。无请求时数量 / RPS可为0，比例 / 分位数为null；数据源故障或过期不得用0填补。

状态区分Available、NoData、Partial、Unavailable、Stale、NotApplicable。Coverage包含完整性、缺失节点、原因和truncated；采样配置与是否找到Trace分开。分页游标列表不返回假总数。

固定占位：ApplicationKey为Anonymous（匿名路由）或Unknown（未验证凭证）；未匹配API采用空ApiId及内部固定Unmatched标签；未选后端DestinationId为空。Outcome为Completed、ProxyError、Timeout或ClientAborted；仅标准有限值可作为指标标签。

TraceId是W3C Activity的32位hex，RequestId保留既有网关请求标识；legacy header / error.traceId不改变语义。访问日志PathTemplate只存可信路由模板，不包含用户路径值、query或正文。IP显示掩码，精确查询仅服务端HMAC匹配，不将原IP写入URL、存储或CSV。

所有查询需服务端授权并生成可信环境集合；metrics.read、log.read、trace.read相互独立。DTO不得返回供应商原始对象、外部端点、密钥、凭证hash、Cookie或Authorization。规则与事件契约在后续任务增加，本文件不代表真实采集链路已经验收。

## 网关采集字段与诊断

指标、日志使用 100% 请求计数；仅 Trace 应用 ParentBased / TraceIdRatioBased 采样。Gateway 只注册其受控 ActivitySource，真实 business server 与代理 client span 使用标准 traceparent 关联，不采集自动 URL、body、异常、events、links、baggage 等内容，不生成未插桩后端内部 spans。

OTLP resource 白名单：service.name=webapi-gateway，service.instance.id=部署 NodeName，webapi.environment.id=部署 EnvironmentId。指标 data point 使用管理 UUID：webapi.api.id / webapi.application.id / webapi.destination.id；Unmatched / Unknown / Anonymous / None 是固定系统占位。请求 counter 另有 http.response.status_code / webapi.outcome / webapi.success；时延 histogram 不携带这些额外状态标签。ConfigVersion、DeploymentSequence、RouteId、ApiVersionId、RuntimeClusterId 只进日志与 Trace。

Collector 下划线转换后，环境、API、应用、目标的查询标签分别为 webapi_environment_id / webapi_api_id / webapi_application_id / webapi_destination_id。histogram 秒边界为 0.005、0.01、0.025、0.05、0.1、0.25、0.5、1、2.5、5、10、30、60，查询显示时乘 1000 转 ms。SDK 每条 instrument 的 cardinality limit 为 10000，overflow 会增加 metrics 诊断缺口，不能视作各资源聚合完整。

日志 Body 固定 gateway.request，其 attributes 包含 webapi.log.id / webapi.request.id / webapi.api.id / webapi.application.id / webapi.destination.id / http.request.method / url.template / webapi.duration.ms / http.response.status_code / webapi.outcome / webapi.success / webapi.client.ip_masked / webapi.client.ip_hmac / webapi.node.name / webapi.config.version / webapi.deployment.sequence / webapi.api.version.id / webapi.route.id / webapi.runtime.cluster.id / webapi.cluster.id。顶层 traceId / spanId 使用 W3C hex。IP 来自实际连接，IPv4 留前三段、IPv6 留前48bit；精确查询需对规范化地址做 HMAC-SHA256。没有启用可信代理配置时不信任 X-Forwarded-For。

同一请求只生成一条访问日志。正常请求 outcome=Completed，按真实 2xx/3xx计成功；断开为 ClientAborted，已发送200仍不计成功，未发送头时 Status=None；代理错误 ProxyError，超时 Timeout。后端返回5xx仍是正常收到HTTP响应的 Completed，但 success=false。

显式容量最多2048个拥有独立JSON存储的条目，单批最多512条。SDK MetricPoint、Activity 与 LogRecord 缓冲不跨生命周期保留，记录请求时无网络I/O。导出只在后台执行，5秒超时，Collector response 最大64KiB，不记录响应错误原文。

诊断指标：webapi_telemetry_dropped_total / webapi_telemetry_export_failures_total 按 signal=metrics/logs/traces 统计条目缺口；webapi_telemetry_last_export_success_timestamp_seconds 记录信号最近导出成功时间；webapi_telemetry_last_observed_timestamp_seconds 每次默认15秒指标采集输出节点观察时间。webapi_destination_health 由实际YARP状态读取，1=Healthy，0=Unhealthy，-1=Unknown，-2=Disabled；当前运行快照不含已禁用目标，禁用展示由查询层数据库状态补全。

## 日志查询及 CSV 约定

GET /api/v1/observability/logs及/logs/{logId}、GET /logs/export使用固定范围和授权Scope。IP仅通过POST /logs/query或POST /logs/export的JSON请求体提交；所有POST沿用Cookie CSRF保护，读取操作不产生业务写入。URL中的ip直接422，不忽略。筛选含API/App/可选Destination、状态/状态段、时延上下限、纯文本Keyword和TraceId。

游标HMAC密钥配置Observability:CursorSigningSecretFile，IP密钥配置IpHmacSecretFile，均为至少32字节base64的SecretFile。游标绑定actor、组织/项目/授权环境集合的hash、固定起止时间及过滤hash，15分钟失效；携带原始纳秒和已输出边界ID，不能从DTO的100ns时间精度回算。Loki一次请求最多5000行，安全DTO按纳秒+logId倒序及ID去重。同时间边界达到源上限或64个游标边界ID上限时明确truncated且不发下一游标；游标总长≤4096，避免超出当前HTTP请求行预算。

实际Loki 3.7.8 OTLP下划线命名和metadata展平形状在log-contract-smoke.json验证；解析同时支持展平stream标签及第三个row metadata对象，二次校验环境与资源归属；不返回body或未知字段。Keyword只对允许的脱敏字段做line_format后文字匹配，不将输入当原生LogQL/正则。官方格式说明：[OTLP映射](https://grafana.com/docs/loki/latest/send-data/otel/)与[HTTP API](https://grafana.com/docs/loki/latest/reference/loki-http-api/)，实际锁定版协议证据优先。

CSV从相同过滤与截止时间的首行重新读取，忽略列表cursor；最多10000行/8MiB，公式及前置空白危险字符中和。每源分页及发送前重授权，失败/撤权丢弃buffer；完成后设置已知X-WebApi-Export-Rows / X-WebApi-Export-Truncated / X-WebApi-Source-State再发CSV。Source-State包含采集覆盖缺口，行数不代表完整采集历史。日志/Trace覆盖使用已授权环境中的节点gauge和对应signal丢弃/失败诊断，不需要metrics.read，且不暴露指标内容；覆盖源失败保持Partial，不把未知当完整。整体来源503仅返回安全sourceType(metrics/logs/traces)、retryAfterSeconds=5与Retry-After:5，不返回外部源地址/错误原文。

## Trace 查询（第 31 页）

`GET /api/v1/observability/traces` 支持同指标范围、TraceId、API、minDurationMs、Outcome=Success/Error、limit（1–100）及签名 cursor；`GET /api/v1/observability/traces/{traceId}` 返回已授权 spans。只需独立 `trace.read`；不隐含日志或指标权限。TraceId 必须为非零 32 位 W3C hex，供应商 Base64 ID 在适配器中归一化。可信环境只从 resource attributes 读取；span 标签不能授予范围权限。原始 URL、Header、异常、events、links、baggage 及未在安全白名单中的属性不会进入 DTO。无授权父节点时清除其标识并标记 partialTrace，不补造 span。

搜索元数据可能来自跨环境 root，平台只用候选 TraceId，重新取回并投影详情。汇总时间及 Success/Error 来自本范围 span，不使用供应商 root duration/name。每查询最多 200 候选，4 个并发，10 秒总预算；每详情最多 1000 span、8 MiB 源响应。候选上限或同时间超过游标 64 个 ID 上限明确 truncated，不生成不可靠下一页。未截断时按可见 Start + TraceId 倒序，以固定 range 和用户/范围/筛选绑定的签名时间游标翻页；不虚构总数。数据在采集入库期间仍可能晚到，刷新从首页重新查。

`sampling` 显示部署配置（默认 0.1，ParentBasedTraceIdRatioBased）。404 表示当前范围未找到，可能未采样或过保留期；源故障为 503 且 sourceType=traces，两者分开。源响应后再次核验授权。

固定 Tempo 3.1.0 实测返回 batches、Base64 trace/span/parent ID 和枚举字符串，受限 resource/API TraceQL search 可用。`trace-contract-smoke.json` 仅证明合成 OTLP 协议；测试中的捕获响应验证适配器投影，真实业务网关与查询 API 的端到端验收仍留待 Task 14。[Tempo API](https://grafana.com/docs/tempo/latest/api_docs/) 与 [TraceQL most_recent](https://grafana.com/docs/tempo/latest/traceql/construct-traceql-queries/) 说明供应商搜索限制，不能替代平台授权。

## 告警规则治理（Task 10）

规则创建/编辑/启停复用Cookie、CSRF、If-Match、幂等键和治理事务锁。任何规则操作检查其完整写Scope；几个环境grant不能替代组织/项目宽grant。预览POST `/observability/alert-rules/preview`返回当前Active环境ID和未来Active环境自动纳入政策，不创建持久记录。

同名唯一使用trim、NFKC和Invariant uppercase规范化，宽Scope的SQL NULL同样参与唯一约束。规则OrganizationId在创建后固定；项目/环境范围仍可修改，跨组织需要新建并停用旧规则。原因是历史事件冻结组织归属，不能随规则租户迁移。修改name/notification只增加revision；判断逻辑、目标、Scope、severity、for/window或enabled改变增加logic_revision，并同事务关闭旧活动事件、追加流转及审计。仅站内中心启用，Email/Webhook/EnterpriseIm只保存意向枚举，不允许URL或密钥。

`unhealthy_destinations`目前只支持环境或具体Destination目标。健康gauge没有API依赖关系，API目标返回422；API仍支持RPS、5xx比例和P95时延。不能借用整个环境的健康值表示单API健康。

POST `/observability/alert-rules/test`和preview只读，但仍要求CSRF，不要求幂等键。只读test使用规则管理权限的可信范围调用原指标源，不隐含metrics.read。缺样本、缺节点、部分/过期来源以及来源503均返回Unknown、Condition=null；故障目标的MetricValue.State=Unavailable，HTTP200不表示条件通过。结构和权限错误仍按正常Problem Details返回。测试不保存规则、事件、治理审计或通知。数据返回后重验授权及环境集合。

已有规则的管理授权按其持久归属Scope判断，允许查看并修复被删除目标的规则；新提交的目标引用重新验证。宽规则只有当前每个环境的最新logic_revision目标都有Known及成功时点才显示Known，LastSuccessAt取这些时点的最小值。

## 持久评估与租约（Task 11）

默认 Alerts:IntervalSeconds=15、QueryDelaySeconds=30、LeaseSeconds=30、MaxConcurrentEvaluations=4。Worker每秒唤醒以继续有界批次，实际每个资源只提交一个当前PG槽位，避免追补历史。槽位、租约有效期及提交时间用clock_timestamp；CPU时钟倒退不推进或认领旧槽位。MaxConcurrentEvaluations允许1–16，批次等于该并发数，查询每个资源用独立DbContext、10秒来源超时。

所有持久写按治理锁8901202、rule、evaluation、event顺序；外部来源I/O不在事务中。提交校验当前revision、logic_revision、enabled、实际Active环境/目标、单调token、真实有效期及last_evaluated_slot。重命名期间的旧修订查询不提交结果，但释放其仍有效的旧租约，保留Pending和人工抑制，允许当前修订重查。

未知来源重置Pending；Firing与人工抑制保持。暂停Scope也保持Firing和SuppressedUntilRecovery，事件标ScopeInactive；真实恢复再Resolved。资源实际删除/离开目标归属才以ResourceRetired关闭，来源不再返回序列不等于删除。有效观测时间必须递增，人工解决后的恢复还必须晚于SuppressedAt。事件创建时间是实际数据库提交时间，condition_started_at来自连续有效观测起点。

规则当前槽位超过两倍间隔则Unknown，LastSuccessAt保留所有当前目标已有有效观测时点的最小值；没有某个目标的成功事实时null。UI应称“最近有效观测”，显示真实年龄和当前状态。健康指标有独立gauge：节点覆盖完整且健康样本有效可在无业务请求时Known；不能把没有比例/分位样本解释成恢复。系统trigger/recovery/retirement追加transition和UserId=null审计，重复true仅更新观测事实。
