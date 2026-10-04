# WebAPI Enterprise V2 真实监控与告警 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将原第28–33页接入真实双网关指标、脱敏访问日志、Trace和可审计告警，完成本机隔离环境的真实链路验收。

**Architecture:** Gateway异步导出OTLP到Collector，Prometheus / Loki / Tempo保存可观测性数据，Control Plane执行Scope授权与受限查询。现有Worker增加带数据库租约的告警评估循环，PostgreSQL保存规则、评估状态、事件和审计，复用现有React设计系统。

**Tech Stack:** 现有.NET10 / EF Core / PostgreSQL / YARP / React；OpenTelemetry SDK / Collector、Prometheus、Loki、Tempo。新依赖在Task 2真实兼容验证后固定补丁和镜像digest，不升级无关核心依赖。

**Spec:** `docs/superpowers/specs/2026-10-04-observability-alerting-design.md`；用户在审阅入口收到设计后回复“继续”，设计已确认。用户已回复“继续”确认本计划；沿用用户此前选择的Native方式，由当前会话逐项实施，完成后一次独立全分支审查。此前执行方式不代替本计划的审阅。

## Global Constraints

- 保留原页面功能、字段、角色权限、组织 / 项目 / 环境架构及方案 1 的高密度企业 UI，桌面 1440px 优先。
- 保留现有 Snapshot schema 和历史原字节。
- Gateway不访问PostgreSQL，不逐请求调用控制面，不等待监控写入。
- 采集上下文从实际租用的 generation 取得 API / Route / Cluster / version / sequence，最终目标从实际选中的 Destination 取得。
- 不得延长 AdmissionGate 持有期，不得在 `finally` 移除 generation 后重新读取 Current 代替旧请求上下文。
- 禁止 API Key、Secret / hash、Authorization、Cookie、数据库连接、URL query、请求或响应正文、任意异常原文及包含用户值的完整 URL。
- 禁止以 TraceId、requestId、原始路径、IP、凭证、异常文本、configVersion / sequence 创建时间序列标签。
- 本机验收配置 100% 采样，企业推荐值由部署验收决定。
- 默认最近 1h，提供 1h / 6h / 24h / 7d；时间边界为 `[start,end)`，最大跨度 7d；趋势最多 600 点。
- 查询超时默认 10 秒，响应大小上限 8 MiB；单次请求不自动无限重试。
- 列表默认 50 条、最大 100；日志导出当前过滤条件的 CSV，上限 10,000 条并显式标记截断。
- for_seconds 0–86400，window_seconds 60–3600；operator 支持 `> >= < <= == !=`。
- Worker默认每15秒评估，查询时点延迟30秒以容纳采集。
- Pending观测间隔不超过两倍评估间隔；数据中断 / Worker停机不计持续窗口，无数据不自动恢复告警。
- 静默持续15分钟到24小时并填原因；Resolved不重开原ID，再次故障创建新occurrence。
- Email / Webhook / 企业 IM 显示“待接入”，不得显示发送成功或执行外部投递。
- 监控服务、业务测试服务和本阶段测试数据使用随机专用项目名与独立卷，不修改既有本机验收账号、Scope或核心持久卷。
- 实施时检查工作区状态、按using-git-worktrees确认隔离需求；保护既有改动，只提交任务文件，不自动推送或合并。

## Review Focus

以下五类输入已补入对应任务测试，整体审查仍需检查其调用链，不能只看函数局部通过。

1. 客户端200响应头已发出但正文中途断开：记录ClientAborted，不计成功，不伪造已发送499；Task 3。
2. 不同环境故意传入同一traceparent：相同TraceId不得带出其他Scope的span / 日志；Task 7、14。
3. 单纳秒日志数量超过供应商查询上限：游标不得静默漏行，源限制必须显式truncated；Task 6。
4. 修改规则名称时存在Pending / 人工抑制：只增加revision，不清空logic_revision的评估状态；Task 10、11。
5. 过期Worker租约迟到提交，同时规则已禁用：旧token结果不能创建事件、恢复告警或改变新槽位；Task 11、14。

## 文件职责与公共契约

路径均相对`enterprise/`；新增Observability / Alerts文件按职责分开，不重构无关核心模块。Contracts保存安全DTO，Domain保存语法 / 统计 / 状态决策，Infrastructure保存源协议 / 授权查询 / EF事务，ControlPlane仅映射端点，Worker仅调度，console复用现有Shell与UI。

共用类型由Task 1建立，后续不得私自重命名：

- `TimeRange(DateTimeOffset Start, DateTimeOffset End)`；`SourceState`枚举Available / NoData / Partial / Unavailable / Stale / NotApplicable。
- `CoverageDto(bool Complete, IReadOnlyList<string> MissingNodes, string? Reason, bool Truncated)`；`SamplingDto(double Ratio, string Mode)`。
- `ObservationEnvelope<T>(SourceState SourceState, TimeRange Range, DateTimeOffset? ObservedAt, CoverageDto Coverage, SamplingDto? Sampling, T? Data)`。
- `ObservationScopeRequest(Guid OrganizationId, Guid ProjectId, Guid? EnvironmentId, bool AllAccessibleEnvironments)`。单环境 / 项目全可访问环境二选一，不接受跨组织全量。
- `MetricFilter(Guid? ApiId, Guid? ApplicationId, Guid? DestinationId, string GroupBy, int Page, int PageSize, string SortBy = "request_count")`；GroupBy为None / Api / Application / Status / Destination，SortBy为request_count / latency_p95_ms。
- `MetricValueDto(string Metric, double? Value, string Unit, long SampleCount, SourceState State = SourceState.Available)`；`MetricPointDto(DateTimeOffset Time, double? Value)`；`MetricGroupDto(string Key, string Name, IReadOnlyList<MetricValueDto> Values)`。
- `DestinationHealthDto(Guid ClusterId, Guid DestinationId, string Health)`；`NodeHealthDto(string NodeName, string Health, IReadOnlyList<DestinationHealthDto> Destinations)`；Health为Healthy / Unhealthy / Unknown / Disabled。
- `MetricsDto(IReadOnlyList<MetricValueDto> Kpis, IReadOnlyDictionary<string,IReadOnlyList<MetricPointDto>> Trends, IReadOnlyList<MetricGroupDto> Groups, int TotalGroups, int Page, int PageSize, IReadOnlyList<NodeHealthDto> NodeHealth)`；无样本比例 / 分位数Value=null，策略未启用的单KPI为NotApplicable。总览的Top APIs / Slow APIs / Top Apps分别用GroupBy + SortBy分页查询，所有请求共享同一截止时间。
- `LogFilter(Guid? ApiId, Guid? ApplicationId, string? Status, double? MinDurationMs, double? MaxDurationMs, string? Ip, string? Keyword, string? TraceId, int Limit, string? Cursor)`。
- `AccessLogDto(Guid Id, DateTimeOffset Time, Guid EnvironmentId, Guid? ApiId, string ApplicationKey, string Method, string PathTemplate, int? Status, double DurationMs, string Outcome, string RequestId, string? TraceId, string MaskedIp, string NodeName, long? ConfigVersion, long? DeploymentSequence, Guid? DestinationId)`。
- `CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool Truncated)`；不包含伪造Total。
- `TraceFilter(string? TraceId, Guid? ApiId, double? MinDurationMs, string? Outcome, int Limit, string? Cursor)`；`TraceSummaryDto(string TraceId, DateTimeOffset Start, double DurationMs, string Outcome)`。
- `TraceSpanDto(string SpanId, string? ParentSpanId, string Name, DateTimeOffset Start, double DurationMs, string Kind, string Status, IReadOnlyDictionary<string,string> Tags)`；`TraceDetailDto(string TraceId, bool PartialTrace, IReadOnlyList<TraceSpanDto> Spans)`。
- `TrustedObservationScope(Guid OrganizationId, Guid ProjectId, IReadOnlyList<Guid> EnvironmentIds)`位于Infrastructure，构造受限，仅由授权Resolver或规则系统Resolver产生，不作为客户端请求DTO。

