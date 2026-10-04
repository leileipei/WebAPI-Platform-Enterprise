# 可观测与告警最终审查

2026-10-04。一次独立全分支审查，基准 f08585773c744592cb8bb6a7e62a72b8a5a4827a，审查提交 7a72d30d6e6c8e85d11860e271874b0f2a04710c；随后由实施者完成同一修正阶段，无第二次独立复审。

## 发现及处置

|级别|实际问题|修正及失败→通过证据|
|---|---|---|
|Important|第一次丢失前计数器没有零基线，首次非零计数的increase可能为0|Gateway DiagnosticsHaveZeroBaselinesBeforeAnyLoss、FirstLossHasActualTimestampEvenBeforeCounterBaseline先失败后通过；三信号零基线与真实last_loss_timestamp；日志/Trace首次丢失覆盖4项回归先失败后通过；真实8秒Collector中断返回known_logs_collection_gap|
|Important|Collector已经接收、下游异步重试最终失败，查询仍能宣称完整|增加内部8888诊断reader和Prometheus固定job，按信号查询send_failed/enqueue_failed/receiver_refused、首次非零序列与job中断；FirstLossAndCollectorDownstreamFailureCannotClaimComplete先失败后通过。实际Loki/Tempo停止42秒，网关失败增量0、Collector up=1、下游失败6/12，对应历史查询Partial，见final-loss-live.json|
|Important|采集完整、RPS=0仍被当成Unknown，低流量不能触发、高流量不能恢复|ZeroRpsIsKnownAndRecoversHighThenTriggersLowTrafficRule先Unknown失败后通过；高流量实际PG事件Recovered、低流量新事件Open；比例和时延无样本仍Unknown|
|Important|已打开的28/29页面没有周期性权限重验|生产产物无HMR、真实三源临时只读用户；另一会话撤销范围后52秒仍残留126/127样本为RED；加入30秒固定窗口自动重验与session.error门禁后，同条件撤销约9秒自动清除两页全部KPI和表格；见final-permission-browser.json及ui/final-metrics-revocation-red/green.png|
|Important|API/应用监控将全环境的无关后端健康归因给选中资源|FilteredResourceDoesNotAttributeUnrelatedEnvironmentBackendHealth(api/app)两项先返回无关1而失败、后NotApplicable/null通过；节点不带环境后端列表，UI明确该维度暂不支持、健康格为—；1440px documentWidth=1440，见ui/final-api-health-scope.png|
|Minor deferred|TraceProjection将数字webapi.config.version当GUID过滤，Span中不显示配置版本|延期。版本可从关联访问日志查看，不影响Trace范围隔离/瀑布；后续单独复现并修正，不扩大本次修正阶段。|

五项Review Focus均审查通过：200响应头后中断的Success=false；相同Trace跨环境投影；单纳秒截断；重命名保留logicRevision/Pending/抑制；旧lease token和禁用规则不得迟写。单纳秒大量关键词格式化存在显式503性能限制，未作为容量或SLA通过证据。

## 验证范围

最终完整组件回归：Domain49/49、Integration191/191、Gateway27/27，均0跳过；Frontend35/35、TypeScript和production build通过。修复专项真实服务结果与清理见两个final-*.json。Task14的三源闭环、原阈值三规则、九项故障、两环境同Trace、5001行截断、六页1440px及普通请求两Span事实保留在原证据，来源提交d34b697。最终代码完整三源闭环及交付包封存结果由verification.json和manifest-observability.json记录，未封存前complete=false。

审查未将生产TLS/企业证书、HA/容量/SLA、7天实际持久化、AMD64实际运行和外部Email/Webhook/IM送达列为本机完成项。页面映射、文档和源码封装属于计划Task15，由最终门禁验证。完整决策及错判成本保存在execution-ledger.md。
