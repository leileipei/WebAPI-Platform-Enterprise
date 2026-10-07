# SMTP/Webhook 外部通知 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成可重启恢复、可查询回执的 SMTP/Webhook 告警通知，在本机隔离实例验证真实协议及权限。

**Architecture:** 在既有告警事务内规划独立 PostgreSQL 任务，现有 Worker 在事务外进行单次传输；冻结规则及连接档案，控制面管理部署秘密引用与渠道状态。现有部署 Outbox 不接收通知消息。

**Tech Stack:** .NET10、EF/Npgsql10、PostgreSQL、MailKit4.17.0、现有 DataProtection、React19.2/Vite6.4、xUnit、Node 原生测试；不新增前端依赖。

**Spec:** [已确认正式规格](../specs/2026-10-08-notification-cross-environment-approvals-design.md)，提交 `adc7dfd5476ae8ae21aa405efeb9248df3085937`；用户随后回复“确认”。本计划负责规格§3–7、§9通知部分、§10通知验收。审批另见同日跨环境审批计划，最终部署另见同日交付计划。

状态：用户已确认实施计划；正在执行。沿用此前 Native：本会话逐任务实施，交付前一次独立整分支审查。

## Global Constraints

- 原六字段和 Keep/Replace/Clear 保留；新增 smtpEnabled=false、webhookEnabled=false、smtpSecurity=StartTlsRequired；旧规则 externalEnabled=false，不追溯发送已有 occurrence。
- Email 最多20地址、每项≤254字符；完整邮箱/域名部署名单必需；每个投递一个收件人，无 Cc/Bcc。
- maxAttempts 1–5（默认5）；baseDelaySeconds 1–300（默认30）；maxDelaySeconds base–3600（默认900）；expiresAfterMinutes 5–1440（默认1440）。
- 秘密文件1–16KiB、受控映射/禁止symlink/私有权限；SMTP username1–320/password1–4096，Webhook解码钥匙32–64字节；明文不持久化进表或证据。
- Worker并发1–8（默认4）、每轮领取≤50、轮询1秒、租约60秒、单次总预算10秒；消息≤16KiB、HTTP响应读取≤4KiB、Retry-After延迟上限3600秒。
- 保存和 RuleTest 不产生发送；Test独立202任务、单次/5分钟，每用户渠道每分钟一次，平台未终止Test≤10；不绕过地址/TLS/秘密策略。
- 静默只阻止未开始发送及后续尝试；OutcomeUnknown承认潜在重复；SMTP/HTTP接受不等于业务送达或阅读。
- 精确暂存任务文件，不push、不覆盖主目录七项修改/历史未跟踪资料；原4192不写QA数据、不自动重发布业务4/4。

## Review Focus

1. 渠道保持同一文字引用但文件内容轮换：旧任务拒绝新秘密，显式保存才固定新版本；N2测试。
2. SMTP接收DATA后断连、进程写回前退出：保留OutcomeUnknown，恢复消息不能先于触发结果协调；N3/N4/N5测试。
3. 静默发生在领取后、联网前或联网后：采用明确定义的发送开始点，不承诺撤回已发送消息；N5测试。
4. Test关闭自动开关、保存编辑另一渠道、旧occurrence仅改通知字段：测试和历史目标保持正确，另一渠道不被误取消；N2/N4/N6测试。
5. 第20个邮箱、重复/大小写/IDN及恶意行分隔：名单精确生效，不群发或改变本地部分；N1/N3测试。

---

## 文件职责与验证入口

执行时使用 using-git-worktrees 核对并优先复用 `enterprise/.worktrees/gateway-restart-readiness`，tracked clean 才快进同步本批文档；保留历史 untracked/symlink。所有下列路径以执行工作区为根。

