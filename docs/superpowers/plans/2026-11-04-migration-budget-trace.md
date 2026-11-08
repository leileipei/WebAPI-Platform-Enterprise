# 迁移预算与校验追溯实施计划

> **For agentic workers:** 使用 `superpowers:executing-plans` 在当前会话逐项实施；用复选框记录进度。本计划不要求也不允许在验证通过前提交。

**目标：** 对受控迁移实施可验证的资源预算、可对账的成功／失败追溯，并在隔离环境检验旧写入者停写措施。

**架构：** 单次尝试在独立持久目录创建不可覆盖的开始证据；受控入口在同一数据库事务内验证、迁移并插入成功回执，事务外写入有限、脱敏的结果证据。预算从事务开始覆盖至提交确认；独立 SQL 保留现有允许场景，并把无外部采集的失败标为追溯未知。

**技术：** .NET 10、EF Core／Npgsql、PostgreSQL 17、现有 xUnit 测试及 Linux 容器；不增加网络依赖。

**规格：** `docs/superpowers/specs/2026-11-04-migration-budget-trace-design.md`。用户已确认 A；数值仅为隔离验证初值。

## 全局约束

- 现有候选位于 `enterprise/.worktrees/merge-governance-24-20261104`，包含大量既有未提交内容；不得重置、清理、覆盖历史失败、保留库、G14 状态或 4192。
- 验证通过前不提交、不合并、不推送、不部署；只操作指定隔离 PostgreSQL owner 和本计划独有的一次性数据库、角色、进程与证据目录。
- 旧的 35 个迁移及其结构指纹不改；新结构采用第 36 个前向迁移。已有历史 SQL 操作的源文件和校验哈希保持，EF／SQL 的新结构独立核对。
- 15 分钟、设置 64 KiB、规则 16 KiB、10 万条／64 MiB 为隔离测试初值，不凭本计划确认为生产默认或容量；超时后仍须独立核实回滚和锁释放。
- 无外部可信时间或历史见证时不提升治理签发状态；普通持久目录、数据库回执和 SHA 不能声称防篡改、防回滚或生产验收。
- 使用真实的旧通知校验器；不输出通知正文、URL、凭据、密码、连接串和异常原文。新的失败、构建失败、原始日志及人工判定不可覆盖或删除。

## 审查重点

1. 数据库 `COMMIT` 结果未知、失联或备份回退：对账保持 `Unknown`，不能由缺少回执推断失败（任务 4、5、7）。
2. 目录满、链接替换、并发配额、崩溃在文件 `fsync` 前后：迁移前阻断或留下未知，不能覆盖已有证据（任务 4、7）。
3. 大型压缩 JSONB、Unicode、空值、数量／总量边界：先判断受限字节数再向 .NET 传值，不弱化旧校验（任务 2、7）。
4. EF 和纯 SQL 的空库、历史库、无待迁移及重复调用：旧 35 个前缀、数据和指纹不变；状态不能把 `NotApplicable` 写成 `Passed`（任务 3、6、7）。
5. 命令在迁移提交后才 bootstrap／seed：后续失败不能标成整个事务回滚；旧账号停用不自动终止旧连接（任务 5、7）。

## Task 1：有界预算与事务时限

**文件：** 新增 `src/WebApi.Infrastructure/Persistence/MigrationBudget.cs`、`MigrationBudgetScope.cs` 和 `tests/WebApi.Integration.Tests/MigrationBudgetTests.cs`；修改 `GovernanceMigrationRunner.cs`、`GovernanceMigrationWriteFence.cs` 及现有受控入口测试调用和夹具。

