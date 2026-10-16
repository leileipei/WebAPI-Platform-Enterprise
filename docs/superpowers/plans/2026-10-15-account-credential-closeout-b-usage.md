# 第4包B：Credential统计 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. 当前会话逐项执行，不派发子代理。

**Goal:** 提供按环境汇总的认证通过次数、最近使用及可信完整性状态，故障不影响API认证。

**Architecture:** Gateway认证后记录有界增量，单写者持久checkpoint和不可变封批，独立内部1.0协议补报。CP以发布快照验证来源，在PostgreSQL原子去重/累加；查询按历史在岗节点及覆盖区间判断完整性。

**Tech Stack:** 现有.NET/EF Core/PostgreSQL、Gateway LKG私有卷、System.Text.Json/TimeProvider、xUnit；无新的消息队列或遥测后端。

**Spec:** [规格§6–10、§12、P4-04–07](../specs/2026-10-15-account-credential-closeout-design.md)。同时遵守[总计划](2026-10-15-account-credential-closeout.md)。

## Global Constraints

- Auth热路径不做网络、数据库、同步磁盘/fsync；统计故障不能改变API认证/授权结果。
- 保留Snapshot2.0–2.3及原Register/Heartbeat DTO；新协议为1.0。节点Bearer认证和事务内Enabled/实例复验不可省略。
- Default/min/max：FlushIntervalSeconds=15/5/60；CheckpointIntervalMs=1000/100/5000；MaxInMemoryKeys=10000/100/20000；MaxPendingBytes=64/8/128MiB；MaxBatchEntries=1000/1/1000；MaxBatchBytes=256/16/256KiB；MaxPendingAgeHours=24/1/24；Enabled=true。
- 1批≤2快照，JSON≤256KiB/深度8，每项Count=1..1000000；未来>2分钟拒绝，过去仅接收配置补报期内(最大24h)。并发处理2(1–4)/预算10秒。
- 1个在途、HTTP3秒、指数退避1–30秒带抖动；每15秒空items脉冲仍上报连续覆盖。gap独立控制预算1MiB。
- Hourly/Coverage默认30天(1–90)，Hash默认25h(2–168且≥MaxPendingAgeHours+1)，清理≤1000行/事务；活跃游标和节点墓碑不随桶清理。
- 计数decimal string；Unknown/Unavailable为null，不将未知当0，不改变业务Revision/UpdatedAt/Snapshot；不提交、不触及原4192。

## Review Focus

1. B1/B3：重复JSON字段、越界时间、伪造旧快照/不同API授权窗口必须拒绝，不能仅校验当前Credential存在。
2. B3/B6：ACK丢失、换发送实例、相同序号不同Hash、已退役回执必须各有稳定结果。
3. B5：磁盘满、半写、损坏、checkpoint回退、易失尾部丢失不得伪造Complete或重封同一增量。
4. B2/B7：大整数、整点/夏令时输入、保留期变化及历史节点退役不能制造错误零次或泄漏跨环境LastUsed。
5. B4/B7：请求冻结的旧配置仍合法时计数；后续工作状态与当前在线节点不能改写历史事实。

---

## B1共享契约目录（后续任务只引用）

全部记录不可变。下面字段为C#类型/属性名；JSON camelCase，UTC输出`Z`；所有时间规范化到UTC ticks后作核心Hash，整数严格解析，不接受浮点计数。

```csharp
public sealed record UsageSource(Guid ReleaseId,long ConfigVersion,long DeploymentSequence,string PayloadHash);
public sealed record UsageInterval(DateTimeOffset From,DateTimeOffset To);
public sealed record UsageGap(DateTimeOffset From,DateTimeOffset To,string Reason);
public sealed record UsageItem(Guid ApplicationId,Guid CredentialId,Guid ApiId,
 DateTimeOffset GrantValidFrom,DateTimeOffset? GrantExpiresAt,UsageSource Source,
 DateTimeOffset HourStart,long Count,DateTimeOffset FirstObservedAt,DateTimeOffset LastObservedAt);
public sealed record UsageBatchCore(string Protocol,Guid ProducerInstanceId,Guid CollectorId,long Sequence,
 DateTimeOffset SealedAt,DateTimeOffset DurableThrough,IReadOnlyList<UsageInterval> Intervals,
 IReadOnlyList<UsageGap> Gaps,IReadOnlyList<UsageItem> Items);
public sealed record UsageBatchRequest(Guid SenderInstanceId,UsageBatchCore Core);
public sealed record UsageCollectorRequest(string Protocol,Guid SenderInstanceId,Guid CollectorId);
public sealed record UsageReceipt(string Result,long NextExpectedSequence,DateTimeOffset? AcceptedThrough);
public sealed record UsageObservation(Guid EnvironmentId,UsageItem Item);
```

