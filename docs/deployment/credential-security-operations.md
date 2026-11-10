# 凭证安全操作手册（G 系列 G01–G13）

合并来源：main `57f2ef2`（tag `governance-g13-57f2ef2`，2026-11-08 合并）。运行边界于 2026-11-10 按主干 `7626115` 源码复核并修正；本手册不证明 4192 或目标环境已安装该版本。关联文档：[当前项目状态](current-project-status.md)、[数据字典](../credential-security-data-dictionary.md)、[P2-4 独立评审](../evidence/governance-merge/p2-4-crypto-review.md)、[迁移双路径证据](../evidence/governance-merge/p2-2-upgrade.json)、[G14 最小子集证据](../evidence/governance-merge/g14-minimal.json)。

## 1. 运行边界（固定主干 7626115 的源码状态）

- **已有运行路径**：普通双 Key 轮换、凭证工作配置撤销、凭证使用证据，以及治理迁移预检与升级守卫。普通轮换通过两次审批发布完成：第一次发布令新旧 Key 并存，消费方切换后撤销旧 Key，再次发布才使撤销进入网关运行快照。工作配置保存不改变已发布快照；目标环境实际安装能力须另行核对。
- **尚未启用**：独立即时撤销、安全检查点/租约准入，以及依赖该通道的正式轮换硬截止。`SecurityLeaseIssuer.IssueAsync` 为关闭的 503 桩；`SecurityAckService`、`CurrentSecurityProofService` 和网关 `SecurityStateStore` 无生产 DI/端点/请求接线。组件与表结构合入不表示这条运行链路已经存在。
- **直接依据**：`ApplicationService.SaveCredentialAsync` 更新凭证工作状态；`ApiKeyMiddleware` 使用当前 generation 中的凭证；`CredentialRotationFlowTests.cs:33–38` 明确断言“撤销后旧 Key 仍为 200，再次发布后才为 401”。独立评审的 274 项安全组件测试及 G14 发布协调故障测试均不能替代实时安全通道端到端验收。
- **更正范围**：本手册此前“已激活”“即时拒绝”“正式双模式已验证”的表述超出以上证据。本次只修正操作口径，保留原始提交、台账和失败/通过证据；不改变实现，也不启用安全路径。

## 2. 签名密钥配置（激活安全路径的前置）

签名组件通过 `SecuritySigningSettings` 等读取配置（`Security:Signing`）；配置有效只证明组件可加载材料，不会接通当前关闭的签发或网关准入路径。

| 配置键 | 说明 |
|---|---|
| `Security:Signing:KeyId` | 签名密钥标识（非秘密，进租约/检查点线格式） |
| `Security:Signing:PrivateKeyFile` | ECDSA P-256 私钥文件（PEM）。**文件权限必须 0600**（无 group/other 任何位），否则加载失败 |
| `Security:TrustedKeys:{keyId}` | 受信公钥（base64）。当前 keyId 的公钥由私钥推导并交叉核对，不一致报 `security_signing_key_conflict` |

失败语义（fail-closed）：缺密钥/坏密钥/权限不对 → `503 security_signing_unavailable`；验证侧无可信公钥 → `503 security_verification_unavailable`。私钥内容不进入任何日志、异常消息或响应体（P2-4 评审确认密钥材料零泄漏，异常统一映射 409/503）。

未来签名密钥轮换协议需覆盖新增 keyId、旧公钥保留期、全节点租约刷新证明与移除旧 keyId 的条件；当前没有可执行的端到端签发/刷新流程，不能仅修改配置便宣称轮换完成。

## 3. 首次检查点引导（待实现与独立评审，当前不可执行）

以下是未来启用的约束与验收要求，尚不是部署步骤。须先完成签发入口、外部见证、恢复协议、ACK/当前证明端点、网关准入及其就绪检查，并通过 P2-4 所要求的同深度独立评审，才能制定固定版本的维护窗口执行单：