告警共用类型由Task 9建立：

- `NotificationIntent(bool InConsole, IReadOnlyList<string> RequestedChannels)`；InConsole固定true，RequestedChannels仅Email / Webhook / EnterpriseIm意向，实际均未启用。
- `SaveAlertRuleRequest(Guid OrganizationId, Guid? ProjectId, Guid? EnvironmentId, string Name, string Metric, string Expression, string Severity, bool Enabled, int ForSeconds, string TargetType, Guid? TargetId, int WindowSeconds, NotificationIntent Notification)`。
- `AlertRuleDto(Guid Id, SaveAlertRuleRequest Definition, long Revision, long LogicRevision, string EvaluationState, DateTimeOffset? LastSuccessAt)`。
- `AlertEventDto`字段按设计§9.1逐项投影，包括Id / RuleId / RuleRevision / LogicRevision / Scope / ResourceKey / ResourceType / ResourceId / OccurrenceNo / Status / Severity / Message / StartedAt / ConditionStartedAt / ResolvedAt / AckedBy / AckedAt / SilencedBy / SilencedUntil / SilenceReason / ResolvedBy / ResolveReason / LastObservedAt / LastValue / LastCondition / EvaluationState / Revision及Transitions。
- `AlertTransitionDto(Guid Id, string? FromStatus, string ToStatus, Guid? ActorId, string Reason, DateTimeOffset OccurredAt, string CorrelationId)`为Transitions元素；Scope使用现有ScopeRef，LastValue / LastCondition允许null。
- `AlertListFilter(string? Severity, string? Source, string? Status, int Page, int PageSize)`；`RuleTestDto(string EvaluationState, IReadOnlyList<MetricGroupDto> Matches, bool? Condition)`。
- `AlertAction(string Kind, string? Reason, DateTimeOffset? Until)`，Kind为Ack / Resolve / Silence / Unsilence；`AlertRuleExpression(string Metric, string Operator, double Threshold)`。
- `EvaluationInput(DateTimeOffset Slot, DateTimeOffset? ObservedAt, double? Value, bool? Condition, SourceState SourceState)`位于Contracts；未知源允许ObservedAt=null。
- `EvaluationPhase`枚举Inactive / Pending / Firing / SuppressedUntilRecovery；`EvaluationStateSnapshot(EvaluationPhase Phase, DateTimeOffset? PendingSince, DateTimeOffset? LastSuccessAt, bool? LastCondition, DateTimeOffset? SuppressedAt)`；`EvaluationDecision(EvaluationPhase NewPhase, DateTimeOffset? PendingSince, bool? LastCondition, bool CreateEvent, string? ResolveReason)`位于Domain，由Infrastructure从持久化`AlertEvaluationState`映射；Domain不能引用EF实体或Infrastructure。SuppressedAt为人工解决的权威时间，仅ObservedAt晚于该时间的有效false才能重新布防，避免延迟查询拿解决前的旧数据当成“后续恢复”。

## 验证与记录约定

沿用`./scripts/check.sh domain|integration|gateway`和`./scripts/check-console.sh`；新增`./scripts/check-observability.sh smoke|e2e|faults|browser`由Task 2 / 14实现。参数过滤使用`--filter FullyQualifiedName~<测试类名>`；0失败0跳过才通过，缺源明确失败，不静默Skip。

集成测试复用`tests/WebApi.TestSupport/Support/ApiFixture.cs`（命名空间仍为`WebApi.Integration.Tests.Support`）及真实PostgreSQL；协议测试可以使用记录请求的HttpMessageHandler，不能把它当成真实Prometheus / Loki / Tempo验收。Gateway组件测试可用内存Exporter证明字段与不阻塞；Task 14必须补齐真实跨容器链路。

每任务遵循先失败测试 → 记录预期失败 → 最小实现 → 指定检查通过 → 只提交任务文件。证据记入`docs/evidence/observability/implementation-record.md`，记录基准 / 变更提交、检查输出、实际配置与限制；不复制秘密值。任务1开始才建立该记录，本文所有复选框当前保持未完成。

## Task 1: 查询契约、口径校验及测试支持

**Files:** Create `src/WebApi.Contracts/Observability/ObservationContracts.cs`、`MetricsContracts.cs`、`LogContracts.cs`、`TraceContracts.cs`；`src/WebApi.Domain/Observability/ObservationQueryValidator.cs`；`tests/WebApi.Domain.Tests/ObservationQueryTests.cs`；`tests/WebApi.TestSupport/Support/ObservationTestSupport.cs`；`docs/contracts/observability.md`。

**Interfaces:** Produces本计划公共观测DTO；`ObservationQueryValidator.Validate(TimeRange range, int limit, DateTimeOffset now) : void`，非法返回领域校验结果映射422。`ObservationTestSupport.GrantAsync(ApiFixture fixture, string[] permissions, string mode, CancellationToken ct) : Task`仅在fixture隔离数据库补测试授权；`RecordingSourceHandler.Enqueue(string json, int status)`及`Requests`仅作协议测试记录。

