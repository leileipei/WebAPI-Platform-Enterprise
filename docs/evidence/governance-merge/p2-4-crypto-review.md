# P2-4 密码学路径独立评审

评审对象:G 系列合并分支 `feature/credential-governance-merge`(worktree `merge-governance-24-20261104`,HEAD `d18b577`)中的四个密码学/ACK 路径文件。评审人为非作者独立评审。

## 1. 评审范围与方法

### 1.1 被评审文件

| 文件 | 行数 | 职责 |
|---|---|---|
| `src/WebApi.Infrastructure/CredentialSecurity/SecurityAckService.cs` | 192 | 网关发布 ACK 验证;每节点恰一次语义;ack 事实持久化(PG `security_ack_*` 表);收敛 = 独立验证的当前证明 + 不可变操作 ACK |
| `src/WebApi.Infrastructure/CredentialSecurity/SecurityEventHash.cs` | 20 | 安全事件哈希链(SHA-256 + 规范 JSON) |
| `src/WebApi.Infrastructure/CredentialSecurity/SecurityCheckpointSigner.cs` | 19 | ECDSA P-256 检查点/节点租约/用量回执签名 |
| `src/WebApi.Gateway/Security/SecurityStateStore.cs` | 288 | 网关准入状态存储(准入所需状态、时钟锚定、LKG 持久化、防降级) |

### 1.2 关联读取(用于验证上述文件的行为前提)

- `src/WebApi.Domain/CredentialSecurity/SecurityCheckpointVerifier.cs`(签名/租约/回执验证、`SecuritySignedEncoding` 域分离与哈希、P-256 曲线校验)
- `src/WebApi.Contracts/Common/CommandContracts.cs`(`CanonicalJson`:对象键序 `StringComparer.Ordinal` 重排序列化)
- `src/WebApi.Contracts/Security/SecurityAckContracts.cs`、`SecurityLedgerContracts.cs`(线格式)
- `src/WebApi.Infrastructure/CredentialSecurity/SecurityLedgerReader.cs`(账本读取 + 全链重算校验)、`SecurityDatabaseClock.cs`、`SecurityLeaseExpiryRecorder.cs`、`SecurityLeaseIssuanceEvidence.cs`、`SecurityPolicyRules.cs`、`CurrentSecurityProofService.cs`、`SecurityOperationStatusReader.cs`、`SecurityAckRevalidationChallengeService.cs`、`SecurityLeaseIssuer.cs`、`SecuritySigningSettings.cs`
- `src/WebApi.Gateway/Security/SecurityAdmissionGate.cs`、`src/WebApi.Gateway/Storage/SecurityLkgStore.cs`(目录 flock 所有权、符号链接拒绝、0700/0600、有界读写、原子替换 + fsync)
- `src/WebApi.Infrastructure/Gateway/NodeIdentityService.cs`(节点身份 SHA-256 + `CryptographicOperations.FixedTimeEquals`)
- 迁移:`Governance24SecurityGuards.cs`、`20261103185741_Governance24SecurityLedger.cs`、`20261104040000_Governance24AckVerification.cs`、`20261104050000_Governance24AckRevalidation.cs`、`20261104060000_Governance24FailureObservation.cs`、`20261104080000_Governance24DatabaseClock.cs`
- 设计依据:`docs/superpowers/specs/2026-11-04-governance-snapshot-24-integration-design.md`(§3–§8 验证门禁)、`docs/evidence/governance-merge/g14-minimal.json`
- 测试:`tests/WebApi.Domain.Tests/SecuritySignatureTests.cs`、`SecurityPolicyLedgerTests.cs`、`IndependentSecurityEvidenceTests.cs`;`tests/WebApi.Gateway.Tests/SecurityStateTests.cs`、`SecurityRecoveryTests.cs`、`SecurityRenewalAdmissionTests.cs`、`SecurityConcurrentAdmissionTests.cs`、`Governance24SecurityStateTests.cs`、`Support/SecurityFixture.cs`;`tests/WebApi.Integration.Tests/GovernanceSecurityAckIntegrationTests.cs`(68 场景)、`GovernanceSecurityClockIntegrationTests.cs`、`GovernanceCurrentSecurityProofTests.cs`、`GovernanceSecurityCoverageIntegrationTests.cs`

