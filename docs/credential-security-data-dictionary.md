# 凭证安全数据字典（G 系列账本表）

适用来源：main `57f2ef2`（tag `governance-g13-57f2ef2`）。字段清单提取自最终 EF 模型快照（`20261104090000_Governance24MigrationReceipts.Designer.cs`），与迁移 SQL 逐字一致。关联：[操作手册](deployment/credential-security-operations.md)、[P2-4 独立评审](evidence/governance-merge/p2-4-crypto-review.md)。

> **2026-11-10 运行边界补充（固定主干 `7626115`）**：本页描述表结构及协议语义，不表示对应生产写入、签发、上报路径已接入。`SecurityLeaseIssuer.IssueAsync` 仍为 503 桩，`SecurityAckService`、`CurrentSecurityProofService` 与网关 `SecurityStateStore` 无生产接线；当前普通凭证撤销只修改工作配置，再次审批发布后由新快照生效。安全组件测试与临时 seeder 账本备份演练不能替代实时撤销验收。详见[当前项目状态](deployment/current-project-status.md)。

通用语义：

- **哈希链**：`security_events` 以 `previous_hash`/`event_hash`（SHA-256，char 64）构成链；读取时全链重算校验，主键 + 不可变触发器防分叉/截断。
- **单调时钟**：安全路径时间以数据库单调时钟为准（`security_database_clock_watermark` 高水位 + 可回拨检测），网关侧以单调截止 + 锚定漂移检查抵抗墙钟回拨。
- **不可变账本**：核心账本行不可 UPDATE/DELETE/TRUNCATE（`23514` 不可变触发器），追加式写入。
- **每节点恰一次**：安全 ACK 以 (operation, node, instance) 去重；重复相同 ACK 幂等 no-op，冲突 ACK 拒绝。
- **组织/项目链**：`organization_id`/`project_id` 冗余列与所属环境的组织/项目链一致（治理预检 SQL 校验，不一致拒绝迁移）。

## 安全事件与操作

### `security_events`（安全事件哈希链）
`environment_id uuid` · `security_generation uuid` · `epoch bigint` · `credential_id uuid`（可空）· `event_type varchar(32)` · `operation_id uuid`（可空）· `previous_hash varchar(64)` · `event_hash char(64)` · `occurred_at timestamptz` · `organization_id uuid` · `project_id uuid` · `policy_json jsonb`（可空）
撤销、轮换、ACK 失败等安全事实按发生顺序追加；`event_hash = H(previous_hash || 规范JSON)`。

### `security_operations` / `security_operation_targets` / `security_operation_alerts`
安全操作（撤销/轮换等）与其目标、告警检查；收敛判定 = 全部目标获得独立验证的 ACK。

### `security_acks`（网关操作 ACK 事实）
`operation_id uuid` · `environment_id uuid` · `node_id uuid` · `instance_guid uuid` · `challenge_nonce uuid` · `epoch bigint` · `security_generation uuid` · `checkpoint_hash char(64)` · `success boolean` · `error_code varchar(64)`（可空）· `admission_enforced boolean` · `durable boolean` · `received_at timestamptz` · `applied_at timestamptz`
每节点每操作恰一行；收敛 = 成功 ACK 数 == 目标数。

### `security_ack_verifications` / `security_ack_attempts` / `security_ack_failure_latest` / `security_ack_revalidation_challenges`
ACK 的独立验证记录（七重验证结果、租约绑定）、失败尝试观测（`observation_sequence` 单调）、每操作最新失败摘要、再验证挑战（防重放 nonce）。

### `security_outbox_messages`
安全操作出站消息（可靠投递，失败由轮询兜底收敛）。

## 租约与检查点

### `gateway_security_leases`（节点租约）
`lease_id uuid` · `node_id uuid` · `instance_id uuid` · `environment_id uuid` · `security_generation uuid` · `epoch bigint` · `checkpoint_hash char(64)` · `signing_key_id varchar(64)` · `lease_seconds integer` · `revision bigint` · `issued_at timestamptz` · `fresh_until timestamptz` · `last_issued_lease_until timestamptz` · `organization_id uuid` · `project_id uuid`
控制面签发、按 keyId 签名；`fresh_until` 之后需刷新，旧租约不可回退。

### `gateway_security_current_receipts`（网关当前证明回执）
`node_id uuid` · `instance_id uuid` · `environment_id uuid` · `security_generation uuid` · `epoch bigint` · `checkpoint_hash char(64)` · `head_hash char(64)` · `lease_id uuid` · `fresh_until timestamptz` · `received_at timestamptz` · `organization_id uuid` · `project_id uuid`
网关上报的当前安全状态证明；与 `gateway_security_lease_issuances`/`gateway_security_lease_expiry_observations`（租约签发证据与过期观察，跨实例持久）配套。

### `environment_security_states`（环境安全状态）
`environment_id uuid`（主键）· `security_generation uuid` · `epoch bigint` · `head_hash varchar(64)` · `require_environment_credentials boolean` · `initial_require_environment_credentials boolean` · `lease_seconds integer` · `lease_refresh_seconds integer` · `maximum_clock_skew_seconds integer` · `minimum_snapshot_schema varchar(24)` · `revision bigint` · `updated_at timestamptz` · `organization_id uuid` · `project_id uuid`
环境级安全下限的当前事实（epoch/generation/高水位不可回退）。

### `environment_security_initializations`
环境安全状态的首次引导证据（初始化时点与初始值）。

### `environment_security_source_receipts`（升级来源回执）
`environment_id uuid` · `captured_at timestamptz` · `source_cluster_id varchar(20)` · `source_database varchar(63)` · `source_database_oid bigint` · `source_migration varchar(150)` · `source_history_hash varchar(64)` · `source_schema_hash varchar(64)` · `organization_id uuid` · `project_id uuid`
升级时记录 pre-G 来源库身份（最后迁移 + 历史/模式指纹），与治理预检的指纹比对配套。

## 凭证使用证据

### `credential_usage_batches`（冻结批次）
`node_id uuid` · `collector_id uuid` · `sequence bigint` · `accepted_through timestamptz` · `applied_at timestamptz` · `core_hash char(64)`
节点上报的计数冻结批次（每节点单调 `sequence`），`core_hash` 保护批次内容。

### `credential_usage_hourly`（小时聚合）
`environment_id uuid` · `credential_id uuid` · `application_id uuid` · `hour_start timestamptz` · `count bigint` · `last_used_at timestamptz`
查询面向的聚合（UTC 整小时，保留天数受限）。

### `credential_usage_coverage`（覆盖状态）
`environment_id uuid` · `node_id uuid` · `hour_start timestamptz` · `state varchar(16)`（Available/NoData/Partial/Unknown/Unavailable）· `accepted_through timestamptz` · `durable_through timestamptz` · `gaps jsonb` · `intervals jsonb` · `updated_at timestamptz`
声明每个小时窗口的数据完整性；`NoData` ≠ 0 使用（无数据到达）。

### `credential_usage_collectors` / `credential_usage_collector_instances`
收集器配置与其节点实例（上报方身份）。

## 发布访问上下文

### `release_access_contexts`（发布访问上下文）
`release_id uuid` · `environment_id uuid` · `access_address_revision bigint` · `base_path varchar(512)` · `internal_origin varchar(2048)` · `public_origin varchar(2048)` · `captured_at timestamptz`
发布时冻结的访问地址上下文（审批与投递一致性校验用）。
