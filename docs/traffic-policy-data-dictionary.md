# 流量策略数据字典

|对象|字段|定义|
|---|---|---|
|Policy|Id / OrganizationId / ProjectId|源策略身份、组织、可选项目；无环境字段，范围创建后不变|
|Policy|Name / Type / Config / Enabled / VersionNo|1–128 字符名称、不可变类型、严格 JSON、工作启用状态、If-Match 修订|
|RoutePolicyBinding|RouteId / PolicyId / Priority|整组路由绑定，Priority 0–1000 用于展示排序，不改变固定执行顺序|
|RuntimePolicy|Id / SourcePolicyId / SourceRevision|运行身份与源身份分离；2.1 身份由源 ID、修订、类型、规范化配置稳定生成|
|RuntimeRoute|PolicyBindings|引用运行策略 ID；同源策略在部分发布中可保留多个修订|
|PolicyDecisionDto|PolicyId / PolicyType / PolicyRevision / Decision / RejectionReason|源 ID、两种高级类型、修订、安全枚举决策与固定原因；日志/Trace最多两条|
|GatewayNode|SupportedSnapshotSchemas|当前实例显式声明能力；未知、缺失或实例不匹配按 2.0 处理|

认证：mode=ApiKey/Anonymous，默认 ApiKey。超时：timeoutMs 1–300000，默认 30000。

限流：algorithm 固定 TokenBucket；keyBy=Route/ApplicationRoute；refillTokens 与 burst 为 1–1000000，burst 不少于 refillTokens；windowMs 10–600000；满桶补充不超过 3600000ms；redisFailureMode=Reject/Allow。表单默认 ApplicationRoute、1000、1000ms、1500、Reject。

熔断：samplingWindowMs 为 1000–300000 的整秒；minimumRequests 1–1000000；failureRatio 大于 0、不超过 1；openDurationMs 1000–300000；halfOpenMaxRequests 1–10；halfOpenSuccesses 1–100；failureStatusCodes 不重复且只含 500–599，最多 100 项；countTimeouts 与 countConnectionFailures 布尔，至少一个故障来源。默认状态码 500/502/503/504，两故障开关开启。

工作引用、冻结发布、desired 与节点当前已应用引用保护源策略删除；单纯历史引用不阻止删除，历史快照内容自足可回滚。引用列表按权限分页，不可见保护只返回泛化原因。

指标 `webapi_gateway_policy_decisions_total` 仅五维：环境、节点、源策略 ID、类型、决策；无应用、修订、请求或 Trace 维度。请求决策限流为 Allowed/Exceeded/StoreRejected/Bypass，熔断为 Allowed/HalfOpenProbe/OpenRejected；状态转移另计 Opened/HalfOpened/Closed。`circuit_rejected_count` 只计 OpenRejected；`rate_limit_store_unavailable_count` 计 StoreRejected 和 Bypass，不能冒充成功限流。单位 requests，缺覆盖为空。

路由编辑 DTO：`timeoutMs` 为持久基础值，`effectiveTimeoutMs` 为只读工作配置有效值；编译后的运行路由 `timeoutMs` 仍为策略折叠后的值。普通路径编辑不把覆盖值写回基础值。Redis 令牌桶有效时间取当前 TIME 与上次时间的最大值，时钟回拨不重复补充已计算时间。熔断新建默认失败状态码为完整 500–599。