- [x] 写`ObservationQueryTests.SevenDaysAndHundredRowsAreAllowed`、`EightDaysOr101RowsAreRejected`及`ReversedRangeAndFutureEndRejected`，核心断言：`Assert.Throws<ArgumentException>(() => ObservationQueryValidator.Validate(new(now.AddDays(-8), now), 50, now)); Assert.Throws<ArgumentException>(() => ObservationQueryValidator.Validate(new(now, now.AddMinutes(1)), 50, now));`。fixture测试仅授予本例六类权限，不改变默认角色。
- [x] 运行`./scripts/check.sh domain --filter FullyQualifiedName~ObservationQueryTests`，先确认因缺校验实现失败，不能用已知基础测试替代RED。
- [x] 实现DTO和校验：Start<End、范围≤7d、End≤server now+30秒（容忍客户端时钟小偏差）、limit1–100，时间统一UTC；指标null与0分开。建立文档中的单位 / 固定outcome / 固定Unknown与Anonymous键；准备隔离fixture测试支持。
- [x] 同一命令通过，并检查原Domain测试未退化；公共DTO字段名在JSON camelCase保持一致。
- [x] 提交`feat: define scoped observability contracts and validation`，只包括上述文件及本任务记录。

## Task 2: 固定依赖与隔离三源协议栈

**Files:** Create `deploy/compose.observability.yml`、`deploy/observability/collector.yaml`、`prometheus.yaml`、`loki.yaml`、`tempo.yaml`；`scripts/check-observability.sh`、`scripts/observability-smoke.mjs`；`tests/observability/deployment.test.mjs`；`docs/deployment/observability-local.md`。Modify `Directory.Packages.props`、Gateway / Infrastructure相关`.csproj`及其`packages.lock.json`、`deploy/images.lock.json`；为源验证需要的依赖设置锁定，不安装图表库。

**Interfaces:** Produces脚本命令`smoke`：随机项目名、临时秘密目录、独立卷，输出安全metadata文件包含project / endpoints / timing，EXIT清理精确本项目；`Observability:*`配置键为Enabled、CollectorEndpoint、PrometheusUrl、LokiUrl、TempoUrl、CredentialSecretFile、IpHmacSecretFile、TraceSampleRatio。

- [x] 写`deployment.test.mjs`，断言镜像均有digest、服务无公网端口、Loki启用structured metadata、Prometheus只抓取Collector、三类pipeline接收OTLP；初始`assert.ok(files.includes('collector.yaml'))`失败。为smoke设置明确断言`requestCounter >= 1 && logMarkerFound && traceFound`，不得仅测health。
- [x] 使用已发现Node运行该测试，确认缺配置失败；记录当前.NET10 / CPU架构而非猜测版本。
- [x] 核验并锁定稳定依赖、OTLP协议和Exporter组件。配置7d保留目标与磁盘限额、memory limiter / batch / retry有界；IP HMAC测试秘密只写临时0600文件。smoke通过合成OTLP payload证明三源实际收录，并检查SDK所选批处理方案有可观测的丢弃 / 失败计数接口；否则在Task 3采用显式有界processor，不能省略诊断。
- [x] 运行`./scripts/check-observability.sh smoke`，三源实际查询找到标记、清理后0本项目容器 / 卷 / 秘密文件；`dotnet restore --locked-mode`及项目编译通过。此项仅证明协议，不宣称Gateway链路完成。
- [x] 提交`build: pin isolated otlp observability stack`；依赖锁文件变更须列实际版本和digest。

## Task 3: 请求采集、脱敏、运行版本及健康观测

**Files:** Create `src/WebApi.Gateway/Observability/RequestTelemetryContext.cs`、`RequestTelemetryMiddleware.cs`、`GatewayTelemetryRecorder.cs`、`TelemetrySanitizer.cs`、`TelemetryDropTracker.cs`、`GatewayHealthObserver.cs`、`TelemetryRegistration.cs`；`tests/WebApi.Gateway.Tests/GatewayTelemetryTests.cs`；`tests/WebApi.Gateway.Tests/Support/RecordingTelemetrySink.cs`。Modify `GatewayApp.cs`、`Security/ApiKeyMiddleware.cs`、`Configuration/WeightedDestinationMiddleware.cs`及`GatewayFixture.cs`必要测试配置。

**Interfaces:** `GatewayTelemetryRecorder.Record(RequestTelemetryContext context) : void`不得执行网络I/O；`TelemetrySanitizer.Path(string? routeTemplate) : string`、`Ip(IPAddress address) : (string Masked, string Hmac)`；`TelemetryDropTracker.Record(string signal, long count) : void`；`GatewayHealthObserver.Read() : IReadOnlyList<DestinationObservation>`，Observation包含EnvironmentId / NodeName / 管理ClusterId / DestinationId / ObservedAt / Health(Healthy/Unhealthy/Unknown/Disabled)。RequestTelemetryContext保留实际generation上下文、最终outcome、W3C TraceId及legacy RequestId。

- [x] 写`LateOldRequestKeepsOldSequenceAndDestination`、`InvalidCredentialUsesUnknownApp`、`Aborted200HeadersNotCountedSuccessful`、`SensitiveMarkersAbsentFromAllSignals`、`QueueFullDoesNotBlockProxy`和`HealthRequestsExcluded`。断言示例：`Assert.Equal(oldSequence, oldRequest.DeploymentSequence); Assert.Equal("ClientAborted", aborted.Outcome); Assert.DoesNotContain(secretMarker, capturedSignals); Assert.True(droppedCount > 0);`；测试后端可人为延迟及断开。
- [x] 运行`./scripts/check.sh gateway --filter FullyQualifiedName~GatewayTelemetryTests`，确认缺采集事实导致失败。
- [x] 从请求实际lease复制上下文，认证成功才附加App，负载均衡完成后附加实际Destination；finally仅记录一次且不延长准入锁。保留旧header / error.traceId，补w3cTraceId和标准传播。计数器`webapi_gateway_requests_total`、histogram`webapi_gateway_request_duration_seconds`、gauge`webapi_telemetry_last_observed_timestamp_seconds`；histogram秒bucket为0.005/0.01/0.025/0.05/0.1/0.25/0.5/1/2.5/5/10/30/60，查询转ms。runtime Cluster ID / version仅日志Trace，指标用管理UUID；健康gauge来自真实YARP状态，采集周期15秒。应用资源白名单及OTel自动属性脱敏；显式有界队列容量2048，batch≤512，丢弃计数、导出失败计数和恢复可查询。
- [x] 运行指定测试和原`GatewayRuntimeTests`均通过，内存Exporter明确只作组件证据；验证无监控overlay时现有代理 / LKG继续运行。Activity / LogRecord生命周期不得保存可复用已释放buffer。
- [x] 提交`feat: record bounded sanitized gateway telemetry`。

## Task 4: Scope限定的真实指标查询与接口