### 1.3 方法

逐行静态审查(验证顺序、fail-open 路径、常量时间比较、防重放、哈希链分叉/截断、密钥材料处理、状态机幂等、异常泄漏、TOCTOU),对照迁移触发表验证数据库第二道防线;随后实际执行测试(见 §4):宿主仅装 SDK 10.0.403 而 `global.json` 严格钉死 10.0.401,故按项目自身 `deploy/compose.e2e.yml` 的固定镜像(`mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80…`、`postgres@sha256:b0f9560a2de0…`)在一次性隔离 Docker 网络中运行;运行前核对测试二进制均晚于被评审安全路径的全部源文件(`Delivery/Releases` 下 4 个与密码学路径无关的较新源文件不影响结论),运行后完整拆除临时容器、网络并销毁临时口令。

## 2. 分文件发现

### 2.1 SecurityAckService.cs

**验证顺序(RecordAsync,84–191 行)**:输入完整性/身份绑定(88)→ 强制成功标志(91)→ 加载可信公钥(缺失即 503 fail-closed,92)→ 签名+租约验证,异常映射为 409(93)→ 跨字段绑定(95:Full 检查点、`CheckpointHash` 与重算值、epoch/generation 相等、AppliedAt 窗口 [IssuedAt−skew, FreshUntil))→ 事务内:全局咨询锁 8901202 + 环境行锁(99)→ 节点身份再验证(99,`RevalidateAsync` 401/409)→ 冻结目标存在且 `ProtocolSupportedAtFreeze`(100)→ 账本全链重算(101,`ReadCompleteLockedAsync`)→ 操作事件/挑战绑定(102:挑战必须等于该操作的冻结挑战,`CredentialId`/`EventType` 按事件类型精确匹配)→ 撤销证明核对(103)→ 策略一致性(104)→ 签发地平线 + 签发证据行 + 单调数据库时钟(105–110)→ 过期/更晚观察检查(116–159,两次时钟采样,`beforeWrite<now` 即冲突重验)→ 已验证则走幂等重复路径(166–176)→ 写 `security_acks` + `security_ack_verifications` + 目标状态(177–185)。

**结论:未发现 fail-open 路径。** 所有坏输入(坏签名、外部节点/实例、过期租约、未签发 LeaseId、篡改租约字段、跨环境)均以 409/503 拒绝,且签名异常统一映射为通用 409(`93` 行 `catch ... when(error is InvalidDataException or ArgumentException)`),不泄漏 `security_signature_invalid` 等内部码;异常消息不含密钥材料。

**防重放/幂等**:
- 首 ACK 要求 `now ∈ [l.IssuedAt, l.FreshUntil)`(179)且租约未被观察过期(116/152/159);重复 ACK 要求与已验证行完全一致(168:generation/epoch/checkpoint 哈希/冻结挑战/量化 AppliedAt/LeaseId),否则 409——精确重试幂等,篡改重试拒绝。
- 租约过期后的精确重试返回 `Duplicate=true`(123–129/166–176),不授予新权威(验证行早已在新鲜期创建;收敛由独立当前证明 + 不可变操作 ACK 驱动,见 `SecurityOperationStatusReader.Confirmed` 73–98 行)。
- 重验证挑战路径(135–147)要求:挑战行存在、`l.IssuedAt ≥ 挑战.CreatedAt`(签发早于挑战的旧租约不可用于重新应用)、AppliedAt 落在挑战窗口内、`now ≥ ExpiresAt` 则提交(推进时钟水印)后 409。
- 失败报告路径(`RecordFailureAsync`,17–83):错误码白名单(13/21)、观察序列必须 +1 递增(42)、同序列内容不一致即冲突(37)、首事实不可变(`security_ack_attempts` 不可更新触发器)、数据库第二道防线 `security_failure_latest_guard`/`security_failure_attempt_nonce_guard`(20261104060000 迁移 64–100 行:序列跳号、未来时间、伪造 nonce 均 23514)。失败报告**不能**翻转收敛:已有验证成功 ACK 后再报失败返回 409(测试 `missing-material-failure`,GovernanceSecurityAckIntegrationTests.cs:306–309)。