`UsageItem.Count`在请求观察时为1，封批时按完整键累加/拆分；键为Environment、Application/Credential/Api、匹配Grant窗口、UsageSource及UTC小时。空pulse允许Intervals非空而Items为空。`UsageReceipt.Result`仅Registered/Applied/AlreadyApplied；冲突及Retired走受控错误响应含expected，不能把410伪装Applied。Intervals为半开区间；gap优先于覆盖。规范化按完整条目键/区间起止/原因排序，JSON对象属性使用现有CanonicalJson；数组排序先于序列化，重复键拒绝。SenderInstanceId不进核心Hash，Core所有字段均进入。

### Task B1：协议、边界验证与启动配置

**Files:**
- Create: `src/WebApi.Contracts/CredentialUsage/UsageProtocolContracts.cs`、`src/WebApi.Domain/CredentialUsage/UsageBatchRules.cs`、`src/WebApi.Gateway/CredentialUsage/CredentialUsageOptions.cs`、`src/WebApi.Infrastructure/CredentialUsage/CredentialUsageRetentionOptions.cs`。
- Test/Create: `tests/WebApi.Domain.Tests/UsageBatchRulesTests.cs`、`tests/WebApi.Gateway.Tests/CredentialUsageOptionsTests.cs`。

**Interfaces:** 产生上方全部record；`UsageBatchRules.Parse(ReadOnlyMemory<byte> utf8,DateTimeOffset now,int maxAgeHours):UsageBatchRequest`严格解析；`UsageBatchRules.CoreHash(UsageBatchCore core):string`小写SHA256；Options静态`Read(IConfiguration configuration)`返回校验后的各自Options类。RetentionOptions属性`RetentionDays:int=30,BatchHashHours:int=25,IngestConcurrency:int=2,IngestTimeoutSeconds:int=10`，与Gateway配置联检补报期。

- [ ] **Step1 RED:** `Duplicate_unknown_fields_and_duplicate_item_keys_are_rejected`、`Changing_sender_preserves_hash_but_changing_core_conflicts`、`Batch_boundaries_and_future_clock_are_exact`。
  ```csharp
  Assert.Equal(hash, UsageBatchRules.CoreHash(retransmitted.Core));
  Assert.NotEqual(hash, UsageBatchRules.CoreHash(changed.Core));
  Assert.Equal("invalid_usage_batch", duplicateField.Code); Assert.Equal(422, future121Seconds.Status);
  Assert.Equal(1000, valid.Core.Items.Count); Assert.Equal(1000000, valid.Core.Items[0].Count);
  ```
  边界参数包括1001项、第三快照、256KiB+1、深度9、空ID、协议2.0、相同小时不同窗口、Count0/1000001/1.5；所有配置min/max及非法组合启动失败。
- [ ] **Step2:** `bash scripts/check-contracts.sh domain --filter FullyQualifiedName~UsageBatchRulesTests`及`bash scripts/check-contracts.sh gateway --filter FullyQualifiedName~CredentialUsageOptionsTests`，预期RED。
- [ ] **Step3:** 实现B1目录规则，解析前检查UTF8字节上限，枚举属性拒绝重复，先验证再构造record；不借宽松反序列化吞未知字段。配置来自部署而非系统设置页面。
- [ ] **Step4:** 重跑Step2，全部PASS；Hash golden vector同时验证不同字段顺序、时区等价和发送实例更换。
- [ ] **Step5:** 固定协议JSON样例及Hash向量到测试文件；不含Bearer、AK或SK，不提交。

### Task B2：五表迁移、历史来源与有界保留