**Files:** Create `src/WebApi.Infrastructure/Observability/TrustedObservationScope.cs`、`ObservationScopeResolver.cs`、`ObservationSourceSettings.cs`、`PrometheusMetricSource.cs`、`ObservationQueryService.cs`、`ObservationCoverageService.cs`；`src/WebApi.ControlPlane/Observability/MetricsEndpoints.cs`；`tests/WebApi.Integration.Tests/ObservationMetricsTests.cs`。Modify `ControlPlaneApp.cs`注册；不更改RuntimeSnapshot契约。

**Interfaces:** `ObservationScopeResolver.ResolveAsync(ActorContext actor, string permission, ObservationScopeRequest scope, Guid? apiId, Guid? appId, Guid? destinationId, CancellationToken ct) : Task<TrustedObservationScope>`；`PrometheusMetricSource.QueryAsync(TrustedObservationScope scope, TimeRange range, MetricFilter filter, CancellationToken ct) : Task<ObservationEnvelope<MetricsDto>>`；`ObservationQueryService.MetricsAsync(ActorContext actor, ObservationScopeRequest scope, TimeRange range, MetricFilter filter, CancellationToken ct)`返回同类型。

- [x] 写`EnvironmentOnlyGrantCannotReadSiblingMetrics`、`ProjectAllReturnsOnlyAllowedEnvironments`、`HistogramBucketsMergeBeforeQuantile`、`MissingNodeMakesPartialNotZero`、`CounterResetNeverProducesNegativeRps`、`SourceFailureReturns503`。断言`Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); Assert.Null(staleKpi.Value); Assert.DoesNotContain(foreignEnvironmentId.ToString(), sourceRequest.Query);`；协议handler仅辅助验证查询串，CP / PostgreSQL真实运行。
- [x] 运行`./scripts/check.sh integration --filter FullyQualifiedName~ObservationMetricsTests`，确认新端点缺失或授权查询失败。
- [x] 实现`/observability/metrics`和`/observability/apis/{apiId}/metrics`；API归属校验合并到目标环境ResourceRef，不能让环境grant因读取项目级API归属而误拒绝，也不能放宽到其他环境。强制环境条件、UUID校验、escape、关闭redirect、10秒 / 8MiB、固定endpoint与SecretFile；Domain校验异常统一映射422，合法状态0与null分开。按节点reset后聚合、600点、统一截止时间、Null口径、source响应二次校验、45秒新鲜度与启用节点覆盖。KPI键固定request_count / request_rps / success_ratio / error_4xx_count / error_4xx_ratio / error_5xx_count / error_5xx_ratio / latency_p50_ms / latency_p95_ms / latency_p99_ms / unhealthy_destinations / cancelled_count，后续规则按相同metric键取值。unhealthy_destinations为同环境DestinationId去重后“至少一节点真实Unhealthy”的数；节点不一致逐节点显示，Unknown / Disabled不计Healthy也不充当规则恢复。已知Exporter丢弃纳入Partial原因；Circuit / RateLimit固定NotApplicable。
- [x] 指定测试通过，scope撤销后的下一真实GET拒绝；单节点过期不能当零成功，全部无样本为NoData。添加协议实际metric导出名检查接Task 2已锁定映射。
- [x] 提交`feat: query real metrics within authorized environments`。

## Task 5: 监控总览及API详情页面

**Files:** Create `console/src/api/observability.ts`、`console/src/observability/query-state.mjs`及`.d.mts`、`TrendChart.tsx`、`SourceNotice.tsx`；`console/src/pages/MetricsOverview.tsx`、`ApiMetrics.tsx`；`console/tests/observability-query.test.mjs`。Modify `Shell.tsx`、`main.tsx`、`navigation.mjs`必要查询参数支持、`styles/tokens.css`；不改变既有导航guard语义。

**Interfaces:** `loadMetrics(scope: ObservationScopeRequest, range: TimeRange, filter: MetricFilter, signal: AbortSignal): Promise<ObservationEnvelope<MetricsDto>>`；`parseObservationSearch(search: string): ObservationSearch` / `serializeObservationSearch(state: ObservationSearch): string`，ObservationSearch含range(1h/6h/24h/7d) / api / app / status / duration / trace / group / cursor及可选内存ip，serialize明确忽略ip；`TrendChart({points,unit,label})`为可访问SVG与同数据表格摘要，不安装未批准图表依赖。

- [x] 写URL纯函数测试：`assert.equal(parseObservationSearch('?range=24h').range,'24h'); assert.equal(serializeObservationSearch({range:'1h',ip:'10.0.0.1'}).includes('10.0.0.1'),false);`；Scope切换重置游标和Abort旧请求。记录浏览器RED：当前两个原路径仍未接真实内容。
- [x] 运行`./scripts/check-console.sh`确认新测试预期失败；不把原构建通过当作新页面已完成。
- [x] 实现28 / 29页KPI、趋势、Top分页与App / status / Destination分组，1440px复用tokens；tooltip带时间 / 单位 / 样本量、缺数据保留null。跳转沿用API / App详情及之后日志 / Trace路径；按目标权限控制入口，规则创建预填API / 环境但需Task 13真正写入。
- [x] 前端检查通过；真实管理员空Scope / 新鲜指标 / NoData / Partial / 503分别浏览器验证。Back / refresh保留查询，旧请求不能覆盖新Scope，权限拒绝清除图表。保存无秘密截图和控制台错误检查。
- [x] 提交`feat: connect metrics overview and api monitoring pages`，O1阶段真实指标链路另在Task 14复验。

## Task 6: 日志查询、稳定游标及安全导出

**Files:** Create `src/WebApi.Infrastructure/Observability/LokiLogSource.cs`、`ObservationCursorCodec.cs`、`AccessLogQueryService.cs`、`CsvLogExporter.cs`；`src/WebApi.ControlPlane/Observability/LogsEndpoints.cs`；`tests/WebApi.Integration.Tests/ObservationLogsTests.cs`。Modify `ControlPlaneApp.cs`源注册。

**Interfaces:** `ObservationCursorCodec.Encode(string kind, Guid actorId, TrustedObservationScope scope, TimeRange range, string filterHash, long boundaryNanoseconds, IReadOnlyList<Guid> boundaryIds) : string`；`Decode(string cursor, string kind, Guid actorId, TrustedObservationScope scope, TimeRange range, string filterHash) : CursorBoundary`校验签名 / 身份 / Scope / 过滤hash / 15分钟有效期，`CursorBoundary(long Nanoseconds, IReadOnlyList<Guid> BoundaryIds)`保留源纳秒而非从DateTimeOffset回算。`AccessLogQueryService.QueryAsync(ActorContext actor, ObservationScopeRequest scope, TimeRange range, LogFilter filter, CancellationToken ct) : Task<ObservationEnvelope<CursorPage<AccessLogDto>>>`；`DetailAsync(ActorContext actor, ObservationScopeRequest scope, TimeRange range, Guid logId, CancellationToken ct)`返回ObservationEnvelope<AccessLogDto>；`ExportAsync(ActorContext actor, ObservationScopeRequest scope, TimeRange range, LogFilter filter, Stream output, CancellationToken ct) : Task<ExportOutcome>`，`ExportOutcome(int Rows, bool Truncated)`。