**发现(均为 Info/Minor,无 Blocker/Major)**:

| # | 级别 | 位置 | 发现 | 建议 |
|---|---|---|---|---|
| A1 | Info | `SecurityAckService.cs:15–16,17–21` | 失败报告路径仅以节点身份认证(PSK 常数时间比较,`NodeIdentityService.cs:29`),报告体本身无签名。设计注释明确此为有意取舍:攻击者无节点秘密无法报告;恶意节点只能对自己冻结目标报告白名单内错误,且受序列/nonce 数据库触发器约束,不能伪造成功或翻转收敛(测试 `missing-material-failure` 等覆盖)。 | 维持;在启用(wiring)评审时复核端点处的节点认证顺序 |
| A2 | Info | `SecurityAckService.cs:66` | `RequestHash` 是请求规范 JSON 的 SHA-256,仅作诊断指纹,不是认证手段(认证=节点身份+挑战/序列)。 | 无需改动;注释已说明 |
| A3 | Info | `SecurityAckService.cs:102` | 成功 ACK 接受 epoch ∈ [操作事件 epoch, 权威 epoch] 的检查点(不必等于权威 epoch)。head 哈希与 `RequireFullProof` 在该 epoch 上逐一重算;收敛另由 `CurrentSecurityProofService` 独立证明把关(其要求 `p.Epoch==authority.Epoch && p.HeadHash==authority.HeadHash`,CurrentSecurityProofService.cs:40),中间 epoch ACK 不构成绕过。 | 无需改动 |
| A4 | Info | `SecurityAckService.cs:123–129,166–176` | 租约过期后精确重试返回 `Duplicate=true`(诊断性幂等),同时记录过期观察并推进时钟水印,防止数据库时钟回拨复活该租约(测试 `legacy-ack-expired-challenge-watermark`、`status-clock-rollback-after-expiry` 验证水印推进)。 | 无需改动 |

### 2.2 SecurityEventHash.cs

- `Genesis` = SHA-256(规范 JSON `[]`),由测试固定为 `4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945`(`SecurityPolicyLedgerTests.GenesisHashPreservesTheCanonicalEmptyArray`)。
- `Compute`(13–19 行)输入含 PreviousHash → 链式;`PolicyJson` 经 `JsonElement` 归一化后进入 `CanonicalJson`(键序排序),空白/键序不改变哈希(测试 `PolicyHashIgnoresJsonbWhitespaceAndOrdering`)。
- **分叉/截断攻击分析**:
  - 截断:`SecurityLedgerReader.Validate` 要求 `Events.Count==Epoch`(44 行)且 `HeadHash` 等于末事件哈希(53 行)、逐事件重算 `EventHash` 并核对 `PreviousHash`(48 行)——截断链被拒。
  - 分叉:`security_events` 主键 `(environment_id, security_generation, epoch)`(20261103185741 迁移 172 行)+ 同 epoch 双事件不可能;`security_event_immutable` 触发器阻止 UPDATE/DELETE(Governance24SecurityGuards.cs:11);代际变化=新链(代际进入哈希输入),网关侧代际冲突被锁存(`SecurityStateStore` `generationConflict`)。
- **发现**:

