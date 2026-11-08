# G 系列(凭证治理)维护窗口演练:全量 PG 备份保住安全账本哈希链

2026-11-09。本轮在一次性本地环境完成了第 5 阶段(维护窗口部署)最未验证、最高价值的风险演练:**对持有有效凭证治理安全账本的数据库做全量 `pg_dump`,恢复到全新库后,哈希链仍逐字节一致、且用真实的 `SecurityEventHash.Compute` 重新校验通过。** 环境已拆除,未触碰原 `webapi-enterprise-local-*` 容器,未新增源码提交。

本结果承接 [凭证治理安全运维手册](credential-security-operations.md) 第 3 节(首次检查点引导)与第 6 节(演练验收清单)。第 3 节第 1 步要求"全量 PG 备份(账本为追加式,备份必须保住哈希链)"。此前各阶段(P0–P4)验证的是升级/迁移与契约正确性;本轮专门验证**备份—恢复这条数据路径**,因为它决定了维护窗口内一旦需要回滚,追加式安全账本的可信链是否完整。机器可读证据见 [p5-rehearsal.json](../evidence/governance-merge/p5-rehearsal.json)。

## 演练对象与边界

| 项目 | 本轮实际结果 |
| --- | --- |
| 源提交 | `main e86aafe`(tag `governance-g13-57f2ef2`) |
| Compose 项目 | `webapi-enterprise-e2e-g13p5-1794151839`(一次性,运行后连卷拆除) |
| Postgres 镜像 | `postgres@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24`(固定) |
| 迁移尝试 | `Migration attempt cab48714-3eee-4975-a3c6-e175eea323b1: Committed; reason=None`(裸 `WebApi.Migrator`,35 个迁移全部落到空库) |
| 演练库 | `webapi`(已迁移 + 种入账本)→ `webapi_restore`(全新库,仅用 dump 恢复) |
| 备份方式 | 普通格式 `pg_dump -U webapi -d webapi`(全部 98 张 public 表)→ `psql` 恢复到 `webapi_restore` |
| 未验证项 | 未验证 dump-重建的 schema 能否通过治理指纹预检(见下"与 P2-2 的关系") |

## 为什么用 seeder 造账本

`main` 当前 HEAD 上**没有任何生产代码路径会写 `security_events`**:发放租约的 `SecurityLeaseIssuer.IssueAsync` 是封闭的 503 桩,且没有 DI/端点接线(P2-4 结论);吊销/轮换只改凭证状态列。若不做种入,备份里就没有哈希链可验证。因此用一个临时 seeder(引用仓库真实 `WebApi.Infrastructure` 工程)以**真实的 `SecurityEventHash.Compute`** 和**真实实体**构造一条合法 5 事件链,并满足库内守卫触发器的约束:

- `environment_security_initializations` 只能以 `Unknown` 或 `FreshNonStrict`(带非零 `fresh_proof_id`)插入,不能直接插成 `Governed`(由 `environment_initialization_insert_guard` 强制)——seeder 用 `FreshNonStrict`。
- `environment_security_states` 首次插入要求存在一条 `state IN (FreshNonStrict, …)` 的初始化记录、`epoch = 0`、且 `head_hash = Genesis`(由 `security_initial_policy_guard` 强制)。
- `security_events` 的 `AltKey (OperationId, EnvironmentId)` 唯一 → 每事件一个独立 `security_operations` 行。

`occurredAt` 精度处理:PG `timestamptz` 为微秒精度,`.NET DateTimeOffset` 更细。seeder 用 `DateTimeOffset.FromUnixTimeMilliseconds()`(整秒 UTC)写;校验端在重算前对读回值执行 `ToUniversalTime()`,因此对 Npgsql 读回时的时区偏移表达免疫。

## 校验方法

校验器按 `Epoch` 顺序读回事件,逐条:先 `e.OccurredAt = e.OccurredAt.ToUniversalTime()`,再 `recomputed = SecurityEventHash.Compute(e)`,断言 `recomputed == e.EventHash`(本条哈希成立)且 `e.PreviousHash == head`(链链接成立),然后 `head = e.EventHash`。Genesis 为常量 `4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945`。分别在**存活库**和**恢复库**上各跑一次。

## 门禁结果

| 门禁 | 结果 |
| --- | --- |
| 存活库哈希链 | `VERIFY_OK db=webapi count=5 head=2350d6936582545913874d1eaac9073a5251ddc4d58cf9aac0b744d05457e736` |
| 恢复库哈希链 | `VERIFY_OK db=webapi_restore count=5 head=2350d6936582545913874d1eaac9073a5251ddc4d58cf9aac0b744d05457e736` |
| 存活 vs 恢复 head | 逐字节一致(`headIdenticalLiveVsRestored: true`) |
| 完整链(两库) | epoch 1–5 = `1251f52f…` / `51007302…` / `c6683cac…` / `ae4922cb…` / `2350d693…`(两库逐字节一致) |
| 备份体积 / 摘要 | `dump_bytes=378265`,`sha256=f80da5ffc7bb644b28bc2c6b6dd3b8c0496a8eb8cb9cfee843a100da3c95d21c` |
| 行数(存活 = 恢复) | `security_events` 5 = 5,`security_operations` 5 = 5,`environment_security_states` 1 = 1 |
| 表数(两库 public) | 98 = 98 |

完整哈希、env/generation GUID 与全部计数见 [p5-rehearsal.json](../evidence/governance-merge/p5-rehearsal.json)。

## 与 P2-2 的关系(两条正交路径)

P2-2 证明:整库 dump/恢复往返**不是 schema-指纹-canonical**——`pg_dump` 会重新序列化 CHECK 约束数组字面量,治理指纹预检会拒绝 dump-**重建**的 schema,因此生产从 G 前库升级到 G 必须**直接跑 G 迁移器**、绝不用 dump/恢复。

本轮对象不同:测的是**账本数据**(哈希链行),且它处在**已迁移、由 EF 创建的 schema** 之上。这条**数据路径**在普通备份/恢复下是 canonical 的,哈希链完整保留。二者不矛盾:

- **迁移路径**(G 前库 → G):直接跑 G 迁移器,不走 dump/恢复(P2-2)。
- **备份/灾备路径**(已 G 库的备份与灾难恢复):普通 `pg_dump`/`psql` 恢复后账本链完整。灾备应直接对恢复后的库启动 G 服务,**不重跑迁移**——因为 dump 重建的 schema 指纹与 EF 创建的不同,若误跑迁移器其指纹预检可能拒绝;而 G 服务启动时不跑该预检(只有迁移器跑),恢复后的 schema + 数据 + 链均可正常使用。

## 结论与残留

- 本轮证明:持有合法凭证治理安全账本的库,其全量普通格式 `pg_dump` 恢复后哈希链逐字节一致且重新校验通过,**满足第 3 节"备份必须保住追加式账本哈希链"的要求**。
- 残留:这是本地一次隔离演练;真实目标环境的备份窗口、恢复演练与切换授权仍需另行执行。dump-重建 schema 能否通过治理指纹预检不在本轮范围内(按上述正交性,灾备不重跑迁移)。
- 未改产品源码;seeder 与编排脚本位于 gitignored 的 `.runtime/g13-p5w5/`。