- [x] 写`EqualNanosecondRowsCrossBoundaryWithoutLoss`、`ProviderCapReturnsExplicitTruncated`、`CursorCannotChangeEnvironmentOrUser`、`IpFilterIsHmacAndNeverStoredRaw`、`CsvFormulaIsNeutralized`、`RevocationStopsExportPages`。断言`Assert.Equal(expectedIds, pages.DistinctIds); Assert.True(capped.Coverage.Truncated); Assert.DoesNotContain("10.0.0.1", storedLog); Assert.StartsWith("'", dangerousCell);`。
- [x] 运行`./scripts/check.sh integration --filter FullyQualifiedName~ObservationLogsTests`，确认缺源适配 / 游标 / 导出行为。
- [x] 实现强制环境LogQL、纯文本Keyword、结构metadata映射、50 / 100限制、固定end与时间+logId顺序、投递ID去重；边界同纳秒超源能力时停止并明示truncated，不能编造页数。IP输入内存传参→HMAC；游标签名密钥SecretFile。CSV保留同一过滤与截止时间，忽略列表当前cursor，从查询首行开始 / 最多10,000行 / 公式中和，每个源分页重新授权；CSV先写入8MiB上限的服务端临时内存buffer，达到行数 / 大小限额即truncated，失败或撤权则丢弃buffer。读取结束再授权并发送已知行数 / 截断header和CSV，避免先发headers再试图补行数或将中途错误当成功文件。
- [x] 指定测试通过；真正LokiOTLP字段名 / 转义格式在smoke栈查询核验，外部原始JSON不返回浏览器。
- [x] 提交`feat: query and export scoped sanitized access logs`。

## Task 7: Trace查询、瀑布图数据与跨Scope裁剪

**Files:** Create `src/WebApi.Infrastructure/Observability/TempoTraceSource.cs`、`TraceQueryService.cs`、`TraceProjection.cs`；`src/WebApi.ControlPlane/Observability/TracesEndpoints.cs`；`tests/WebApi.Integration.Tests/ObservationTraceTests.cs`。Modify `ControlPlaneApp.cs`注册。

**Interfaces:** `TraceQueryService.SearchAsync(ActorContext actor, ObservationScopeRequest scope, TimeRange range, TraceFilter filter, CancellationToken ct) : Task<ObservationEnvelope<CursorPage<TraceSummaryDto>>>`；`DetailAsync(ActorContext actor, ObservationScopeRequest scope, string traceId, TimeRange range, CancellationToken ct) : Task<ObservationEnvelope<TraceDetailDto>>`；`TraceProjection.Project(string traceId, IReadOnlyList<SourceSpan> spans, TrustedObservationScope scope) : TraceDetailDto`。`SourceSpan(Guid? EnvironmentId, string SpanId, string? ParentSpanId, string Name, DateTimeOffset Start, double DurationMs, string Kind, string Status, IReadOnlyDictionary<string,string> Attributes)`为适配器内部解析类型，Scope只从可信Resource属性解析，不接受客户端标签，SourceSpan不作为API原样输出。

- [ ] 写`SameTraceIdAcrossEnvironmentsDoesNotLeak`、`MissingScopeSpanOmittedAndPartialMarked`、`MetricsPermissionDoesNotGrantTrace`、`UnsampledNotFoundDiffersFromSourceFailure`、`SpanTimesRemainReal`。断言`Assert.Single(detail.Spans); Assert.True(detail.PartialTrace); Assert.DoesNotContain(foreignTag, serializedDetail); Assert.Equal(HttpStatusCode.ServiceUnavailable, sourceFailure.StatusCode);`。
- [ ] 运行`./scripts/check.sh integration --filter FullyQualifiedName~ObservationTraceTests`，确认缺Trace端点或裁剪行为。
- [ ] 构造受限TraceQL搜索，验证32位hex非全0TraceId，按ID获取仍重验span属性；无可信Scope的后端span舍弃。时间游标和查询截止保持固定，无法稳定跨边界则明确truncated。保留真实parent关联 / duration，无授权parent时标部分链路；默认W3C ID与legacy requestId分开。
- [ ] 指定测试通过；真实Tempo返回server / client span，并验证sourceState、sampling信息及没有敏感URL / Header属性。
- [ ] 提交`feat: expose scope-filtered trace search and details`。

## Task 8: 访问日志与Trace页面联动

**Files:** Create `console/src/pages/AccessLogs.tsx`、`Traces.tsx`；`console/src/observability/CursorPagination.tsx`、`TraceWaterfall.tsx`；`console/tests/observability-cursor.test.mjs`。Modify `api/observability.ts`、`main.tsx`、`Shell.tsx`、`styles/tokens.css`。

**Interfaces:** 前端`loadLogs(scope: ObservationScopeRequest, range: TimeRange, filter: LogFilter, signal: AbortSignal): Promise<ObservationEnvelope<CursorPage<AccessLogDto>>>`；`loadTraces(scope,range,filter: TraceFilter,signal): Promise<ObservationEnvelope<CursorPage<TraceSummaryDto>>>`；`loadTraceDetail(scope,range,traceId: string,signal): Promise<ObservationEnvelope<TraceDetailDto>>`，scope / range / signal类型同loadLogs；`CursorPagination({nextCursor,onNext,onPrevious,truncated})`不显示伪总数；`TraceWaterfall({detail: TraceDetailDto})`输出真实span位置 / 安全标签与表格回退。

- [ ] 写查询 / cursor测试，断言更改filter后旧cursor无效，Back恢复已读页但Scope切换清空，IP不会写入URL；浏览器记录原30 / 31页缺真实内容的RED。
- [ ] 运行前端检查确认新游标测试失败。
- [ ] 实现源列与完整筛选、详情抽屉、CSV当前查询导出、trace复制 / 双向跳转、瀑布图 / span标签、采样 / 部分链路提示。日志过滤IP仅内存；链接继承环境 / API / 时间，不在前端替代服务端授权。
- [ ] `./scripts/check-console.sh`通过；浏览器验证30 / 31页查询、分页、跳转、复制、导出、NoTrace、Unavailable和撤权清空；1440px无页面横向溢出，键盘可操作，无浏览器错误。
- [ ] 提交`feat: connect access logs and trace investigation pages`。