| # | 级别 | 位置 | 发现 | 建议 |
|---|---|---|---|---|
| E1 | Info | `SecurityEventHash.cs:13` | 链哈希未加密钥(无 HMAC/秘密输入),完整性依赖 PG 访问控制 + 离域签发的检查点/租约签名。持 PG 写权限的攻击者属完整数据库越权边界,由迁移/部署写栅栏(设计 §7.1)兜底。 | 维持现有信任边界;启用评审时确认部署栅栏测试 |

### 2.3 SecurityCheckpointSigner.cs

- 算法:ECDSA P-256 / SHA-256 / IEEE P1363 固定域拼接(`9–12` 行);`RequireP256`(SecuritySignedEncoding.cs:31)同时校验 KeySize=256 与曲线 OID,验证侧同样校验(SecurityCheckpointVerifier.cs:52)——弱曲线不可用。
- 域分离:检查点/租约/用量回执各有独立 domain 前缀(SecuritySignedEncoding.cs:23),跨协议重放被拒(测试 `SignedMalformedBoundsAndCrossProtocolAreStillRejected`)。
- 密钥绑定:`payload.KeyId` 必须等于配置 KeyId(11 行),防止以他人密钥身份签发;KeyId 格式白名单(1–64 字母数字 `_-.`)。
- 密钥材料处理:私钥仅存在于构造器闭包字符串;`ToString` 只含 KeyId(18 行);异常为通用码(`security_signing_key_invalid`),不含 PEM 文本;加载端强制文件 0600(`SecuritySigningSettings.cs:14`,group/other 位即 503 fail-closed);公钥信任集支持轮换且新旧并存(测试 `TrustedKeyRotationAllowsBothExplicitPublicKeys`)。
- 线格式防护:验证侧 32 MiB 上限、重复字段(大小写)拒绝、未映射字段拒绝(SecurityCheckpointVerifier.cs:35–46;测试 `DuplicateSecurityFieldsCannotHideBehindDifferentCasing`)。

**发现**:

| # | 级别 | 位置 | 发现 | 建议 |
|---|---|---|---|---|
| S1 | Info | `SecurityCheckpointSigner.cs:12` | 每次签名新建 `ECDsa` 实例——性能问题,非安全问题。 | 可选:复用实例 |

### 2.4 SecurityStateStore.cs

**准入决策 fail-closed(`CanAdmitCore`,62–74 行)**:disposed/无状态/时钟异常/已知撤销/Blocked/租约过期/单调时钟回退/代际冲突/epoch 低于地板/无租约/`AllowAuthenticatedTraffic=false`/漂移超限 → `Unavailable` 或 `KnownRevoked`,只有全部条件满足才 `Allow`。

**时钟防护**:`TryTimestamp`(77–99 行)以单调读夹逼墙钟读、单锁内完成;单调时间戳回退即锁存 `clockRegressed`(89 行);`ClockAligned`(105–115 行)要求墙钟漂移落在单调间隔窗 + 2×skew 内,采样宽度超限(`SampleWidthAcceptable`,100–104 行)即拒绝;进程锚点不被重试/后续租约刷新(218–221 行),小步回拨不能重置漂移基线。单调截止 `deadline`(224–232 行,`checked` 溢出即阻断)不随旧租约重放延长(234–237 行);已过期租约的生命周期锁存(252 行 + `LeaseLifetime`)不可被墙钟回拨复活。

**状态机/幂等**:`Apply` 全程持 `updates` 信号量 + 准入写屏障;续写(renewal)仅在"同 epoch、同权威、AllowAuthenticatedTraffic、FreshUntil/deadline 不缩短"时释放读屏障(240–242 行),持久化完成后再取写屏障(249–250 行,`CancellationToken.None`——取消不能中断失败关闭或已完成持久化的发布,251/253–256 行);旧/限制型同 epoch 租约保持独占准入(测试 `RestrictiveSameEpochLeaseCannotAdmitDuringPersistence`、`NewRevocationRetainsExclusiveAdmissionUntilDurablePublication`)。墓碑回归、schema/策略降级、同 epoch head 冲突 → `Block` + `conflictEpoch` 地板(208–216 行),冲突状态不可被普通更新清除(测试 `OldGenerationReplayCannotClearGenerationConflict`、`CorruptRestoredHighWaterCannotBeClearedByAnOrdinarySignedUpdate`)。

