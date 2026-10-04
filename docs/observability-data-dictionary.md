# 观测与告警数据字典

此字典对应真实控制台28–33页；遥测由Prometheus/Loki/Tempo存放，规则、评估状态、事件、流转与审计由PostgreSQL存放。原业务实体、Snapshot schema和历史原字节保持兼容；没有新增逐请求网关数据库写入。

## 统一查询与指标

|字段|类型与含义|
|---|---|
|range.start/end|UTC ISO时间，[start,end)，最大7天|
|sourceState|Available/NoData/Partial/Unavailable/Stale/NotApplicable；不能以0替代未知|
|observedAt|真实源观测事实；显示年龄，不当作当前健康证明|
|coverage|complete、missingNodes、reason、truncated；预期集合是启用的已登记节点|
|sampling|部署比例及模式；只影响Trace，远端未采样父节点仍受尊重|
|KPI value/unit/sampleCount/state|可空数值、单位、样本与独立状态|

12项KPI：request_count/request_rps/success_ratio/error_4xx_count/error_4xx_ratio/error_5xx_count/error_5xx_ratio/latency_p50_ms/latency_p95_ms/latency_p99_ms/unhealthy_destinations/cancelled_count。请求数是Prometheus采样增量的近似量；分位数先合并histogram再估算，以ms显示。成功仅正常完成的2xx/3xx，正文中途断开不计成功，即便已发200头。无流量时比例/时延为null。

7类趋势：RPS、成功率、4xx比例、5xx比例、P50/P95/P99；至多600点。分组None/Api/Application/Destination/Status，状态分组没有时延histogram则不伪造分位数。节点健康含EnvironmentId及真实Destination归属；API/Application过滤时后端健康为NotApplicable/null，节点不带环境级后端列表。采集完整且新鲜时RPS=0是有效数值，可触发低流量或恢复高流量规则；分母为0的比例、无样本时延仍为Unknown。

窗口首尾与固定15秒步长内的最大观测年龄共同检查45秒容差；中间缺口必须Partial。指标最多12个源请求，日志/Trace覆盖检查7个。网关三信号的丢弃/失败计数有零基线，last_loss_timestamp捕捉首次丢失，避免increase初始为0掩盖缺口。Collector内部8888诊断job检查对应信号的发送/入队失败、接收拒绝与采集连续性；首次出现非零计数也使Partial。共享Collector没有可信环境标签时对该信号保守覆盖所有查询环境，不返回原始诊断标签。该离散检查不证明毫秒连续性或生产SLA。源请求默认10秒、响应8MiB，超限/超时503。

## 日志与Trace

|字段|保留口径|
|---|---|
|id/time/environmentId|一次生成UUID、源纳秒时间投影UTC、可信部署环境|
|apiId/applicationKey/destinationId|实际管理资源UUID；Unmatched/Anonymous/Unknown/None为受控占位|
|method/pathTemplate/status/durationMs/outcome|方法与路由模板、实际已发送状态或null、完成耗时及固定分类|
|requestId/traceId|既有请求标识与W3C TraceId分别保存；不得互相冒充|
|maskedIp|IPv4掩码或IPv6 /48；精确筛选只在POST中转换成HMAC，不保存原IP|
|nodeName/configVersion/deploymentSequence|请求租用的实际generation和节点，不能在完成后读取新Current替代|
|nextCursor/truncated|签名游标绑定actor/scope/range/filter；不虚构总行数|
|spanId/parentSpanId/start/durationMs/kind/status/tags|实际已授权Server/Client span及白名单标签；partialTrace明确缺父或跨Scope投影|

日志页默认50/max100。Loki查询最多5000条；同纳秒边界ID最多64、签名游标4096字节，不能完整代表边界则显式truncated并无nextCursor。CSV至多10000条/8MiB，完整授权后才输出，公式字符中和，仍脱敏。Trace搜索最多200候选详情、并行4，每详情最多1000 span；忙范围需要缩小查询。相同TraceId不是权限凭据，服务端重新投影实际授权环境及资源。API Key、Secret/hash、Authorization、Cookie、query、body、任意异常文本从采集开始禁止。

## PostgreSQL告警表

|表|字段与约束|
|---|---|
|alert_rules|id UUID；organization_id必填、project_id/environment_id可空但实际祖先一致；name/normalized_name按Scope规范唯一，NULL同样参与唯一性；metric/expression/severity/enabled/for_seconds保留|
|规则补充|revision所有编辑并发版本；logic_revision仅逻辑/Scope/目标/级别/持续/启停改变；target_type/target_id、window_seconds、notification JSONB、created/updated_by/at|
|alert_evaluation_states|rule_id+logic_revision+environment_id+resource_key唯一；实际Scope、Phase、pending_since、last_evaluated_slot、last_success_at、last_condition、last_event_id、next_occurrence_no、evaluation_state、suppressed_at、lease_owner/until/token、revision|
|alert_events|冻结rule_revision/logic_revision、实际Scope/资源、occurrence_no、status/severity/message/rule_summary、condition_started_at/started_at/resolved_at、确认/静默/解决actor与时间原因、last_observed_at/value/condition、evaluation_state、revision|
|事件约束|同规则/环境/资源最多一个非Resolved事件；每逻辑版本/资源/occurrence唯一；历史祖先外键保留，不随规则后续Scope编辑重新归属|
|alert_event_transitions|id/event_id/from_status/to_status/actor_id/reason/occurred_at/correlation_id；系统actor为空；与事件状态和审计同事务保存|

规则名称1–128，for_seconds 0–86400，window_seconds 60–3600，severity Info/Warning/Critical。表达式严格三个由空白分隔的token：metric operator finite非负数；metric仅request_rps/error_5xx_ratio/latency_p95_ms/unhealthy_destinations，操作符 > >= < <= == !=。error_5xx_ratio阈值限定0–1。不运行任意表达式。健康仅Environment/Destination目标；API健康422，不能伪用全环境健康值。

组织归属不可编辑迁移，项目/环境可编辑；组织/项目宽范围要求该层写Scope，并纳入未来Active环境。每环境独立评估，实际Scope写入事件。revision与logic_revision分离：名称/通知改变不能重置Pending或人工抑制。事件revision仅生命周期命令并发，不随每次观测推进；元数据仍受行锁/隔离令牌保护。

## 状态与时钟

业务事件Open/Ack/Silenced/Resolved；评估Known/Unknown/ScopeInactive/ResourceRemoved等是独立状态。源不可用或缺样本中断Pending，不虚假恢复Firing。人工Resolve抑制同一持续故障；仅在人工处理之后的有效false观测解除抑制，下一次故障生成新occurrence。静默900–86400秒，DB写入时点导出relative duration，保留absolute until支持，两者不可同时传。

默认评估槽15秒/查询延后30秒/租约30秒/并行4；PG clock_timestamp决定slot、expiry与提交有效性。源查询在事务外，实际写入重新验证治理、规则revision/启停、Scope、lease token、有效期和slot；旧token不能写新事实。规则/事件超过2个间隔未评估显示Unknown；历史有效时间保留，当前数值/条件在过期时为null。

六项独立权限：metrics.read/log.read/trace.read/alert.read/alert.operate/alert.rule.manage；页面导航不带出未授权数据。普通源查询失败503；规则只读测试按设计返回200 Unknown/null，HTTP200本身不是条件成立证明。外部Email/Webhook/EnterpriseIm仅保存意向，当前无投递或回执。