## Task 9: 告警数据迁移及表达式领域规则

**Files:** Create `src/WebApi.Contracts/Alerts/AlertContracts.cs`；`src/WebApi.Domain/Alerts/AlertExpressionParser.cs`；`src/WebApi.Infrastructure/Persistence/Entities/AlertRule.cs`、`AlertEvent.cs`、`AlertEvaluationState.cs`、`AlertEventTransition.cs`及对应`Configurations/*Configuration.cs`；迁移`20261004030000_ObservabilityAlerts.cs` / `.Designer.cs`；`tests/WebApi.Domain.Tests/AlertExpressionTests.cs`、`tests/WebApi.Integration.Tests/AlertPersistenceTests.cs`。Modify `WebApiDbContextModelSnapshot.cs`及`CoreConstraints.cs`的告警约束部分。

**Interfaces:** Produces告警公共类型及四实体；`AlertExpressionParser.Parse(string metric, string expression) : AlertRuleExpression`；`Compare(AlertRuleExpression expression, double? value) : bool?`；合法metric为request_rps / error_5xx_ratio / latency_p95_ms / unhealthy_destinations。评估state键、租约token及索引按设计§9.1。

- [ ] 写`OperatorsAndFiniteThresholdsAreAccepted`、`RawPromQlAndNaNRejected`及PG`OnlyOneActiveOccurrenceForNullableResourceId` / `WrongScopeForeignKeyRejected`。断言`Assert.Throws<ArgumentException>(() => Parse("error_5xx_ratio", "sum(rate(secret[5m])) > 0")); Assert.Null(Compare(expr, null)); Assert.Equal(1, activeCount);`，两个事务并发第二条事件冲突。
- [ ] 运行Domain / Integration上述类，确认缺表 / parser导致失败；不能用EF InMemory。
- [ ] 保留全部源字段，建立新增字段、精确UTC时间、revision / logic_revision、nullable资源+非空resource_key、唯一键与部分索引、transitions追加事实；规则名称按Scope规范化唯一。TargetType Environment要求TargetId=null且每个匹配环境各一评估键；Api / Destination必须指定TargetId，API规则可跨同项目环境，Destination规则固定单环境。表达式只能三个token及受限操作符 / 有限数字；比例阈值0–1、非负RPS / latency / unhealthy阈值，for0–86400和window60–3600，字段错误422。
- [ ] 两类检查通过；从首期已有数据库仅迁移新增结构，原核心记录 / Snapshot原bytes不变，迁移脚本不seed规则或扩权。
- [ ] 提交`feat: persist alert governance and restricted expressions`。

## Task 10: 告警规则治理、Scope预览及只读测试

**Files:** Create `src/WebApi.Infrastructure/Alerts/AlertRuleScopeResolver.cs`、`AlertRuleService.cs`；`src/WebApi.ControlPlane/Alerts/AlertRuleEndpoints.cs`；`tests/WebApi.Integration.Tests/AlertRuleTests.cs`。Modify `ControlPlaneApp.cs`注册、`AuditedCommandExecutor.cs`仅添加脱敏告警字段白名单。

**Interfaces:** `AlertRuleScopeResolver.ResolveForActorAsync(ActorContext actor, SaveAlertRuleRequest definition, CancellationToken ct) : Task<IReadOnlyList<Guid>>`检查完整规则写Scope；`ResolveForSystemAsync(AlertRule rule, CancellationToken ct) : Task<IReadOnlyList<Guid>>`仅枚举已持久化Active环境，不创建Actor。`AlertRuleService.ListAsync(ActorContext actor, ObservationScopeRequest scope, int page, int pageSize, CancellationToken ct) : Task<PageResult<AlertRuleDto>>`包含当前Scope相关且用户可完整管理的继承规则；`DetailAsync(ActorContext actor, Guid id, CancellationToken ct) : Task<AlertRuleDto>`；`CreateAsync(ActorContext actor, SaveAlertRuleRequest request, CancellationToken ct) : Task<AlertRuleDto>`；`UpdateAsync(ActorContext actor, Guid id, SaveAlertRuleRequest request, string etag, CancellationToken ct) : Task<AlertRuleDto>`；`SetEnabledAsync(ActorContext actor, Guid id, bool enabled, string etag, CancellationToken ct) : Task<AlertRuleDto>`；`TestAsync(ActorContext actor, SaveAlertRuleRequest request, CancellationToken ct) : Task<RuleTestDto>`使用Task 4的指标源，一环境一查询，按相同metric键和目标过滤取值，不另写一套跨Scope查询。

- [ ] 写`EnvironmentGrantCannotCreateOrganizationRule`、`WideScopePreviewIncludesFutureEnvironmentPolicy`、`RenamingKeepsLogicRevision`、`LogicChangeResolvesOldOccurrence`、`RuleTestDoesNotPersistOrNotify`、`StaleEtagRetainsDatabaseRevision`。断言`Assert.Equal(before.LogicRevision, renamed.LogicRevision); Assert.Equal("RuleChanged", closed.ResolveReason); Assert.Equal(0, createdEventsByTest); Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);`。
- [ ] 运行`./scripts/check.sh integration --filter FullyQualifiedName~AlertRuleTests`，确认新规则端点缺失。
- [ ] 实现列表 / 详情 / 创建 / 编辑 / 启停 / 只读test全部契约；实际事务重新授权、ETag / 幂等、CSRF。宽Scope要求对应宽写grant，新Active环境纳入预览说明；宽规则任何目标缺观测时总EvaluationState=Unknown，LastSuccessAt取所有当前目标最近成功时间的最小值，不能用一环境成功掩盖其他环境未知。仅name / notification编辑不改变logic_revision，其余相关修改关闭旧活动事件并重置评估。name / expression / notification安全投影审计，渠道意向不存密钥或任意URL。
- [ ] 检查通过；test对缺样本返回Condition=null / Unknown；作者后来撤权不删除已授权规则，但不可再编辑。已停用Scope不自动恢复活动告警。
- [ ] 提交`feat: manage scoped alert rules with safe live tests`。

## Task 11: 连续窗口、租约防重及Worker评估

**Files:** Create `src/WebApi.Domain/Alerts/AlertEvaluationMachine.cs`；`src/WebApi.Infrastructure/Alerts/AlertEvaluationLeaseStore.cs`、`AlertEvaluationService.cs`、`AlertSystemAudit.cs`；`src/WebApi.Worker/Workers/AlertEvaluationWorker.cs`；`tests/WebApi.Domain.Tests/AlertEvaluationMachineTests.cs`、`tests/WebApi.Integration.Tests/AlertEvaluationTests.cs`。Modify `WorkerApp.cs`注册；发布 / Outbox任务独立。