**恢复/LKG**:`RestoreAsync`(258–274 行)重验签名与绑定后,恢复出的状态为 `Blocked=true`、`Lease=null`(271 行)——**恢复永不直接准入**,只保留高水位;前进程租约不可复用(184/202 行)。`ApplyCoreAsync` 在 `recoveryChecked` 前发现已有 LKG 即要求走恢复(169–181 行),防止覆盖高水位(测试 `ApplyBeforeRestoreCannotOverwriteExistingHighWater`、`ConcurrentRestoreAndApplyCannotReplaceExistingHighWater`)。写入失败(含意外异常)→ `Block("security_lkg_write_denied")` 并保留中断异常(243–251 行);旧检查点不能治愈写失败(测试 `WriteFailureCannotBeHealedByAnOlderCheckpoint`)。

**文件/进程安全**(`SecurityLkgStore`):路径全程符号链接拒绝(`CheckLinks`)、目录 0700/文件 0600、flock 目录 inode(unlink 重创建不能产生第二持有者,测试 `ReplacingAnOrdinaryLockFileCannotCreateASecondUnixOwner`)、有界读写(32 MiB,测试 `GrowingInputCannotBeReadPastTheConfiguredSecurityFileLimit`)、临时文件 + rename + 目录 fsync 原子替换。

**发现**:

| # | 级别 | 位置 | 发现 | 失败场景 | 建议 |
|---|---|---|---|---|---|
| T1 | Minor | `SecurityStateStore.cs:60–61,67,71–72` | 公开 API `CanAdmit(credentialId, utcNow)` 接受调用方提供的墙钟时间。分析:未来向 utcNow 只会放宽 `IssuedAt-utcNow` 窗口,但 `TryTimestamp` 同时以**采样真时** `utc>=FreshUntil` 与单调 `stamp>=state.ExpiresAt`(93 行)把关,单调截止主导真实时间,故无准入绕过;过去向 utcNow 只会更严格。风险是未来接线误用(例如传入客户端时间)。 | 若接线传入不可信时间,仅造成"提前 Unavailable"(fail-closed 方向),不造成越权;但应约定仅传本节点 TimeProvider 或 null | 接线评审时强制:utcNow 只能来自网关自身 TimeProvider;或将参数改为内部使用 |
| T2 | Minor | `SecurityStateStore.cs:122–128,138–140` | `observedHeads` LRU(4096)驱逐后,被驱逐 epoch 上出现的冲突 head 按"新历史观察"缓存而非冲突。缓解:epoch≤已观察 full epoch 的冲突事实仍被拒(135 行);威胁前提是同一受信签名密钥对同一 epoch 签出两个不同检查点(控制面分叉/失陷),超出该密钥信任模型;准入只使用最新 full 检查点。 | 控制面分叉 + 4096 epoch 垃圾检查点后,在 LRU 窗口外注入冲突旧 head——不改变当前准入状态,仅丢失一次恢复告警 | 接受(签名密钥信任边界内);可在设计文档补一句残余风险说明 |
| T3 | Minor | `SecurityStateStore.cs:275–287` + `SecurityLkgStore.cs:89–97` | 非干净停机(崩溃/写失败)后运行标记 `.security-running` 残留,下次启动 `CreateRunningMarker`(FileMode.CreateNew)失败 → 所有权获取失败 → `security_lkg_owner_unavailable`,网关保持不可用直至运维按恢复手册处置(测试 `FailedWriteThenRestartCannotReopenAnOlderPersistedHighWater` 固化该行为)。有意的 fail-closed 可用性取舍。 | 网关崩溃重启后无法自愈,需人工恢复 | 启用前确认恢复 runbook 存在且演练过(fb8ccdd 已提交 runbook 文档,需在门禁中核对可执行性) |