**接口：** `MigrationBudget(TimeSpan TransactionTimeout, int SettingsMaxBytes, int RuleMaxBytes, int RuleMaxCount, long TotalMaxBytes)`；`MigrationBudget.Validate()`；`MigrationBudgetScope.Start(MigrationBudget, CancellationToken)` 在数据库连接已打开、开始事务之前启动单调计时并暴露关联取消令牌，`ConfigureTransactionAsync(WebApiDbContext, CancellationToken)` 只配置 Runner 已持有和登记的事务，不创建或接管调用方事务。连接建立使用独立的最多 30 秒取消预算；受控入口必须显式传入已校验预算，不存在不带预算的产品入口。

- [ ] 新增测试 `BudgetRejectsMissingInvalidAndOutOfRangeValues`：缺项、零、负值、非十进制和溢出时拒绝；事务时间为 60–3600 秒，设置单值为 1–1048576 字节，规则单值为 1–16384 字节，规则数为 1–1000000，总读取量为 1–1073741824 字节。隔离测试显式传入 `900/65536/16384/100000/67108864`；这些边界均为计划中的验证值，不代表生产容量，绝不把无效值修正为更宽的值。
- [ ] 先运行上述测试，记录预期失败；实现不可变预算与严格配置解析：`WEBAPI_MIGRATION_TRANSACTION_SECONDS`、`WEBAPI_MIGRATION_SETTINGS_MAX_BYTES`、`WEBAPI_MIGRATION_RULE_MAX_BYTES`、`WEBAPI_MIGRATION_RULE_MAX_COUNT`、`WEBAPI_MIGRATION_TOTAL_MAX_BYTES` 均须显式配置；生产调用不偷偷采用隔离初值。
- [ ] 新增真实 PostgreSQL 测试 `TransactionBudgetCoversLockReadAndCommit` 与 `ShorterExistingTimeoutIsNotRelaxed`。受控入口在开始前启动单调计时和取消令牌，连接后核验 PostgreSQL 17 能设置事务时限，在同一事务中设置不超过预算及现有更短限制的 `transaction_timeout`、`statement_timeout`、`lock_timeout`；单次锁等待不超过 5 秒。无法应用时阻断。
- [ ] 验证达到时限、等待锁、读取及提交期间取消均不会被记成已回滚；用独立连接核验实际回滚／连接释放。测试专用短时限不得成为公开配置绕过 60 秒下限的途径。记录 PostgreSQL 临时空间和 TOAST 解压并不由客户端限额严格覆盖。
- [ ] 在指定隔离测试容器中运行 `dotnet test tests/WebApi.Integration.Tests/WebApi.Integration.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~MigrationBudgetTests`，保留 RED／GREEN 原始日志。

## Task 2：通知读取预算和结果摘要

**文件：** 修改 `src/WebApi.Infrastructure/Notifications/NotificationMigrationPreflight.cs` 及原预检调用测试；新增 `src/WebApi.Infrastructure/Notifications/NotificationValidationSummary.cs`、`tests/WebApi.Integration.Tests/NotificationMigrationBudgetTests.cs`。

**接口：** `NotificationMigrationPreflight.ValidateAsync(WebApiDbContext db, MigrationBudget budget, CancellationToken ct)` 返回 `NotificationValidationSummary`：状态 `Passed`／`NotApplicable`、设置存在性、规则数和已读取 UTF-8 字节数；失败抛只含阶段、原因和可选规则 ID 的受控错误。

- [ ] 写 `SettingsAndRuleByteLimitsApplyBeforeMaterialization`、`CountAndAggregateLimitsFailWithoutWrites`：使用 PostgreSQL 17 JSONB，比较数据库 `::text` UTF-8 实际字节数与既有 .NET 校验器；覆盖 16384/16385、65536/65537、Unicode、压缩值、NULL、超数量、超累计量及最末一条超限。
- [ ] 先运行并保存预期失败；实现有界逐条读取：数据库侧返回长度及条件化内容，超限时不向 .NET 返回该值；逐条累计且在第一个超限值处停止，整数计算检查溢出。服务器端解压和排序仍不是严格内存上界。
- [ ] 保留六字段／两字段旧形态检查及原 `SettingsValueCodec`／`NotificationPolicyValidator` 判定；无数据为 `NotApplicable`，解析未完成不得写 `Passed`。回归原有合法、无效、URL 和历史通知用例。
- [ ] 在同一隔离测试容器中运行 `dotnet test tests/WebApi.Integration.Tests/WebApi.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~NotificationMigrationBudgetTests|FullyQualifiedName~NotificationMigrationTests"`。