**Files:**
- Create: `src/WebApi.Infrastructure/Persistence/Entities/CredentialUsageCollector.cs`、`CredentialUsageCollectorInstance.cs`、`CredentialUsageBatch.cs`、`CredentialUsageHourly.cs`、`CredentialUsageCoverage.cs`及`Configurations/`下各同名`Configuration.cs`。
- Create: `src/WebApi.Infrastructure/Persistence/Migrations/20261015020000_CredentialUsageTracking.cs`及`.Designer.cs`；Modify: 同目录`WebApiDbContextModelSnapshot.cs`。
- Create: `src/WebApi.Infrastructure/CredentialUsage/CredentialUsageCleanupService.cs`、`src/WebApi.Worker/Workers/CredentialUsageCleanupWorker.cs`；Modify: `src/WebApi.Worker/WorkerApp.cs`。
- Modify: `src/WebApi.Infrastructure/Gateway/NodeRegistry.cs`、`GatewayReadService.cs`，只增持久来源事件，不改旧报文。
- Test/Create: `tests/WebApi.Integration.Tests/CredentialUsageStorageTests.cs`。

**Interfaces:** `CleanupAsync(DateTimeOffset now,CancellationToken ct):Task<int>`返回本事务删除数≤1000；Worker每轮一次事务，下一轮再取页，停止信号可打断。五表锁定模型：

| Entity/表 | 主键 | 核心列/索引 |
|---|---|---|
| Collector / credential_usage_collectors | NodeId,CollectorId | Protocol、EnvironmentId、AcceptedSequence bigint、AcceptedThrough nullable、ClosedAt nullable、CreatedAt；节点FK及关闭状态索引 |
| CollectorInstance / credential_usage_collector_instances | NodeId,CollectorId,InstanceId | RegisteredAt、EndedAt nullable、IdentityProof(string)；collector FK |
| Batch / credential_usage_batches | NodeId,CollectorId,Sequence | CoreHash char(64)、AppliedAt、AcceptedThrough nullable；AppliedAt清理索引 |
| Hourly / credential_usage_hourly | EnvironmentId,CredentialId,HourStart | ApplicationId、Count bigint正值check、LastUsedAt；ApplicationId/HourStart/EnvironmentId查询索引 |
| Coverage / credential_usage_coverage | EnvironmentId,NodeId,HourStart | Intervals/Gaps有界jsonb、DurableThrough/AcceptedThrough、State、UpdatedAt；HourStart清理索引 |

覆盖每小时控制JSON限制1MiB；超限合并为Unknown/GapSummaryLimit，不截掉缺口。Credential/Application关联保留可信ID，不级联删掉仍可补报的历史事实；节点显式删除须先拒绝认证，才可受控清除其墓碑。节点启停/注册事件使用现有GatewayNodeEvent追加来源事实，旧事件不能证明的历史保持Unknown。

- [ ] **Step1 RED:** `Hourly_overflow_rolls_back_and_cleanup_keeps_cursor`、`Retention_rejects_invalid_combinations`、`Node_history_survives_offline_and_disable`。
  ```csharp
  Assert.Equal(long.MaxValue, unchangedHourly.Count); Assert.Equal(beforeSequence, collector.AcceptedSequence);
  Assert.InRange(deletedInTransaction, 0, 1000); Assert.NotNull(activeCollector);
  Assert.Contains(history, x=>x.EventType=="usage.node.disabled");
  Assert.Equal(beforeLastUsed, credentialAfterCleanup.LastUsedAt);
  ```
  beforeLastUsed使用固定非空时间；清理不改变实体全局最近使用。新旧库迁移验证五主键、check/FK/索引、19旧历史不变、A1迁移仍在。
- [ ] **Step2:** `bash scripts/check-contracts.sh integration --filter FullyQualifiedName~CredentialUsageStorageTests`，预期RED。
- [ ] **Step3:** 实现五表及RetentionOptions联检；清理并发用数据库锁/跳过已锁记录，接收事务与清理事务不能删除仍有效回执。Closed collector保留序列墓碑到节点显式删除。启停事件与节点状态同事务；同一环境原锁次序保持，不逆序死锁。
- [ ] **Step4:** 重跑Step2及A1迁移测试，真实PG全部PASS；清理/迟到补报并发验证没有计数重置或重复累计。
- [ ] **Step5:** 保存数据库schema摘要和迁移SQL审阅；不在4192应用，不提交。