无密钥材料进入日志/异常的发现:`SecurityStateStore` 错误码全部为固定字符串;异常仅在 `interrupted` 时以 `ExceptionDispatchInfo` 重抛(251 行),对象为磁盘 IO 异常,不含密钥。

## 3. 验证矩阵对照

**仓库中不存在 G01–G13 验证矩阵文档**(已检索 `docs/` 全部 `验证矩阵`/`verification-matrix`/`G0x` 字样;仅 `docs/evidence/governance-merge/g14-minimal.json` 记录 G14 最小子集 D1-1/2/3 通过)。设计文档 `docs/superpowers/specs/2026-11-04-governance-snapshot-24-integration-design.md` §8 列出验证门禁,以下对照以该门禁为基准,全部测试为本评审实际执行并通过(§4):

| 设计门禁(§8/§3.2/§4/§6) | 被评审文件 | 证明测试(全部已执行通过) |
|---|---|---|
| 3.2-4 哈希链与检查点/租约签名分别校验 | SecurityEventHash | `SecurityPolicyLedgerTests`:ValidPolicyEventsDoNotInventTombstones、CutoverCannotBeWeakened、Governance24CannotReturnToGovernance23、RevocationEventRequiresItsExactTombstone、GenesisHashPreservesTheCanonicalEmptyArray;`SecuritySignatureTests`:TrustedSignatureBindsEveryCheckpointField、WrongEnvironmentUnknownKeyAndCorruptSignatureAreRejected、SignedMalformedBoundsAndCrossProtocolAreStillRejected、DuplicateSecurityFieldsCannotHideBehindDifferentCasing |
| 2.3 两种方言不互降、2.4 独立形状 | SecurityStateStore | `Governance24SecurityStateTests`:Network23RequiresFreshTrustedStateAndCannotBypassGovernance23Floor、Governance24RequiresItsOwnShapeAndCannotFallBackToEither23Dialect |
| 高水位/LKG 不降级、断电恢复、前进程租约不可复用 | SecurityStateStore | `SecurityRecoveryTests`:ApplyBeforeRestoreCannotOverwriteExistingHighWater、ConcurrentRestoreAndApplyCannotReplaceExistingHighWater、CorruptSecurityFileFailsClosed、RestoredSecurityFactsCannotTrustPreviousInstanceLease、FailedWriteThenRestartCannotReopenAnOlderPersistedHighWater;`Governance24SecurityStateTests.RestartCannotReuseAFormerInstancesLeaseEvenForLegacyNetwork23` |
| 墙钟回拨/最大偏差/单调截止 | SecurityStateStore | `SecurityStateTests`:WallClockRollbackOutsideAllowedSkewFailsClosed、RepeatedSmallWallClockRollbacksCannotResetTheDriftBaseline、LeaseReplayDoesNotExtendDeadline、ClockRollbackCannotExtendMonotonicLeaseBudget、ExpiredLeaseCannotReviveEvenWhenWallClockRollbackStaysWithinSkew、LongClockSamplingPauseCannotExtendMonotonicDeadline、ExcessivelyWideClockSampleCannotAuthorizeEvenBeforeNominalExpiry |
| 数据库单调时钟不可伪造/回退、升级保留未来证据 fail-closed | SecurityAckService(时钟) | `GovernanceSecurityClockIntegrationTests`:DatabaseClockWatermarkRequiresLockAndCannotBeForgedOrRegressed、ClockGuardRetainsTransactionLockAfterSessionLockIsReleased、UpgradeRetainsFutureLegacyEvidenceAndStartsFailClosed、DatabaseClockObservationRollsBackWithBusinessTransaction |
| 4.1 节点/实例身份绑定、外部节点拒绝、恰一次/重放 | SecurityAckService | `GovernanceSecurityAckIntegrationTests.AckRequiresRealSignedMaterialAndIndependentCurrentProof` 场景:bad-signature、wrong-challenge、wrong-instance、stale-node、unissued-lease-id、no-issuance-evidence、altered-lease-claims、applied-after-expiry、applied-at-expiry、lease-outlives-checkpoint、checkpoint-expired-lease-fresh、future-latest-lease、legacy-ack-replaced-instance、legacy-ack-expired-replay、expired-first-ack-replay、expired-lease-reuse-first-ack、expired-lease-duplicate-ack、legacy-ack-concurrent、dirty-context、ack-cross-environment-isolation |
| 收敛 = 独立当前证明 + 不可变操作 ACK(当前证明不能替代操作 ACK) | SecurityAckService + StatusReader | 同测试:ack-first、proof-first、ack-cross-environment-isolation、newer-lease-issued-status、rollback-before-newer-lease-status、status-clock-rollback-after-expiry、future-receipt-status、future-verification-status、expired-current-receipt-status;`GovernanceSecurityCoverageIntegrationTests.MissingCurrentNodeOrEnvironmentCannotBeReportedFullyApplied` |
| 6.x 失败/超时不自动视为成功;失败报告有界序列 | SecurityAckService(RecordFailureAsync) | 同测试:missing-material-failure(含 23514 触发器直查、search_path 影子函数攻击、TRUNCATE/DELETE 阻断)、future-failure-history、future-failure-sequence、conflicting-failure-history、unexpected-failure-constraint、legacy-ack-expired-failure-retry、legacy-ack-expiration-during-failure-save、premature-expiry-future-forgery、future-current-receipt-forgery、future-ack-verification-forgery、future-challenge-forgery |
| 租约过期观察跨实例持久(新连接不复活) | SecurityAckService | 同测试:proof-expiry-before-reader、ack-expiry-before-reader、duplicate-ack-expiry-before-reader、proof/ack/status-expiry-client-cancel、-commit-cancel、-validation-cancel、ack-cross-expiry-read、expiry-concurrent-status、expiry-during-status-read |
| 3.3/5 快照与当前策略/租约一致性、恶意快照 fail-closed | SecurityStateStore | `Governance24SecurityStateTests`:SnapshotMustMatchCurrentPolicyAndCannotOutliveItsLease、MalformedSnapshotFailsClosedWithoutEscapingTheAdmissionCheck;`SecurityStateTests.MismatchedCheckpointLeaseAndExpiredLeaseCannotMakeReady` |
| 并发准入/失败关闭存活取消 | SecurityStateStore(门闩) | `SecurityConcurrentAdmissionTests` 全部 7 项(含 MandatoryFailureCloseSurvivesCancellationAndConcurrentReaders、HealthyRenewalPublicationWaitsForEveryConcurrentReader);`SecurityRenewalAdmissionTests` 全部 24 项(含 RevocationCannotOvertakeAnInFlightRenewalDiskWrite、CancellingPublicationAfterFailedRenewalCannotLeaveOldTrafficAllowed) |

