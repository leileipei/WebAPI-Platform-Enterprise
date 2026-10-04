# 监控查询与数据口径契约

公共DTO位于WebApi.Contracts/Observability，JSON字段为camelCase，SourceState为字符串枚举。时间统一UTC、范围[start,end)、最大7天；每页默认50、最大100，趋势最大600点。客户端时钟偏差允许30秒。

RPS单位req/s；请求数为时间窗口采样估计，不能作逐请求计费账本。成功率 / 4xx / 5xx比例为0–1；正常完成2xx / 3xx计成功，客户端中断不计成功；分位数为合并桶后估计，显示ms，底层histogram单位seconds。无请求时数量 / RPS可为0，比例 / 分位数为null；数据源故障或过期不得用0填补。

状态区分Available、NoData、Partial、Unavailable、Stale、NotApplicable。Coverage包含完整性、缺失节点、原因和truncated；采样配置与是否找到Trace分开。分页游标列表不返回假总数。

固定占位：ApplicationKey为Anonymous（匿名路由）或Unknown（未验证凭证）；未匹配API采用空ApiId及内部固定Unmatched标签；未选后端DestinationId为空。Outcome为Completed、AuthenticationFailed、AuthorizationFailed、ProxyError、TimedOut、ClientAborted或Unmatched；仅标准有限值可作为指标标签。

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