**Interfaces:** `AlertEvaluationMachine.Decide(EvaluationStateSnapshot state, EvaluationInput input, int forSeconds, int intervalSeconds) : EvaluationDecision`；`ClaimAsync(DateTimeOffset slot, string owner, CancellationToken ct) : Task<IReadOnlyList<EvaluationLease>>`，`EvaluationLease(Guid RuleId, long RuleRevision, long LogicRevision, Guid EnvironmentId, string ResourceKey, DateTimeOffset Slot, long Token, DateTimeOffset LeaseUntil)`；`AlertEvaluationService.EvaluateAsync(EvaluationLease lease, CancellationToken ct) : Task<bool>`为true表示该token结果实际提交，false表示过时结果被丢弃。Claim先按持久化规则及ResolveForSystemAsync的环境建立缺失评估键，再锁内认领，不依赖已发生告警才建立state。

- [ ] 写`Continuous300SecondsFiresOnlyAfterWindow`、`UnknownAnd31SecondGapResetPending`、`ManualSuppressionSurvivesRenameUntilFalse`、`RecoverySampleBeforeManualResolveCannotRearm`、`LeaseExpiredResultCannotCommitAfterDisable`、`TwoWorkersSameSlotCreateOneEvent`。断言`Assert.False(at299.CreateEvent); Assert.True(at300.CreateEvent); Assert.Null(afterGap.PendingSince); Assert.Equal(EvaluationPhase.SuppressedUntilRecovery, earlierFalse.NewPhase); Assert.False(await staleLeaseCommit); Assert.Equal(1, eventCount);`，domain用受控时间，集成租约使用PG时间 / 并发实际事务。
- [ ] 运行Domain / Integration对应类，确认缺状态机和Worker评估事实失败。
- [ ] 实现15秒槽位、30秒query delay、两倍间隔连续性、持久Pending / Firing / SuppressedUntilRecovery；query无事务锁，租约默认30秒、10秒源超时，DB权威时间 / token递增 / lease / revision / Slot重验。有效false恢复；Unknown保留已有状态，for0可首个有效true触发。所有落库命令按现有治理锁8901202→rule→evaluation state→event统一顺序执行短事务，规则修改 / 人工操作 / Worker提交及到期任务采用同一顺序；系统事件 / transition / audit同事务，system UserId=null，audit明确实际事件ID / 冻结环境。评估重复true仅观测，不写高频治理审计。
- [ ] 检查通过；Worker重启、source断开与恢复、规则重命名 / 逻辑修改、WideScope新增环境和资源确实退休分别验证。测试CPU时钟倒退不推进槽位，故障不影响现有PublishWorker循环。
- [ ] 提交`feat: evaluate alerts with durable windows and fenced leases`。

## Task 12: 告警列表、处理命令、到期与原子审计

**Files:** Create `src/WebApi.Infrastructure/Alerts/AlertEventService.cs`、`AlertSilenceExpiryService.cs`；`src/WebApi.ControlPlane/Alerts/AlertEventEndpoints.cs`；`tests/WebApi.Integration.Tests/AlertActionTests.cs`。Modify `ControlPlaneApp.cs`及`AlertEvaluationWorker.cs`调用独立静默到期循环；不等待外部源才执行到期。

**Interfaces:** `AlertEventService.ListAsync(ActorContext actor, ObservationScopeRequest scope, AlertListFilter filter, CancellationToken ct) : Task<PageResult<AlertEventDto>>`；`DetailAsync(ActorContext actor, Guid id, CancellationToken ct) : Task<AlertEventDto>`；`ActAsync(ActorContext actor, Guid id, AlertAction action, string etag, CancellationToken ct) : Task<AlertEventDto>`；`AlertSilenceExpiryService.ExpireAsync(CancellationToken ct) : Task<int>`。PageResult复用项目已有通用DTO，端点所有命令接事务内IdempotentCommandExecutor。

- [ ] 写`SilenceAckThenExpiryRestoresAck`、`ResolveSuppressesPersistentTrueUntilRecovery`、`ResolvedCannotReopen`、`DuplicateKeyChangesOnlyOnce`、`ScopeRevokedAtCommitBlocksAction`、`AuditFailureRollsBackEventAndTransition`。断言`Assert.Equal("Ack", expired.Status); Assert.Equal(beforeCount, duplicateTransitionCount); Assert.Equal(beforeRevision, rolledBack.Revision);`。
- [ ] 运行`./scripts/check.sh integration --filter FullyQualifiedName~AlertActionTests`，确认缺命令状态变化。
- [ ] 实现原四状态、权限分离、实际环境ResourceRef授权、readScope禁止operate、ETag / 幂等 / 原因、15m–24h静默；对Silenced确认保持Silenced，保留首位确认人。到期Ack或Open，不复活Resolved；人工Resolve同事务设置抑制状态，真实恢复重新布防，新故障新ID。范围筛选仅返回有权事件，source固定Metrics，指标链接仍需metrics.read。
- [ ] 指定测试通过；多Worker同时静默到期只一次transition / audit，撤权实际事务竞争后零未授权变更；403 / 404不泄漏其他环境rule摘要。
- [ ] 提交`feat: operate alert lifecycles with transactional audit`。

## Task 13: 告警中心与规则页面

**Files:** Create `console/src/api/alerts.ts`；`console/src/pages/AlertCenter.tsx`、`AlertRules.tsx`；`console/src/observability/RuleEditor.tsx`、`AlertActionDialog.tsx`；`console/tests/alert-editor.test.mjs`。Modify `main.tsx`、`Shell.tsx`、`api/types.ts`必要类型、`styles/tokens.css`、`pages/ApiMetrics.tsx`预填创建入口。

**Interfaces:** `loadAlerts(scope: ObservationScopeRequest, filter: AlertListFilter, signal: AbortSignal): Promise<PageResult<AlertEventDto>>`；`saveRule(id: string | null, request: SaveAlertRuleRequest, etag: string | null, idempotencyKey: string): Promise<AlertRuleDto>`；`testRule(request: SaveAlertRuleRequest, signal: AbortSignal): Promise<RuleTestDto>`；`actOnAlert(id: string, action: AlertAction, etag: string, idempotencyKey: string): Promise<AlertEventDto>`。RuleEditor复用现有useUnsaved / Editor冲突行为，state含definition / dirty / latestRevision / latestLogicRevision，不把test结果视为保存成功。