**矩阵缺口**:
1. G 系列矩阵本体不在仓库(在合并计划文档中,仓库外)——建议将本评审 §3 的"门禁 → 测试"映射并入该矩阵存档。
2. 独立证据增量(ExternalWitness/`IndependentSecurityEvidenceTests` 48 项)不属于本四文件范围,但其挑战一次性/伪造拒绝测试已通过(§4)。

## 4. 测试证据(本评审实际执行)

运行方式:隔离 Docker 网络(用后即拆)+ 项目固定镜像 SDK 10.0.401 / postgres;集成测试前核对安全路径全部源文件早于测试二进制(仅 Delivery/Releases 4 个无关源文件更新,不影响执行代码与评审代码一致性)。

| 套件 | 结果 |
|---|---|
| `WebApi.Domain.Tests`(SecuritySignatureTests + SecurityPolicyLedgerTests + IndependentSecurityEvidenceTests) | **71 通过 / 0 失败** |
| `WebApi.Gateway.Tests`(SecurityStateTests + SecurityRecoveryTests + SecurityRenewalAdmissionTests + SecurityConcurrentAdmissionTests + Governance24SecurityStateTests + SecurityApplicationReceiptTests) | **107 通过 / 0 失败** |
| `WebApi.Integration.Tests` GovernanceSecurityAckIntegrationTests(68 场景) | **68 通过 / 0 失败**(52s) |
| `WebApi.Integration.Tests` Clock(4)+ CurrentProof(21)+ Coverage(2) | **28 通过 / 0 失败**(20s) |
| **合计** | **274 通过 / 0 失败** |