| 文件组 | 职责 |
| --- | --- |
| Contracts/Notifications、Domain/Notifications | DTO、严格规则策略、纯状态/退避/签名与消息形状 |
| Infrastructure/Notifications | 档案/秘密/地址、事务规划、租约存储、传输、回执查询 |
| Infrastructure/Persistence | 四表、冻结列、索引、默认关闭及兼容读取迁移 |
| ControlPlane/Notifications、Worker/Workers | API鉴权/治理、独立后台调度与持久密钥DI |
| console/src/notifications、settings、observability | 设置、测试、规则、回执、未保存/412/撤权状态 |
| tests/WebApi.NotificationFixtureHost、scripts/notifications | 自有SMTP/TLS/Webhook fixture、固定提交实际验收 |

`N=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node`；运行 `./scripts/check-contracts.sh domain|integration --filter <ClassName>`、`N --test console/tests/<name>.test.mjs`；全量 `./scripts/check-console.sh` 和交付计划规定五套完整回归。新入口先补最小可调用签名；编译失败、fixture失败、超时或skip不算行为RED。

依赖只在获准执行后新增：`Directory.Packages.props`固定MailKit4.17.0，Infrastructure显式引用；更新受影响项目锁文件闭包，并执行固定SDK容器 `dotnet restore WebApi.Enterprise.sln --force-evaluate`，再 `--locked-mode`。记录包摘要和全部传递许可证，不升级其他已有直接依赖。官方依据：[NuGet版本](https://www.nuget.org/packages/MailKit)、[4.17.0 MIT许可证](https://github.com/jstedfast/MailKit/blob/4.17.0/LICENSE)、[Socket连接接口](https://mimekit.net/docs/html/M_MailKit_Net_Smtp_SmtpClient_ConnectAsync_1.htm)，2026-10-08查阅；实际.NET10/TLS验证由N3完成，不能以文档代替实测。

## Task 1 (N1)：通知契约、配置预算与纯协议

**Files:** Create `src/WebApi.Contracts/Notifications/NotificationContracts.cs`、`src/WebApi.Domain/Notifications/{NotificationPolicyValidator,NotificationDecisionMachine,WebhookSignature,NotificationPayload}.cs`、`tests/WebApi.Domain.Tests/NotificationPolicyTests.cs`；Modify `src/WebApi.Contracts/Alerts/AlertContracts.cs`。

**Interfaces:** `NotificationRetryPolicy(int MaxAttempts,int BaseDelaySeconds,int MaxDelaySeconds,int ExpiresAfterMinutes)`；NotificationIntent追加可选 ExternalEnabled=false、EmailRecipients、NotifyRecovery=true、RetryPolicy（缺省采用规格值）；`NotificationPolicyValidator.Normalize(NotificationIntent) -> NotificationIntent`。Contracts定义 `NotificationChannel {Email,Webhook}`、`DeliveryStatus`及规格状态、`DeliveryOutcome {Accepted,TransientFailure,PermanentFailure,OutcomeUnknown}`、`TransportResult(DeliveryOutcome Outcome,string Code,int? ProtocolStatus,TimeSpan? RetryAfter)`。`NotificationDeliveryDto(Guid Id,string Kind,Guid? EventId,NotificationChannel Channel,string MaskedTarget,DeliveryStatus Status,string? Reason,int AttemptCount,int MaxAttempts,DateTimeOffset CreatedAt,DateTimeOffset ExpiresAt,DateTimeOffset? NextAttemptAt,bool CanRetry,long Revision)`；`NotificationAttemptDto(int AttemptNo,DateTimeOffset StartedAt,DateTimeOffset? CompletedAt,DeliveryOutcome? Outcome,string? Code,int? ProtocolStatus)`，均不含秘密或原地址。`NotificationDecisionMachine.NextAttemptAt(NotificationRetryPolicy,int attemptNo,DateTimeOffset now,DateTimeOffset expires,TimeSpan? retryAfter,double jitter) -> RetryDecision(DateTimeOffset? NextAt,DeliveryStatus Status,string? Reason)`；`WebhookSignature.Sign(ReadOnlySpan<byte> key,string timestamp,Guid deliveryId,ReadOnlySpan<byte> body) -> string`。

- [x] **Step 1：写行为用例。** `LegacyIntentDefaultsToExternalOff`：`Assert.False(policy.ExternalEnabled)`；`BudgetsRejectEdgesDuplicatesAndCrLf`覆盖各极值±1、重复字段/渠道/邮箱、20/21地址与CR/LF；`RetryAfterNeverShortened`断言7200秒结果`Failed/RetryAfterExceedsBudget`；`SignatureUsesExactFrozenBytes`用独立HMAC计算预期相等，改变一个字节不等。域名IDN规范化后比较名单，邮箱本地部分不改大小写。
- [x] **Step 2：RED。** Run `./scripts/check-contracts.sh domain --filter NotificationPolicyTests`；确认测试运行并发生上述行为断言失败。
- [x] **Step 3：实现。** 规范枚举、参数及旧默认；新增v1消息record只含规格允许字段，16KiB检查在序列化后执行；有效Retry-After解析由传输提供，退避不越界；选择性纯决策不访问网络/数据库。
- [x] **Step 4：GREEN。** 同上；另跑`--filter AlertEvaluationMachineTests`，全部通过/零skip。
- [x] **Step 5：提交。** 精确暂存本任务文件，`feat(notifications): define bounded notification policies and protocol`。

## Task 2 (N2)：四表、默认关闭、渠道档案与持久秘密版本

**Files:** Create `src/WebApi.Infrastructure/Notifications/{NotificationConfigurationService,NotificationSecretResolver,NotificationSecretVersion,NotificationDeploymentSettings,NotificationMigrationPreflight}.cs`；Create `src/WebApi.Infrastructure/Persistence/Entities/{NotificationChannelProfile,NotificationChannelState,NotificationDelivery,NotificationDeliveryAttempt}.cs`及各同名Configuration；Create `src/WebApi.Infrastructure/Security/PersistentProtectionConfiguration.cs`、`tests/WebApi.Integration.Tests/{NotificationConfigurationTests,NotificationMigrationTests}.cs`。Modify `Settings/{SettingsValues,SystemSettingsValidator,SystemSettingsService}.cs`、`Persistence/{WebApiDbContext,AuditedCommandExecutor}.cs`、`Persistence/Entities/AlertEvent.cs`及其Configuration、ControlPlane/Worker DI、Migrator/Program.cs和原 `ControlPlane/Security/PersistentDataProtection.cs`；生成 `20261008030000_ExternalNotifications.cs/.Designer.cs`和ModelSnapshot。

**Interfaces:** `NotificationDeploymentSettings.Read(IConfiguration,IHostEnvironment) -> NotificationDeploymentSettings`包含地址/收件名单、秘密映射、ConsoleBaseUrl及规格部署预算；`RequireAllowedRecipient(string email) -> void`按规范化域名和未改写的本地部分校验部署名单。`INotificationSecretResolver.ResolveAsync(string reference,NotificationChannel,CancellationToken) -> Task<NotificationSecret>`，NotificationSecret为私有内部类型，不入DTO；`NotificationSecretVersion.Pin(Guid profileId,NotificationSecret) -> string`、`Matches(Guid,string,NotificationSecret) -> bool`。`NotificationConfigurationService.ApplyAsync(NotificationSettings before,NotificationSettings after,long settingsRevision,ActorContext,CancellationToken) -> Task`在调用者治理事务中更新当前档案指针；同值保存支持显式重新固定已替换文件，另一渠道无变化则复用原档案。`PersistentProtectionConfiguration.Configure(IServiceCollection,IConfiguration,bool readOnly=false) -> void`保持原密钥路径和应用名；Worker使用readOnly=true并禁用自动生钥，控制面保留原生成权限。

- [x] **Step 1：写行为用例。** `OldSixFieldsAndIntentSurviveMigration`比较原六值/全部旧业务行，`Assert.False(newSettings.SmtpEnabled)`，旧事件冻结关闭；`PinSurvivesWorkerRestartAndSsoKeysUnchanged`复用原持久目录跨两个服务实例验证；`SameReferenceChangedFileRequiresExplicitActivation`先旧任务匹配false再显式保存新profile，旧ID不变；`OtherChannelEditDoesNotChangeProfile`断言Webhook编辑不改变SMTP profile；恶意文件/权限/symlink/额外JSON/无映射均拒绝。预览和审计不得含秘密、引用全文或邮箱原文。
- [x] **Step 2：RED。** Run `./scripts/check-contracts.sh integration --filter 'NotificationConfigurationTests|NotificationMigrationTests'`；保存失败与迁移失败不能由缺失fixture解释。
- [x] **Step 3：实现。** 四表和索引/约束按规格§6；默认及旧合法解码明确，非法旧JSON报错。ApplicationName与原SSO用途不变，Worker和控制面统一持久配置。启用时只解析秘密和验证地址/配置，不发SMTP/HTTP；新规则使用N1规范化；审计捕获通知摘要和收件数，关闭旧Notification全文捕获。
- [x] **Step 4：GREEN及消费者回归。** 同上及`--filter 'SystemSettings|SsoSecret|AlertPersistence'`；规则禁用默认、原设置五组及SSO消费者仍通过。
- [x] **Step 5：提交。** 精确提交上述源码/迁移，`feat(notifications): persist immutable profiles and safe migration defaults`。

## Task 3 (N3)：有界真实 SMTP/Webhook 单次传输

**Files:** Create `src/WebApi.Infrastructure/Notifications/{NotificationAddressPolicy,SmtpNotificationTransport,WebhookNotificationTransport,NotificationTransport}.cs`、`tests/WebApi.NotificationFixtureHost/WebApi.NotificationFixtureHost.csproj`、`tests/WebApi.NotificationFixtureHost/{Program,NotificationFixtureApp,SmtpFixture,WebhookFixture}.cs`、`tests/WebApi.Integration.Tests/NotificationTransportTests.cs`及`tests/WebApi.TestSupport/Support/NotificationTransportFixture.cs`；Modify `Directory.Packages.props`、`src/WebApi.Infrastructure/WebApi.Infrastructure.csproj`、`WebApi.Enterprise.sln`、TestSupport项目引用及上述依赖变动影响的各 `packages.lock.json`，新增fixture自己的锁文件。

**Interfaces:** `INotificationTransport.SendAsync(NotificationSendEnvelope,CancellationToken) -> Task<TransportResult>`；私有Envelope含冻结body、deliveryId、地址/TLS/发件人及单个接收目标与已解析秘密；`INotificationAddressPolicy.ResolveAsync(NotificationChannel,string host,int port,Uri? url,CancellationToken) -> Task<IReadOnlyList<IPAddress>>`。`NotificationFixtureApp.Build(string[],Action<WebApplicationBuilder>? configure=null) -> WebApplication`承载WebHook控制/查询；SMTP TCP listener附着同host生命周期，支持强制STARTTLS/TLS-on-connect、AUTH、单目标DATA、暂时/永久错误、延迟、接受后断连。fixture记录协议码/次数/ID，不保存认证原文。

- [ ] **Step 1：写真实协议用例。** `SmtpUsesValidatedSocketRequiredTlsAndOneRecipient`断言RCPT数量1、未知TLS模式拒绝、无STARTTLS不回退；`SmtpDataAcceptedThenDisconnectedIsUnknown`断言`OutcomeUnknown`；`WebhookSignatureReplayAndExactBody`独立校验签名及稳定deliveryId；`DnsRebindingMixedAnswersAndRedirectRejected`禁止第二次DNS或跳转；`TimeoutAndBodyReadAreBounded`超过10秒取消、响应读取≤4096；`HostnameMismatchNotAllowedByFixtureCa`必须失败。20目标由20独立发送，不合并信封。
- [ ] **Step 2：RED。** Run `./scripts/check-contracts.sh integration --filter NotificationTransportTests`；只有实际服务启动、认证/TLS协商已发生且行为不符才算RED。
- [ ] **Step 3：实现及锁定依赖。** MailKit连接先自行将socket连到允许IP，再传原hostname和显式StartTls/SslOnConnect；禁Auto、协议日志及系统代理。Webhook SocketsHttpHandler.ConnectCallback绑定已验证地址，关重定向/Cookie/代理；总体CancellationToken覆盖全阶段。fixture CA仅匹配显式配置实例，保留hostname/有效期校验；正式路径保持正常证书验证。SMTP接受后QUIT错误不抹掉已取得的DATA接受结果。
- [ ] **Step 4：GREEN与库验证。** 同上；固定SDK容器.NET10编译、`dotnet restore WebApi.Enterprise.sln --locked-mode`成功，归档4.17.0及传递依赖摘要/许可证，不跳过TLS或Socket实际用例。
- [ ] **Step 5：提交。** 精确提交源码/fixture/锁文件，`feat(notifications): send bounded SMTP and signed webhooks`。

## Task 4 (N4)：告警原子规划、静默及可重启恢复配对

**Files:** Create `src/WebApi.Infrastructure/Notifications/NotificationPlanner.cs`、`tests/WebApi.Integration.Tests/NotificationPlanningTests.cs`；Modify `Alerts/{AlertEvaluationService,AlertEventService,AlertSilenceExpiryService,AlertRuleService}.cs`。

**Interfaces:** `NotificationPlanner.OnTransitionAsync(AlertEvent,AlertEventTransition,CancellationToken) -> Task`须在持有既有锁的事务调用；`CoordinateResolvedAsync(Guid eventId,CancellationToken) -> Task`按持久转换和父任务协调；`CoordinateNextResolvedAsync(CancellationToken) -> Task<bool>`每轮有界扫描≤50事件。生产路径不借用规则创建者权限，采用冻结真实Scope及当前有效范围。

- [ ] **Step 1：写行为用例。** `EventAndTasksCommitOrRollbackTogether`事务失败时`Assert.Empty(deliveries)`；`RepeatedTransitionCreatesOneTaskPerTarget`每收件人/转换只一条；`NotificationEditDoesNotResetLogicOrRetargetOldOccurrence`比较logic_revision及冻结目标；`SilencedResolvedAndManualClosureAreDifferent`FromStatus=Silenced生成抑制、不宣称恢复；`ResolveDuringSendingSurvivesRestart`发送中Resolve后重建服务、结束父尝试再协调，断言Resolved只有一个且创建晚于父结果；规则关闭/范围变更取消，升级旧事件不补发。
- [ ] **Step 2：RED。** Run `./scripts/check-contracts.sh integration --filter NotificationPlanningTests`。
- [ ] **Step 3：实现。** 规则保存接N1.Normalize，在ExternalEnabled且选择Email时逐个调用N2.RequireAllowedRecipient；保存未启用意向不自动激活。接入全部四类转换入口，治理→规则→状态/事件→任务统一锁顺序；评估落库阶段也先取得治理锁，外部观测I/O仍在锁外。已持有规则锁的Planner不反向获取上游锁。Triggered冻结策略/档案，Resolved仅配对Accepted/OutcomeUnknown父任务，终止未开始Triggered；不把恢复待协调状态放到仅内存回调。旧关闭理由治理分支仅生成取消/抑制。
- [ ] **Step 4：GREEN及生命周期回归。** 同上及`--filter 'AlertActionTests|AlertEvaluationTests|AlertRuleTests'`，状态机和原RuleTest仍正确。
- [ ] **Step 5：提交。** `feat(alerts): plan durable notifications on lifecycle transitions`。

## Task 5 (N5)：租约 Worker、测试/回执/人工重试 API

**Files:** Create `src/WebApi.Infrastructure/Notifications/{NotificationDeliveryStore,NotificationDispatcher,NotificationQueryService,NotificationTestService}.cs`、`src/WebApi.Worker/Workers/NotificationDeliveryWorker.cs`、`src/WebApi.ControlPlane/Notifications/NotificationEndpoints.cs`、`tests/WebApi.Integration.Tests/{NotificationDispatchTests,NotificationApiTests}.cs`；ModifyControlPlane/Worker注册及Contracts DTO。

**Interfaces:** `NotificationDeliveryStore.TryBeginAsync(string owner,CancellationToken) -> Task<DeliveryLease?>`（DeliveryLease含Id/Token/AttemptNo/Expires）；`CompleteAsync(DeliveryLease,TransportResult,CancellationToken) -> Task<bool>`fencing；`NotificationDispatcher.DispatchNextAsync(string owner,CancellationToken) -> Task<bool>`仅一次传输。`NotificationQueryService.ListForEventAsync(Guid,ActorContext,int page,int size,CancellationToken) -> Task<PageResult<NotificationDeliveryDto>>`、`AttemptsAsync(Guid,ActorContext,int,int,CancellationToken) -> Task<PageResult<NotificationAttemptDto>>`、`RetryAsync(Guid,string etag,ActorContext,CancellationToken) -> Task<NotificationDeliveryDto>`。`NotificationTestService.CreateAsync(NotificationChannel,string? email,string tag,ActorContext,CancellationToken) -> Task<NotificationDeliveryDto>`及`GetAsync(Guid,ActorContext,CancellationToken)`；使用既有CommandRequestContext幂等键。

规则编辑需要的 `NotificationLimitsDto(int MaxRecipients,bool EmailConfigured,bool EmailEnabled,bool WebhookConfigured,bool WebhookEnabled)` 经 `GetLimitsAsync(ScopeRef,ActorContext,CancellationToken) -> Task<NotificationLimitsDto>` 和 `GET /api/v1/notification-limits?organizationId=...&projectId=...&environmentId=...` 输出：核对真实层级，要求该范围 alert.rule.manage，no-store，不输出端点、秘密、固定邮箱名单或跨范围数据。名单仍由保存时服务端校验；system.manage平台设置可显示原部署名单及来源。

- [ ] **Step 1：写行为用例。** `TwoWorkersCannotOwnSameLiveAttempt`、`ExpiredLeaseCannotOverwriteNewReceipt`比较Token与attempt唯一键；`CrashAfterRemoteAcceptRecordsUnknownAndRecovers`重启后有且仅有恢复协调结果；`SilenceBeforeDispatchBlocksAndAfterDispatchStopsOnlyFutureAttempts`围绕begin点控制barrier；`RetryCannotResetBudgetOrShortenRetryAfter`次数/期限/下界不变；`DisabledChannelCanBeTestedWithoutActivation`202→实际回执，保存值与profile指针不变；`TestLimitsAndIdempotencyAreAtomic`第11并发Test/同分钟第二条拒绝；越Scope404、无写403、412输入可重试不被缓存绕过。
- [ ] **Step 2：RED。** Run `./scripts/check-contracts.sh integration --filter 'NotificationDispatchTests|NotificationApiTests'`。
- [ ] **Step 3：实现。** SKIP LOCKED按数据库时间领取即将执行任务，持久attempt再网络发送；超时fencing/取消/Unknown归类与N1退避；Complete先结束其任务写回事务，再按统一锁顺序调用恢复协调，不在任务锁内反向取得治理/事件锁。每轮协调恢复并处理暂停到期。所有API按规格§7，Test不用自动enabled门禁但重新校验已保存配置/秘密版本，Receipt一律no-store且掩码；人工retry不重置预算。
- [ ] **Step 4：GREEN。** 同上及`--filter 'IdempotencyTests|AlertActionTests|SystemSettingsCommandTests'`；实际4并发/60秒租约/5最大次数按注入时钟验证，不靠长sleep。
- [ ] **Step 5：提交。** `feat(notifications): dispatch durable jobs and expose governed receipts`。

## Task 6 (N6)：系统通知设置与真实测试 UI

**Files:** Create `console/src/notifications/{api.ts,settings-state.mjs,settings-state.d.mts,NotificationSettingsPanel.tsx,NotificationTestDialog.tsx}`、`console/tests/notification-settings.test.mjs`；Modify `pages/SystemSettings.tsx`、`settings/state.mjs`、`styles/tokens.css`。

**Interfaces:** `notificationSettingsInput(dto) -> values`保留Keep引用操作；`notificationTestState(state,event) -> state`按请求epoch/revision区分Queued/Accepted/Failed等；`NotificationSettingsPanel({state,onChange,onTest})`复用原设置保存/预览，`NotificationTestDialog({channel,revision,onClose})`只选择Email接收人或已保存WebHook目标。

- [ ] **Step 1：写状态用例。** `queuedTestIsNotSuccess`：`assert.equal(label({status:'Queued'}),'已排队，等待实际回执')`；`unsavedOrStaleRevisionCannotBeTested`、`secretKeepNeverBecomesPlaceholderReplace`、`lateReceiptCannotOverwriteNewRevision`、`deniedClearsTargetAndSecretDraft`；测试错误不覆盖dirty/412输入，渠道关闭仍能明确发测试。
- [ ] **Step 2：RED。** Run `N --test console/tests/notification-settings.test.mjs`，模块可加载后断言失败。
- [ ] **Step 3：实现。** 展示3新增字段、收件/地址名单来源、实际启用效果及轮换影响；“校验配置”和“发送测试”分按钮；Test受saved/etag条件，202只显示排队并轮询自身回执。未保存导航/焦点/Escape沿用现有状态保护，秘密不回显。
- [ ] **Step 4：GREEN。** 同上及 `./scripts/check-console.sh`，Node全测试和TypeScript/Vite构建通过。
- [ ] **Step 5：提交。** `feat(console): manage notification activation and actual test receipts`。

## Task 7 (N7)：规则通知策略与告警投递记录 UI

**Files:** Create `console/src/notifications/{RuleNotificationEditor.tsx,NotificationDeliveryList.tsx,delivery-state.mjs,delivery-state.d.mts}`、`console/tests/notification-delivery.test.mjs`；Modify `observability/{RuleEditor.tsx,alert-state.mjs,alert-state.d.mts}`、`pages/{AlertRules,AlertCenter}.tsx`及现有告警API。

**Interfaces:** `RuleNotificationEditor({value,onChange,readOnly,limits})`使用N1完整策略；`NotificationDeliveryList({eventId,scope})`调用N5接口；`deliveryActionState(state,event) -> state`按真实预算/永久错误/读取权限控制重试和清空。

- [ ] **Step 1：写状态用例。** `legacyChannelsRemainIntentUntilExplicitEnable`、`twentyRecipientsAreSeparateTargets`、`attemptsExhaustedAndRetryAfterCannotBeBypassed`、`silenceShowsInFlightBoundary`、`maskedTargetsAndUnknownOutcomeAreDistinct`；更换Scope/401/403丢弃迟到回执，普通412保留规则及接收人草稿。
- [ ] **Step 2：RED。** Run `N --test console/tests/notification-delivery.test.mjs`。
- [ ] **Step 3：实现。** 新增显式外部开关、收件人、恢复通知和4预算字段，企业IM保留待接入；详情显示分页状态/尝试/可能重复及掩码目标。重试按钮以服务器可操作信息及原权限为准，不以前端修改次数启用。原RuleTest继续只测试阈值，不发送。
- [ ] **Step 4：GREEN。** 同上及`./scripts/check-console.sh`；对应API重新鉴权，不因只读渲染隐藏服务端错误。
- [ ] **Step 5：提交。** `feat(console): configure rule notifications and inspect delivery attempts`。

## Task 8 (N8)：可维护本机通知服务与独立实际验收

**Files:** Create `scripts/notifications/{fixture,scenario,runtime-secrets,evidence}.mjs`、`tests/notifications/{runtime,evidence}.test.mjs`、`deploy/compose.notifications-demo.yml`；Modify `deploy/compose.runtime.yml`、`scripts/runtime/{state,context,lifecycle,status,acceptance-backup}.mjs`和固定构建归档清单。公共证据至 `docs/evidence/notifications/`，私有材料只进ignored `.runtime/`。

**Interfaces:** `prepareNotificationSecrets({directory,owner,projectName}) -> Promise<SecretReceipt>`，通知卷独立且ControlPlane/Worker只读，Worker挂载原 `dp-keys:/keys:ro` 并采用N2只读保护配置，控制面原dp-keys写权限保持；`runNotificationScenario({revision,imageId,cloneDirectory,fixtureDirectory}) -> Promise<NotificationScenarioResult>`；`validateNotificationEvidence(proof,files,{revision,imageId,privateValues}) -> AcceptanceResult`读取内部身份/回执/协议观察而非信wrapper标签。JS AcceptanceResult固定 `{passed:boolean,errors:string[]}`；ScenarioResult必须含内部revision/imageId、每项checks、协议观察、截图/动作manifest及逐文件摘要。

新增私有 `notification-runtime.json` sidecar保存通知fixture开关、独立端口、秘密receipt引用和原owner/project identity；`loadNotificationRuntime(directory,coreState) -> Promise<NotificationRuntimeState>`必须核对原归属及私有文件权限。原 `runtime.json` 及 ports 对象的严格schema不添加字段，旧固定工具仍能解析原核心状态；完整新增服务维护使用本批入口。新端口冲突独立验证，不能借原三端口校验跳过。新冷备将sidecar与实际新增卷manifest一起保留。

- [ ] **Step 1：写门禁用例。** 空SMTP回执/伪签名/错revision、旧截图改标、缺TLS/静默竞态、目录指向4192、目标非fixture、秘密出现在日志、卷遗漏冷备、旧工具被改、sidecar归属不符/新端口冲突均`assert.throws`；`LegacyCoreStateSchemaIsUnchanged`用原严格validator回读核心状态成功；Run `N --test tests/notifications/*.test.mjs`确认行为RED。
- [ ] **Step 2：实现及GREEN。** 运行工具固定档案和归档源码；自动channel默认关闭，所有TCP/管理端口仅本机暴露。冷备/restore包含通知秘密和保留原dp-keys；状态报告区别未配置/禁用/可投递/fixture可用，不能用Health=200声称投递成功。
- [ ] **Step 3：真实克隆验收。** 自有UUID实例中新建独立fixture账号/Scope，不改已有只读账号；保护原4192只做已授权冷备。运行实际Email单目标、TLS正负、Webhook签名/2xx/429/5xx/断连、跨Worker/restart、静默/恢复、引用轮换、平台配置测试，并从fixture观察次数和ID。完整实际1440/1280交互与截图绑定镜像和源码，保存全部失败尝试和最终结果。
- [ ] **Step 4：验收门禁。** `N --test tests/notifications/*.test.mjs`及真实scenario验证结果必须全部成功/零缺项；本模块可在不实施审批模块的情况下工作和独立验收。全量/整分支审查/原4192升级由交付计划统一执行，不重复整分支审查。
- [ ] **Step 5：封存提交。** `test(notifications): verify local SMTP webhook and recovery evidence`；只提交脱敏材料与驱动，记录实际计数，不沿用历史1629数字宣称本批通过。

## 覆盖与自查

规格§4→N1/N2/N3/N6；§5→N1/N4/N7；§6→N2/N3/N4/N5；§7→N5/N6/N7；§9通知部署→N8及交付D1/D2；§10→各任务及交付D2/D3。五项Review Focus均指定实际测试。跨任务使用同一Contracts状态/结果类型，传输不再自行重试，Planner不再另存隐藏网络凭证。未列出的实现细节由执行者按已确认规格处理，不能改变预算、权限或默认发送状态。