### Task B3：节点绑定、快照验权和事务去重

**Files:**
- Create: `src/WebApi.Infrastructure/CredentialUsage/CredentialUsageIngestService.cs`、`UsageSnapshotProofReader.cs`、`UsageCollectorRegistry.cs`、`src/WebApi.ControlPlane/Gateway/CredentialUsageEndpoints.cs`。
- Modify: `src/WebApi.ControlPlane/ControlPlaneApp.cs`，只注册服务及独立端点。
- Test/Create: `tests/WebApi.Integration.Tests/CredentialUsageIngestTests.cs`。

**Interfaces:** `UsageCollectorRegistry.BindAsync(NodeIdentity node,UsageCollectorRequest request,CancellationToken ct):Task<UsageReceipt>`；`CredentialUsageIngestService.ApplyAsync(NodeIdentity node,UsageBatchRequest request,CancellationToken ct):Task<UsageReceipt>`；`UsageSnapshotProofReader.ValidateAsync(Guid environmentId,IReadOnlyList<UsageItem> items,CancellationToken ct):Task`一次批量取得≤2份快照，验出非法即throw受控ApiException。Endpoints从原Bearer认证取得NodeIdentity，不接受客户端组织/项目声明。

- [ ] **Step1 RED:** `Same_batch_replay_once_and_hash_conflict_rolls_back`、`New_sender_can_replay_proven_old_producer`、`Wrong_environment_snapshot_grant_or_unknown_instance_is_rejected`、`Old_working_revocation_does_not_discard_published_use`。
  ```csharp
  Assert.Equal("AlreadyApplied", duplicate.Result); Assert.Equal(7, hourly.Count);
  Assert.Equal(409, hashConflict.Status); Assert.Equal(410, retiredReceipt.Status);
  Assert.Equal(2, nextExpected); Assert.Equal(beforeRevision, credential.Revision);
  Assert.Equal(beforeUpdatedAt, application.UpdatedAt); Assert.Equal(beforeSnapshotBytes, afterSnapshotBytes);
  ```
  覆盖双节点/双collector序列独立、seq跳跃/<=watermark且无Hash、两个并发同批、节点禁用竞争、count溢出、跨App/Api/环境伪造、授权窗口断档、First/Last越小时或凭证窗口、旧生产者无证明。Snapshot读取SQL数≤2快照查询，无逐item读库。
- [ ] **Step2:** `bash scripts/check-contracts.sh integration --filter FullyQualifiedName~CredentialUsageIngestTests`，预期RED。
- [ ] **Step3:** 实现两个`/internal/v1/nodes/{nodeId}/credential-usage/{collectors|batches}`。当前sender必须匹配Node.Instance，producer须本collector登记或同节点registered持久证据；绑定写入来源能力事件。10秒预算/并发2，DB事务重新验节点Enabled/hash/env和短行锁，再Collector FOR UPDATE；原子receipt+增量upsert+LastUsed GREATEST+coverage+游标。溢出整事务回滚后以独立受控故障写来源gap/状态，不能留下半计数。锁顺序固定Node→Collector→有序小时键。
- [ ] **Step4:** 重跑Step2及现有节点能力测试，全部PASS；真正PostgreSQL竞争和FK校验不能由内存executor代替。
- [ ] **Step5:** 固定快照证据、SQL计数和并发结果，确认旧Node报文序列化字节未变；不提交。

### Task B4：冻结认证结果后的有界观察

**Files:**
- Create: `src/WebApi.Gateway/CredentialUsage/ICredentialUsageRecorder.cs`、`CredentialUsageAccumulator.cs`。
- Modify: `src/WebApi.Gateway/Security/ApiKeyMiddleware.cs`、`src/WebApi.Gateway/GatewayApp.cs`。
- Test/Create: `tests/WebApi.Gateway.Tests/CredentialUsageRecordingTests.cs`。

**Interfaces:** `ICredentialUsageRecorder.TryRecord(UsageObservation observation):bool`；`CredentialUsageAccumulator.Drain(DateTimeOffset through):IReadOnlyList<UsageObservation>`只由单后台写者调用。共享Observation为B1定义；在原grant匹配选择中保留实际窗口而不改变匹配规则，观察时间注入TimeProvider。