## 5. 缺陷清单汇总

| 级别 | 数量 | 条目 |
|---|---|---|
| Blocker | 0 | — |
| Major | 0 | — |
| Minor | 3 | T1(CanAdmit 外部时间参数,接线约束)、T2(observedHeads LRU 驱逐窗口,签名信任边界内)、T3(非干净停机需人工恢复 runbook) |
| Info | 6 | A1(失败路径身份认证模型)、A2(RequestHash 诊断指纹)、A3(中间 epoch ACK 由独立证明兜底)、A4(过期后精确重试语义)、E1(链哈希未加密钥,PG 信任边界)、S1(每签名新建 ECDsa,性能) |

补充备注(被评审文件之外,供门禁参考):
- `NodeIdentityService.Hash` 为秘密的裸 SHA-256(NodeIdentityService.cs:28),比较用 `CryptographicOperations.FixedTimeEquals`(29 行)。对 32–1024 字符高熵 PSK 可接受;若未来降低秘密熵,应改 KDF。
- **集成状态**:截至 `d18b577`,四条路径均未激活——`SecurityLeaseIssuer.IssueAsync` 为有意关闭的 503 桩(SecurityLeaseIssuer.cs:9–16:"This entry point remains closed until the external witness, recovery protocol and gateway admission are integrated and reviewed");`SecurityAckService`/`CurrentSecurityProofService`/`SecurityStateStore` 在 `src/` 中无 DI 注册、无端点/调用方(ControlPlaneApp.cs/GatewayApp.cs 已核对)。合并本身不改变运行行为;**激活(签发端点 + 外部见证 + 网关接线)必须另行通过同等级独立评审**。

## 6. 结论

**判定:PASS(合并门禁,附条件)。** 四个文件未发现 Blocker/Major 缺陷:签名/验证路径全面 fail-closed(缺密钥、坏签名、外部节点/实例、过期/重放材料一律 409/503);ECDSA P-256 + 域分离 + 密钥 ID 绑定构成完整的检查点/租约线格式;事件哈希链有重算校验、主键与不可变触发器防分叉/截断;ACK 幂等语义(精确重试幂等、篡改重试拒绝)由应用层与数据库触发器双层保证,且以单调数据库时钟(可回拨检测)与过期观察(跨实例持久)防时钟回拨重放;网关侧准入以单调截止 + 锚定漂移检查 + 失败关闭状态机抵抗墙钟回拨与取消竞态;密钥材料未进入任何日志/异常。274 项安全测试在本评审环境全部通过,失败路径(坏签名、断链、重放 nonce、外部节点、跨环境、未来时间伪造、search_path 影子函数等)均有具体测试方法证明。条件:① 激活路径(签发/见证/接线)尚未存在,须在其落地时按本评审同等深度复审后方可启用;② T3 的人工恢复 runbook 须在启用前核对可执行;③ 建议把 §3 的门禁-测试映射并入 G 系列验证矩阵存档。T1/T2 为接线期约束与残余风险说明,不阻塞本合并。