1. 固定源码、镜像、节点身份、密钥配置及回退边界；全量备份并冻结发布。恢复后以真实 `SecurityEventHash.Compute` 从 Genesis 逐条核对 `event_hash` 与 `previous_hash`。已有[G13 备份演练](governance-g13-upgrade-rehearsal.md)仅证明临时种入的五事件链可恢复，不证明生产路径已写入账本或引导已完成。
2. 区分升级与灾备路径：pre-G → G 的迁移预检按 P2-2 直接检查目标库，不能把 dump/restore 后的 schema 视为同一指纹。真实目标库的执行仍需单独维护窗口与授权。
3. 在隔离环境验证完整链路：独立证明 → 检查点/租约签发 → 节点验证与持久化 → ACK/当前证明 → 安全准入及就绪状态。
4. 验证未授权、过期、断链、时钟异常、节点重启与写盘失败均按协议拒绝，并保留恢复证据。现有 `/health/ready` 只检查运行配置 generation，不能证明安全租约有效。
5. Unavailable 节点应停止接收流量并保留证据；不能把回退到不执行安全准入的旧镜像写成默认恢复动作。回退须有单独审阅的应用、数据库、节点状态和流量隔离方案，不能以绕过撤销约束换取恢复。

## 4. 当前撤销/轮换流程与未来安全演练

**当前可用流程**：按[账号与凭证操作说明](account-credential-operations.md#双key轮换)先发布新旧 Key 并存，确认消费方已切换，再通过影响预览与修订保护撤销旧 Key，重新审批发布；逐环境核对当前节点的配置版本、部署序列与 Key 调用结果。保存撤销后、重新发布前，旧 Key 仍可能被当前快照接受；不能将管理面状态当作所有网关已拒绝的证明。

**未来即时撤销验收**：在 §3 的链路实现并评审后，验证无需普通配置重发即可全节点拒绝已撤销凭证，且对应安全事件、节点 ACK、当前证明及重启恢复幂等均可独立核验。目前不具备这项验收结论。

**未来正式轮换硬截止验收**：需证明暂停协调服务、保留旧运行快照或中止操作均不能无限延长双钥窗口。现有 `ApiKeyMiddleware` 会检查运行快照中的 `ExpiresAt`，这不等于正式轮换硬截止协议已接入。

**G14 已验证范围**：最小子集 3 项与完整矩阵 4 项覆盖普通发布协调故障。worker 故障须恢复 worker 才能推进终态；节点故障可能以 `ack_timeout` 失败，再创建 retry 发布收敛；Redis 重启不得反写 PG 权威事实。这里的发布 ACK 与 `security_acks` 是不同事实，不能据此宣称节点已从安全账本恢复准入。见[完整故障矩阵](governance-g14-fault-matrix.md)。

## 5. 安全 LKG 恢复约束（安全组件尚未接入生产）

安全 LKG 与普通配置 LKG 不同。P2-4 Minor T3 要求启用前完成非干净停机恢复演练；组件测试不代表现有网关已使用该恢复路径。遇到 `security_lkg_owner_unavailable` 或高水位异常时，先隔离流量并保留目录、标记、文件身份及原始字节；须由未来已评审的所有权与恢复协议决定接管条件。不能把删除 `.security-running`、清空 LKG、回退安全地板或直接从 PG 重建写成未经验证的通用恢复办法。

## 6. 未来启用验收清单（全部保持待验收）

- [ ] 升级守卫预检在目标库通过（`Committed`，含幂等重放）
- [ ] 备份恢复后安全账本哈希链重算逐条一致（`SecurityEventHash.Compute`，Genesis→head，已在[G13 升级演练](governance-g13-upgrade-rehearsal.md)通过）
- [ ] 全节点 ACK 后检查点达 Allow，`/health/ready` 全绿
- [ ] 紧急撤销：所有节点即时拒绝 + 重启恢复回执幂等
- [ ] 正式轮换：双钥窗口并存 + 硬截止撤旧
- [ ] 单网关 Unavailable 隔离流量，恢复/回退不绕过安全地板或撤销事实
- [ ] §5 恢复 runbook 实际执行一次