- [ ] **Step1 RED:** `Authorized_request_counts_once_despite_retries_cache_or_upstream_failure`、`Jwt_anonymous_denied_preflight_and_ip_block_do_not_count`、`Recorder_failure_never_changes_auth_result`。
  ```csharp
  Assert.Single(recorded); Assert.Equal(1, recorded[0].Item.Count);
  Assert.Equal(frozen.PayloadHash, recorded[0].Item.Source.PayloadHash);
  Assert.Empty(deniedRecords); Assert.Equal(200, responseWhenRecorderFull.StatusCode);
  ```
  参数化上游503/缓存Hit/内部3重试/之后429、跨UTC整点/重叠授权窗口；并发Drain不丢已交接增量也不重复。饱和必须产生缺口/丢失指标而非伪成功完整。
- [ ] **Step2:** `bash scripts/check-contracts.sh gateway --filter FullyQualifiedName~CredentialUsageRecordingTests`，预期RED。
- [ ] **Step3:** ApiKeyMiddleware验证credential+匹配API窗口后、await next之前TryRecord一次；B1完整键绑定该请求RuntimeGeneration，不查询管理库/最新generation。有界并发内存集合/双缓冲交接，MaxInMemoryKeys严格上限；采集异常隔离并通知gap状态，避免高基数Credential标签指标。DI仅singleton recorder。
- [ ] **Step4:** 重跑Step2及Gateway全套；PASS且JWT/匿名、Header信任边界、CORS/IP/Transform、缓存/重试既有回归保持。
- [ ] **Step5:** 保存计数语义矩阵和资源边界证据，不作性能包结论，不提交。

### Task B5：持久checkpoint、封批和缺报账目

**Files:**
- Create: `src/WebApi.Gateway/CredentialUsage/UsageJournal.cs`、`UsageCheckpoint.cs`、`UsageGapLedger.cs`、`UsageBatchSealer.cs`。
- Test/Create: `tests/WebApi.Gateway.Tests/UsageJournalTests.cs`、`UsageRecoveryTests.cs`。

**Interfaces:** `UsageCheckpoint(Guid EnvironmentId,string NodeName,Guid CollectorId,long NextSequence,DateTimeOffset DurableThrough,IReadOnlyList<UsageObservation> Active,IReadOnlyList<UsageBatchCore> Pending,IReadOnlyList<UsageGap> Gaps)`。`UsageJournal.LoadAsync(CancellationToken ct):Task<UsageCheckpoint?>`、`SaveAsync(UsageCheckpoint checkpoint,CancellationToken ct):Task`；`UsageBatchSealer.Seal(UsageCheckpoint checkpoint,Guid producerInstanceId,DateTimeOffset now):UsageCheckpoint`；`UsageGapLedger.Add(UsageGap gap):void`及`Snapshot():IReadOnlyList<UsageGap>`。

- [ ] **Step1 RED:** `Crash_between_fsync_rename_and_ack_never_double_seals_delta`、`Volatile_tail_and_corrupt_file_create_gap`、`Pending_and_gap_budgets_stay_bounded`、`Rewound_checkpoint_is_quarantined`。
  ```csharp
  Assert.Equal(originalCoreHash, restoredHash); Assert.Equal(2, restored.NextSequence);
  Assert.Contains(gaps, g=>g.Reason=="CrashTail"); Assert.False(coverageComplete);
  Assert.InRange(pendingBytes, 0, 64*1024*1024); Assert.InRange(gapControlBytes, 0, 1024*1024);
  Assert.NotEmpty(quarantinedFiles); Assert.NotEqual(oldCollectorId, replacement.CollectorId);
  ```
  实际文件系统故障点：临时写前/后、fsync后、rename后/目录fsync前、ACK后保存前；磁盘满、权限错、符号链接、错节点身份、关停取消、超24h和1000001拆项。损坏文件留私有证据不原位覆盖。
