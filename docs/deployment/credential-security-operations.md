# 凭证安全操作手册（G 系列 G01–G13）

适用来源：main `57f2ef2`（tag `governance-g13-57f2ef2`，2026-11-08 合并）。关联文档：[数据字典](../credential-security-data-dictionary.md)、[P2-4 独立评审](../evidence/governance-merge/p2-4-crypto-review.md)、[迁移双路径证据](../evidence/governance-merge/p2-2-upgrade.json)、[G14 最小子集证据](../evidence/governance-merge/g14-minimal.json)。

## 1. 运行边界（当前 HEAD 的实际状态）

- **已激活**：凭证轮换（普通/正式双模式）、紧急撤销（撤销后各网关节点即时拒绝）、凭证使用证据（冻结批次 + 小时聚合 + 覆盖状态）、治理迁移预检与升级守卫。以上能力由真实二进制 E2E（双钥轮换、撤销→网关即时拒绝、故障矩阵）验证。
- **未激活（有意分阶段）**：安全检查点/租约准入路径（`SecurityLeaseIssuer.IssueAsync` 为关闭的 503 桩；ACK/proof 端点与网关 `SecurityStateStore` 无 DI/端点接线）。合并不改变运行行为；**激活（签发端点 + 外部见证 + 网关接线）必须先通过同深度独立评审**（P2-4 评审条件①），再按本手册 §3 引导。

## 2. 签名密钥配置（激活安全路径的前置）

控制面/网关从配置读取（`Security:Signing`）：

| 配置键 | 说明 |
|---|---|
| `Security:Signing:KeyId` | 签名密钥标识（非秘密，进租约/检查点线格式） |
| `Security:Signing:PrivateKeyFile` | ECDSA P-256 私钥文件（PEM）。**文件权限必须 0600**（无 group/other 任何位），否则加载失败 |
| `Security:TrustedKeys:{keyId}` | 受信公钥（base64）。当前 keyId 的公钥由私钥推导并交叉核对，不一致报 `security_signing_key_conflict` |

失败语义（fail-closed）：缺密钥/坏密钥/权限不对 → `503 security_signing_unavailable`；验证侧无可信公钥 → `503 security_verification_unavailable`。私钥内容不进入任何日志、异常消息或响应体（P2-4 评审确认密钥材料零泄漏，异常统一映射 409/503）。

轮换签名密钥：新增 keyId（私钥+公钥入配置）→ 旧 keyId 保留在 `TrustedKeys` 直至所有节点完成租约刷新 → 移除旧 keyId。

## 3. 首次检查点引导（激活时，维护窗口内）

升级顺序与动作必须原子化（旧控制面不能给新网关提供可信租约，不支持先升级网关并继续依赖旧控制面）：

1. 窗口开始：全量 PG 备份（账本追加式，备份必须保住哈希链）；冻结发布（不得有 Building/Publishing 中的发布）。备份/灾备恢复后**须用真实 `SecurityEventHash.Compute` 重算并逐条比对 `event_hash` 与 `previous_hash`**（Genesis 起接到 head），确认追加式链完整——已在一次性隔离环境演练通过，见[G13 升级演练](governance-g13-upgrade-rehearsal.md)。
2. 预检：升级守卫 SQL 在目标库执行（G 迁移器自身的治理预检即此守卫；直接对目标库跑 `WebApi.Migrator`，不得经 dump/restore——pg_dump 往返会改变 CHECK 约束序列化文本导致指纹失配，见 P2-2 证据）。
3. 部署控制面 + worker：配置 §2 签名密钥；确认 worker 可签发完整检查点。
4. 逐台升级网关节点：新镜像 → 注册 → 控制面签发**初始完整检查点 + 节点租约** → 网关 ACK（七重验证）→ `SecurityStateStore` 达 Allow → `/health/ready` 变绿。
5. 任一台卡在 Unavailable：该台回退旧镜像（旧代码无安全准入要求，流量不受阻），其余节点继续。

## 4. 撤销与轮换演练（窗口内必做）

**紧急撤销演练**：创建凭证 → 撤销 → **观察所有节点即时拒绝**（各节点 ACK 与安全事件 `security_events` 落库）→ 网关重启 → 恢复回执幂等（重复 ACK 为 no-op，`security_acks` 每节点恰一行）。

**完整轮换**：
- 普通轮换：显式撤销旧凭证（新旧按业务节奏切换）。
- 正式轮换：管理员明确确认硬截止——双钥窗口内新旧并存，硬截止自动撤旧；**硬截止可能在新凭证未验证时中断调用**（已接受的设计约束，演练须覆盖新凭证验证先于截止）。

**故障语义（已验证，G14 最小子集 3/3）**：网关 ACK 事实落库后节点宕机 → 另一节点 ACK 补齐即收敛；worker 发布中途宕机 → 网关轮询兜底收敛，重启后幂等续处理；Redis 协调式重启 → PG 状态字节级不变（缓存不反写），节点从 PG 账本重建运行态。

## 5. 网关恢复（`security_lkg_owner_unavailable`）

网关本地持久化（LKG 目录，0700/0600 + flock 所有权）非干净停机（SIGKILL/断电）后可能残留 `.security-running` 所有权标记，下次启动拒绝接管旧高水位（P2-4 评审 Minor T3）。恢复步骤：确认该节点无在途安全操作 → 停止节点 → 按目录属主核对后移除残留标记 → 启动节点，等待其从 PG 账本重建运行态。该 runbook 须在激活前完成一次真实演练。

## 6. 演练验收清单

- [ ] 升级守卫预检在目标库通过（`Committed`，含幂等重放）
- [ ] 备份恢复后安全账本哈希链重算逐条一致（`SecurityEventHash.Compute`，Genesis→head，已在[G13 升级演练](governance-g13-upgrade-rehearsal.md)通过）
- [ ] 全节点 ACK 后检查点达 Allow，`/health/ready` 全绿
- [ ] 紧急撤销：所有节点即时拒绝 + 重启恢复回执幂等
- [ ] 正式轮换：双钥窗口并存 + 硬截止撤旧
- [ ] 单网关 Unavailable 回退旧镜像，其余节点不受阻
- [ ] §5 恢复 runbook 实际执行一次
