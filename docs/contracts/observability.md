# 监控查询与数据口径契约

公共DTO位于WebApi.Contracts/Observability，JSON字段为camelCase，SourceState为字符串枚举。时间统一UTC、范围[start,end)、最大7天；每页默认50、最大100，趋势最大600点。客户端时钟偏差允许30秒。

RPS单位req/s；请求数为时间窗口采样估计，不能作逐请求计费账本。成功率 / 4xx / 5xx比例为0–1；正常完成2xx / 3xx计成功，客户端中断不计成功；分位数为合并桶后估计，显示ms，底层histogram单位seconds。无请求时数量 / RPS可为0，比例 / 分位数为null；数据源故障或过期不得用0填补。

状态区分Available、NoData、Partial、Unavailable、Stale、NotApplicable。Coverage包含完整性、缺失节点、原因和truncated；采样配置与是否找到Trace分开。分页游标列表不返回假总数。

固定占位：ApplicationKey为Anonymous（匿名路由）或Unknown（未验证凭证）；未匹配API采用空ApiId及内部固定Unmatched标签；未选后端DestinationId为空。Outcome为Completed、AuthenticationFailed、AuthorizationFailed、ProxyError、TimedOut、ClientAborted或Unmatched；仅标准有限值可作为指标标签。

TraceId是W3C Activity的32位hex，RequestId保留既有网关请求标识；legacy header / error.traceId不改变语义。访问日志PathTemplate只存可信路由模板，不包含用户路径值、query或正文。IP显示掩码，精确查询仅服务端HMAC匹配，不将原IP写入URL、存储或CSV。

所有查询需服务端授权并生成可信环境集合；metrics.read、log.read、trace.read相互独立。DTO不得返回供应商原始对象、外部端点、密钥、凭证hash、Cookie或Authorization。规则与事件契约在后续任务增加，本文件不代表真实采集链路已经验收。