- [ ] **Step2:** `bash scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~UsageJournalTests|FullyQualifiedName~UsageRecoveryTests'`，预期RED。
- [ ] **Step3:** 原LKG owner卷固定子目录`credential-usage/`，私有目录/文件、同目录临时文件+文件fsync+rename+父目录fsync，单写者原子存活动增量/封批/collector序列/水位/gap；固定文件路径拒绝链接穿越。每1000ms checkpoint；封批≤1000项/256KiB/2快照，增量只转移一次，ACK持久后删pending。保留1MiB独立gap控制：同小时/原因合并，超额粗化为Unknown/GapSummaryLimit；仍不能持久则Unavailable。过期/满队列先记gap与有界失败摘要再释放最旧项。
- [ ] **Step4:** 重跑Step2，全部PASS；实际杀进程恢复验证留待C5，普通测试文件fsync成功不能声称突然断电验收。
- [ ] **Step5:** 保存故障点表、恢复水位和私有材料索引，不把队列明文复制到公共证据，不提交。

### Task B6：后台发送、离线兼容与恢复握手

**Files:**
- Create: `src/WebApi.Gateway/CredentialUsage/CredentialUsageReporter.cs`、`UsageNodeTransport.cs`。
- Modify: `src/WebApi.Gateway/Workers/NodeClient.cs`、`src/WebApi.Gateway/GatewayApp.cs`、`src/WebApi.Gateway/Workers/LkgRestoreWorker.cs`，仅接入注册后统计握手/关停flush。
- Test/Create: `tests/WebApi.Gateway.Tests/CredentialUsageReporterTests.cs`。

**Interfaces:** `UsageNodeTransport.BindAsync(UsageCollectorRequest request,CancellationToken ct):Task<UsageReceipt>`、`SendAsync(UsageBatchRequest request,CancellationToken ct):Task<UsageReceipt>`使用当前NodeClient注册身份；`CredentialUsageReporter`实现`BackgroundService`，循环B4 Drain→B5持久封批→B3发送。复用NodeClient已使用的GatewaySettings.InstanceId及其NodeId，不并行创建第二个节点注册者。

- [ ] **Step1 RED:** `Lost_ack_resends_identical_core_with_new_sender`、`Old_control_plane_404_keeps_bounded_queue_and_api_works`、`Empty_pulse_proves_idle_coverage`、`Server_cursor_ahead_creates_new_collector_and_gap`。
  ```csharp
  Assert.Equal(firstHash, resentHash); Assert.Equal(1, maximumConcurrentSends);
  Assert.Equal(TimeSpan.FromSeconds(3), transportTimeout); Assert.Empty(idlePulse.Core.Items);
  Assert.NotEmpty(idlePulse.Core.Intervals); Assert.Equal(200, businessResponse.StatusCode);
  Assert.Contains(gaps, g=>g.Reason=="CheckpointRewind");
  ```
  再测断网→注册→绑定→24h合法旧producer补报、未知producer保持Partial、410退役不重计/不装作Hash相同、错误凭证停止无效重试并报Unavailable、停机在有界时间内退出。
- [ ] **Step2:** `bash scripts/check-contracts.sh gateway --filter FullyQualifiedName~CredentialUsageReporterTests`，预期RED。
- [ ] **Step3:** 15秒flush/空pulse、3秒HTTP、最多1inflight、1–30秒抖动退避，取消优先；不重封pending核心，仅替换sender。握手比较本地和服务端cursor：合法待ACK重传由回执证明；回退冲突隔离+gap+新Collector，绝不提升旧本地cursor以掩盖丢失。新CP旧GW来源Partial；新GW旧CP404后台退避/容量缺口，业务继续。正常停止checkpoint+封批，有界发送不阻塞退出。
- [ ] **Step4:** 重跑Step2、B3及原节点兼容回归；PASS。新旧互通不能只用“总是200”的假transport证明，C5运行真实双版本HTTP。
- [ ] **Step5:** 记录时序、有限重试和恢复结果；不提交。

### Task B7：授权查询、历史覆盖与精确数字

**Files:**
- Create: `src/WebApi.Contracts/CredentialUsage/UsageQueryContracts.cs`、`src/WebApi.Infrastructure/CredentialUsage/CredentialUsageQuery.cs`、`UsageCoverageReader.cs`。
- Modify: `src/WebApi.ControlPlane/Applications/ApplicationEndpoints.cs`、`src/WebApi.ControlPlane/ControlPlaneApp.cs`、`src/WebApi.Infrastructure/Applications/ApplicationService.cs`（全部CredentialDto映射的LastUsed过滤）。
- Test/Create: `tests/WebApi.Integration.Tests/CredentialUsageQueryTests.cs`。