- [ ] 写纯表单测试，断言当前输入412后不变、修改name保留forSeconds、Unknown测试Condition=null、源三种延期渠道无“已发送”；浏览器RED记录32 / 33页尚未接真实接口。
- [ ] 运行前端检查，确认新增表单状态测试预期失败。
- [ ] 实现severity / env / source / status筛选、详情transitions、Ack / Resolve / Silence / Unsilence、关联指标；规则创建 / 编辑 / 启停 / 表达式校验 / Scope预览 / 实际只读测试。展示规则窗口和页面时间差异、数据新鲜度 / Unknown、人工解决抑制解释、通知待接入。API监控跳规则预填需真正保存及后续触发可验。
- [ ] `./scripts/check-console.sh`通过；浏览器用隔离项目验证规则创建 / test / 修改 / 并发412 / 禁用、告警处理四状态、表单侧栏 / Back / Scope切换保护、权限按钮与真实拒绝；中文文案和1440px截图留证。
- [ ] 提交`feat: connect alert center and rule governance pages`。

## Task 14: 真实三源闭环、故障恢复及浏览器QA

**Files:** Create `tests/WebApi.EndToEnd.Tests/ObservabilityLoopTests.cs`、`ObservabilityFaultTests.cs`；`tests/WebApi.EndToEnd.Tests/ObservabilityScenario.cs`；`scripts/observability-e2e.mjs`、`observability-faults.mjs`；`docs/evidence/observability/verification.json`、`verification-output.md`、`browser-qa.json`及脱敏截图。Modify `scripts/check-observability.sh`、`deploy/compose.observability.yml`仅测试场景overlay；`src/WebApi.TestBackend/Program.cs`仅添加验收专属慢响应 / 5xx / 断开路径，不向生产代理新增管理接口。

**Interfaces:** `ObservabilityScenario.CreateAsync() : Task<ObservabilityScenario>`读取本次随机fixturemetadata，不输出秘密；提供真实Publish / GatewayRequest / Query / RuleCommand / WaitForCondition。命令`e2e|faults|browser`启动与维护精确本次项目；browser保持fixture直到检查结束，退出销毁本次容器 / 卷 / 合成秘密，不重建持久测试账号。

- [ ] 写闭环断言：两节点真实请求→Prometheus原始counter正确 / 比例误差明确 / Loki日志→Tempo server/client spans→三类示例规则依forSeconds触发→Ack / Silence / 恢复→审计。跨环境同traceparent仅返回当前环境span；单纳秒源上限显式截断；queuefull仍成功代理；原A请求跨B切换完成A且遥测仍A。`Assert.Equal("Open", alert.Status); Assert.Equal("Recovered", recovered.ResolveReason); Assert.Equal("A", oldLog.DestinationBackend); Assert.Empty(foreignSpans);`，DestinationBackend为scenario根据真实DestinationId映射的测试事实，不新增API字段。
- [ ] 在空三源或未采集情况下跑指定新E2E，确认真实断言失败；不能只用缺依赖退出证明业务RED，至少留一次三源运行但新闭环事实不足的失败记录。
- [ ] 完成测试夹具 / fault控制及证据采集，使用真实源、真实CP、PG、两Gateway、独立审核会话、两个Worker。验收可缩短window / interval但保留配置记录；另以受控domain时钟验证300 / 600 / 120秒原语义，不能声称短E2E实际等待原时长。秘密只经内存 / 0600合成文件使用，不截图一次性Secret或输出其全文。
- [ ] 运行`./scripts/check-observability.sh e2e`、`faults`、`browser`全部通过：Collector / Prometheus / Loki / Tempo单独停机与恢复、单节点采集过期、Worker重启 / 租约接管；业务代理和核心发布 / 回滚 / LKG继续成立。6页真实操作、1440px、无页面水平溢出、错误日志空；原核心完整回归及前端检查通过。结束确认0本次容器 / 卷 / 秘密文件，保留安全证据；非本机目标列未验收。
- [ ] 提交`test: verify real observability and alerting fault recovery`；此时若任何关键项缺证据，verification.complete必须false。

## Task 15: 独立整体审查、修正及交付

**Files:** Create `docs/evidence/observability/final-review.md`、`docs/observability-data-dictionary.md`、`docs/deployment/observability-runbook.md`；Modify `docs/console-coverage.md`、`console/src/coverage.json`、`docs/decisions.md`、`scripts/package.sh` / `deliverables/manifest.json`按实际范围更新；源码包不含Secret / runtime / cache / .git。

**Interfaces:** 使用前14项代码提交作为不可变审查基准；保留一次全分支独立审查及同一修正阶段，不自动推送或合并。实施方式Native的整体审查依executing-plans规定执行；本计划编写期间不派子agent。

- [ ] 请求一次独立全分支审查，提供设计、计划、基准、Review Focus及实际证据；发现Critical / Important不得以文档提示代替修正；Minor如延期必须逐项理由与证据。
- [ ] 对实际发现先写能复现问题的失败测试 / 浏览器证据，再在同一修正阶段修正；仅对变化和未决风险补验证，避免无依据反复全量跑或二次独立复审。
- [ ] 形成最终验证记录，明确三源链路 / 6页真实行为 / 本机CPU架构 / 采样和测试interval；生产TLS、容量 / SLA、HA、企业端点、外部通知均不凭本机结果宣称完成。更新28–33页能力映射按实际项标记。
- [ ] 用最终源提交生成源码与验收包，检验ZIP完整性 / SHA256 / manifest / 必需源码与证据 / 排除秘密；操作手册包含启动、源不可用、队列丢弃、Pending中断、静默 / 人工抑制、精确测试清理和依赖配置。
- [ ] 只提交本阶段交付文档和metadata，给用户提供可运行入口与交付包；满足全部必需项才设本阶段complete=true。保留首期交付与原40页原型。

## 计划自检与执行入口

覆盖关系：设计§1–3→Task 1 / 2 / 14 / 15，§4→Task 3，§5→Task 1 / 4 / 5，§6→Task 6 / 7 / 8，§7→Task 5 / 8 / 13，§8→Task 9 / 10，§9→Task 9 / 10 / 11 / 12，§10→Task 4 / 6 / 7 / 10 / 12，§11→Task 2 / 14 / 15，§12→Task 14 / 15，§13→Task 2协议核验。五项Review Focus各有指定失败检查。

自检要求：所有任务接口使用本计划已定义类型或项目现有类型；每个新增业务任务有明确RED / GREEN，全部源页面字段及交互均有责任任务。具体依赖补丁由Task 2以真实编译 / 协议输出选定，迁移生成后固定上述文件名并校验设计snapshot，不能留下实现期空占位。

本计划保存时仅修改设计确认状态和新增本文；未安装产品依赖、启动新服务、修改产品代码或运行本阶段验收。用户审阅通过后，沿用Native当前会话实施，先读取executing-plans与适用工作区规则再执行Task 1。