## Task 3：同事务成功回执与第 36 个结构指纹

**文件：** 新增 `src/WebApi.Infrastructure/Persistence/MigrationReceiptStore.cs`、`Migrations/20261104090000_Governance24MigrationReceipts.cs` 及其 Designer、`tests/WebApi.Integration.Tests/MigrationReceiptTests.cs`；修改 `GovernanceSchemaFingerprints.cs`、`GovernanceMigrationRunner.cs`、`deploy/initial-core.sql` 和相关迁移前缀测试。

**接口：** `MigrationReceiptStore.InsertAsync(WebApiDbContext db, MigrationReceipt receipt, CancellationToken ct)` 只能在持有的迁移事务内调用；`MigrationReceipt` 包含 attempt ID、实际入口、源／目标迁移范围、可核验数据库身份或 `Unknown`、结构／清单摘要、验证结果、数量／字节、预算、历史操作哈希、可观察构建身份及状态。

- [ ] 写 `ReceiptCommitsWithMigrationAndRollsBackWithIt`、`ReceiptRejectsOrdinaryMutationAndTruncate`、`MissingIdentityIsUnknown`、`ConcurrentAndDuplicateAttemptsRespectReceiptQuota`：提交后存在同 attempt ID 成功回执；故障回滚后回执和业务变更均不存在；UPDATE／DELETE／TRUNCATE 受保护；没有集群身份时不伪造，并发和重复 attempt ID 不得突破容量或覆盖历史。
- [ ] 先运行并保存失败；以新增迁移建有界字段、唯一 attempt ID 和追加保护，记录 `Passed`／`NotApplicable`／`Unknown` 的区别，不把散列称作签名。`WEBAPI_MIGRATION_RECEIPT_MAX_ROWS` 显式限制回执总行数，隔离初值 100000；事务内以互斥表锁串行核算容量并在回执写入前阻断满额，首次建表也须检查容量。不得自动删除旧回执。只按新迁移添加 SQL 标记，保留旧 35 个迁移内容。
- [ ] 在相互独立的 EF 和 SQL 空库分别运行两次生成第 36 个结构指纹，确认相同后才登记；保留原 35 条指纹并验证 0–36 前缀。仅允许已存在的、准确匹配的 SQL 函数格式规范化。
- [ ] 对空库、旧业务数据、已完成数据库、重复调用及受限角色，验证回执与迁移的原子性；运行 `--filter FullyQualifiedName~MigrationReceiptTests` 及历史前缀用例。

## Task 4：独立持久证据目录

**文件：** 新增 `src/WebApi.Infrastructure/Persistence/MigrationEvidenceJournal.cs`、`MigrationEvidenceRecords.cs`、`tests/WebApi.Integration.Tests/MigrationEvidenceJournalTests.cs`。

**接口：** `IMigrationEvidenceJournal.StartAsync(MigrationAttemptStart, CancellationToken)` 创建独占尝试；`AppendAsync(Guid attemptId, MigrationEvidenceRecord record, CancellationToken)` 追加独立记录；`ReadAsync(Guid attemptId, CancellationToken)` 只读返回完整、缺失或损坏状态。记录按版本和枚举分类，单条与目录均有显式上限。