**Interfaces:** `UsageWindow(DateTimeOffset From,DateTimeOffset To)`；`UsageCoverageDto(string State,DateTimeOffset? CoverageStart,DateTimeOffset? CompleteThrough,DateTimeOffset? Freshness,IReadOnlyList<Guid> MissingNodes,IReadOnlyList<UsageGap> Gaps,string? Reason)`；`CredentialUsageRow(Guid CredentialId,string AccessKeyLast4,string? Count,DateTimeOffset? LastObservedAt,DateTimeOffset? VisibleLastUsedAt,UsageCoverageDto Coverage)`；`CredentialUsagePage(UsageWindow Window,IReadOnlyList<Guid> VisibleEnvironmentIds,PageResult<CredentialUsageRow> Page,UsageCoverageDto Coverage)`。
`CredentialUsageQuery.ReadAsync(Guid applicationId,Guid? environmentId,UsageWindow window,int page,int pageSize,ActorContext actor,CancellationToken ct):Task<CredentialUsagePage>`；`UsageCoverageReader.ReadAsync(IReadOnlyList<Guid> environmentIds,UsageWindow window,CancellationToken ct):Task<UsageCoverageDto>`。Status只Available/NoData/Partial/Unknown/Unavailable，Count为规范非负decimal string或null。
另产生`CredentialUsageQuery.VisibleLastUsedAsync(Guid applicationId,IReadOnlyList<Guid> credentialIds,ActorContext actor,CancellationToken ct):Task<IReadOnlyDictionary<Guid,DateTimeOffset?>>`，批量返回当前可见来源中可证明的最近已知时间；历史超出保留期或缺来源不能证明时返回null并在UsageCoverage说明原因，不用实体全局时间回填。

- [ ] **Step1 RED:** `Unseen_environment_last_used_is_never_leaked`、`Idle_complete_is_zero_unknown_is_null_partial_is_lower_bound`、`Historical_cohort_and_retention_boundaries_are_honest`、`Count_above_js_safe_integer_roundtrips`。
  ```csharp
  Assert.Equal("9007199254740993", row.Count); Assert.Null(unknown.Count);
  Assert.Equal("0", idle.Count); Assert.Equal("NoData", idle.Coverage.State);
  Assert.Equal("Partial", missingOldNode.State); Assert.Equal(visibleOnlyLastUsed, row.VisibleLastUsedAt);
  Assert.Equal(422, nonHourly.Status); Assert.Equal(403, foreignEnvironment.Status);
  Assert.Equal("18446744073709551614", sumOfTwoMaxInt64Buckets.Count);
  ```
  测UTC边界/时区偏移等价、30天+1h/实际保留更短、当前小时不Complete、退役/掉线/旧协议节点、无历史cohort、API凭证撤销后的合法补报、窗口外可见LastUsed。对原应用详情/凭证列表/单凭证详情及更新响应分别断言不泄漏全局LastUsed；历史超保留/旧来源不可证明显示null而非猜测。SQL日志验证page=50/100读取有界，无每Credential独立查询；explicit foreign采用统一403避免存在性差异。
- [ ] **Step2:** `bash scripts/check-contracts.sh integration --filter FullyQualifiedName~CredentialUsageQueryTests`，预期RED。
- [ ] **Step3:** 接入`GET /api/v1/applications/{id}/credential-usage`，先app.read应用范围再逐环境批量授权，未指定环境仅合计可见环境；默认50/上限100，非法page返回422。通过VisibleLastUsedAsync统一改造原CredentialDto读/写响应映射，只按可见环境派生LastUsed，不直接返回全局entity.LastUsedAt。小时/保留窗口严格校验；历史Node事件+collector实例+interval/gap拼覆盖，无法证明为Unknown/Partial；缺失优先于连续覆盖。SQL分组分页；桶内Count仍bigint受检，窗口SUM用PostgreSQL numeric并以text取回，通过BCL BigInteger校验/格式化为规范十进制字符串，不降为long/浮点，前端不得Number转换。
- [ ] **Step4:** 重跑Step2与既有ScopedListQueryTests，PASS；统计查询不能凭app.read获取metric/audit/log/trace权限。
- [ ] **Step5:** 保存权限主体、SQL读取上界及完整性真值表，交付C2消费接口；不提交。