- [ ] 写 `JournalNeverOverwritesAndRejectsUnsafeDirectory`、`JournalConcurrentQuotaIsBounded`、`JournalFlushFailureStopsBeforeMigration`：已有文件／目录、符号链接、权限不安全、并发占额、目录满与文件或目录持久化失败均不能覆盖、清理或开始迁移。
- [ ] 先运行并保留失败；实现受控私有目录、独占创建、禁止链接穿越、跨进程加进程内同步、文件及目录持久化和有界目录配额。部署平台不支持所需安全文件操作时明确阻断；不得以路径前缀或先检查后打开代替句柄身份校验。失败和崩溃后保留原始文件，不自动清理。
- [ ] 增加 `JournalContainsNoSensitiveMaterial`，注入含密码、URL、连接串和服务端异常详情的失败；结果仅保存分类、阶段、ID 和有界数值。不同步存储、磁盘写满或 fsync 出错时，不推断数据库结果。
- [ ] 用 `WEBAPI_MIGRATION_EVIDENCE_DIR`、`WEBAPI_MIGRATION_EVIDENCE_RECORD_MAX_BYTES`、`WEBAPI_MIGRATION_EVIDENCE_QUOTA_BYTES` 和 `WEBAPI_MIGRATION_EVIDENCE_MIN_FREE_BYTES` 显式配置目录及配额；隔离初值分别为测试专属目录、8192 字节、16 MiB 和 1 MiB，不能当作生产要求。并发尝试预留足够空间记录结果；预留不能保证其他系统进程不会耗尽磁盘，最终写入失败仍需保留 `Unknown`。运行 `--filter FullyQualifiedName~MigrationEvidenceJournalTests`。

## Task 5：受控迁移、故障对账及后续步骤

**文件：** 修改 `src/WebApi.Infrastructure/Persistence/GovernanceMigrationRunner.cs`、`src/WebApi.Migrator/Program.cs`；新增 `MigrationAttemptCoordinator.cs`、`MigrationReconciler.cs`、`tests/WebApi.Integration.Tests/MigrationAttemptTraceTests.cs`；更新调用该入口的测试夹具。

**接口：** `MigrationAttemptCoordinator.RunAsync(WebApiDbContext, MigrationBudget, IMigrationEvidenceJournal, CancellationToken)` 返回 attempt ID 与 `Committed`／`Failed`／`Unknown`；`MigrationReconciler.CheckAsync(Guid attemptId, ...)` 只读对账。调用方不能通过旧的无预算／无日志重载绕过受控入口。

- [ ] 写 `StartedBeforeDatabaseMutation`、`CommitUnknownNeverBecomesFailureFromMissingReceipt`、`RollbackConfirmedProducesFailure`、`JournalFailureAfterCommitIsUnknown`。开始文件不可持久化时数据库状态不变；仅已证实未开始或回滚才可记录 `Failed`；提交失联、数据库身份不可核验、可能回退备份及单侧记录缺失均为 `Unknown`。
- [ ] 实现开始记录 → 事务预检／预算／通知校验 → 事务内成功回执 → 提交确认 → 独立完成记录；崩溃恢复只读对账，禁止自动重试或猜测结果。使用经过确认的数据库身份与连续性证据区分能证明提交的情况；缺一项保留未知。
- [ ] `--bootstrap` 和 `--seed-catalog` 仍在迁移提交后执行，分别记录其成功、失败或未知；写文件失败不得把已提交迁移说成失败回滚。CLI 输出只含安全分类、attempt ID 和可复核状态，明确非零退出码与未知的区别。
- [ ] 运行 `--filter FullyQualifiedName~MigrationAttemptTraceTests`，并重跑原 179 项限定迁移／来源回归及调用方兼容测试；保留原有失败。

## Task 6：独立 SQL 与部署配置

**文件：** 修改 `deploy/initial-core.sql`、`deploy/compose.runtime.yml`、`deploy/runtime/init-volumes.sh`、`src/WebApi.Migrator/Program.cs`、`docs/database-core.md`；新增 `tests/WebApi.Integration.Tests/MigrationSqlTraceTests.cs`。

- [ ] 写 `StandaloneSqlRequiresBoundedTransactionAndKeepsEntryContract`、`SqlFailureWithoutCollectorIsUnknown`、`SqlSuccessReceiptIsNotAClaimOfManagedValidation`。纯 SQL 对有旧通知的待升级库继续在写入前阻断；允许场景保持独立可执行，成功回执在自身事务内写入且真实标注入口和校验状态。受控执行传入 attempt ID；直接运行无 ID 时由数据库生成并在成功输出中报告，缺少构建身份时记录 `Unknown`。
- [ ] SQL 入口从会话级 `webapi.migration_transaction_seconds`、`webapi.migration_receipt_max_rows` 获取显式配置，以 PostgreSQL 17 服务端事务超时保留更短限制；直接 `psql` 可通过不含秘密的 `PGOPTIONS` 设置这两个参数。配置缺失、无效或不支持时阻断，不能默默恢复无限时。直接执行失败无法在回滚事务内持久化，应明确显示需外部采集而非声称已记录。
- [ ] 为受控 SQL 执行提供 `--run-sql` 入口与可复用的开始／结果证据和 attempt ID；在专用新连接上用会话级设置传递 attempt ID 和两个预算，防止脚本最外层事务检查清除事务级设置。由配置提供 SQL 文件及预期 SHA256，启动前比较实际字节，哈希只能证明文件匹配、不能证明来源可信。不下载或在命令行暴露连接秘密。验证成功回执与外部记录对账，崩溃／失联为未知。
- [ ] 为 Migrator 提供专用持久卷、10001:10001 私有权限和显式预算配置的候选编排；仅初始化新建的空证据卷，已有卷的属主和模式正确时原样复用、不改内容，不匹配时阻断，不递归改属主或清空。只修改文件，不切换运行环境。用 `docker compose config` 的隔离输入检查无误；未配置预算或证据目录时受控迁移保护性阻断。保留原 4192 卷和账号。

## Task 7：隔离集成验收与交接

**文件：** 新增 `tests/WebApi.Integration.Tests/MigrationBudgetTraceAcceptanceTests.cs`，更新 `docs/database-core.md` 和本计划专属执行记录。

- [ ] 在固定 PostgreSQL 17.11 镜像的独立 owner 中分别测试空库、0–36 历史前缀、含合法历史通知库、异常规则、超限值、无通知库及重入；记录数据、结构和历史摘要，不用真实 4192 数据。
- [ ] 编写 `DisabledWriterStaysFencedAcrossManagedAndSqlRecovery`：以同步屏障验证旧账号在停用前具备写权限，随后分别检查已有连接、迁移期间、取消／超时／进程中止后、重试提交后；只操作一次性角色。真实进程、共享／特权账号和长期隔离仍需外部证据。
- [ ] 编写 `CrashAndStorageBoundariesRemainUnknownUntilProven`：测试持久目录不可用、并发占额、崩溃在 DB 提交前／后与结果写入前／后、回执缺失、数据库身份不可读和模拟回退；`Unknown` 不得自动重试。每个失败保留原始日志和受控测试现场。
- [ ] 运行相关 179 项限定迁移回归、旧写入者 8 项、所有新增测试及必要的领域／构建回归；组间重叠分别报告。仅在现行源码与证据身份一致时报告结果；排查失败而不删除失败记录。
- [ ] 复核原 433 项保护输入、G14 检查点、四个保留测试库及两个保留角色的确切身份。列出所有未闭合项和 EF 事务回调兼容风险；独立审查后才判断能否进入提交／合并阶段。

## 执行记录

- 2026-11-04：用户确认方案 A 和仅用于隔离验证的预算初值。现有源码和 4192 未部署。
- 2026-11-04：一次性 PostgreSQL 17 探针设置 750 ms 事务超时；服务端终止连接，随后观察到未提交标记回滚、无残留会话。探针库经身份核对后清理；原始输出保留在 `.superpowers/sdd/2026-11-04-governance-snapshot-24-integration/migration-budget-probe-20261104/attempt1/`。这不证明产品入口已实现或总回滚时长有界。
