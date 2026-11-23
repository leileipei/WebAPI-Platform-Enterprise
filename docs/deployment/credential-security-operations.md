# 凭证安全操作手册

适用源码为 `7e8a1d9`，核对日期为 2026-11-23。普通凭证轮换走审批发布。治理通道由控制面取得独立见证后签发检查点和租约，每个网关实例分别核验并上报。目标生产环境仍登记为 `awaiting_target`。[项目状态与预生产回执](current-project-status.md#p12-项目主管批准结项)

## 治理启用条件

标准宿主默认关闭凭证治理。部署治理 2.4 时，各进程须使用同一部署的信任配置。对照下表核对正在运行的版本和配置。[标准宿主配置与见证协议](credential-governance-witness.md#2026-11-20-标准宿主配置)

| 组件 | 运行前提 |
| --- | --- |
| 控制面和 Worker | 两者显式设置 `CredentialGovernance:Enabled=true`，使用相同的 `JournalId` 权威与代次，以及相同的 2.4 发布环境名单 `Environments`。各自使用有写入权限的客户端证书 `WriterCertificateFile` 访问独立持久日志 `GovernanceJournal`。完成治理迁移并登记独立账本，`RecoveryHistory` 须能确认该环境的历史与本地账本一致。 |
| 控制面签发 | `TimeEvidence` 和 `HistoryEvidence` 使用分别固定的 HTTPS 端点与签名公钥。`Security:Signing:KeyId` 和 `Security:Signing:PrivateKeyFile` 指定 ECDSA P-256 签发者及私钥 PEM，私钥可按 0600 挂载。`Security:TrustedKeys` 中同一 KeyId 的公钥须与签发者匹配。租约签名密钥须独立于见证密钥。 |
| 每个网关 | 显式启用 `CredentialGovernance:Enabled=true`，用 HTTPS 的 `Gateway:ControlPlaneUrl` 和已登记的节点身份连接控制面。配置 `TimeEvidence`、`HistoryEvidence` 及 `CredentialGovernance:LeaseVerificationKeys`，按签发 KeyId 固定租约公钥。网关独占安全状态目录，重启沿用原目录。自动续租要求 `Gateway:AutomaticUpdates` 开启。 |

首次登记由授权操作者调用 `POST /api/v1/credential-governance/journal/enroll`，核对目标数据库及独立账本的初始位置。签发密钥轮换需另行明确新 KeyId、旧公钥保留期和移除条件，逐节点留存新租约刷新证明。[迁移与账本登记](credential-governance-witness.md#数据库与控制面入口)

## 节点同步与故障排查

以下三个端点均为节点内部 POST 请求，使用节点 Bearer 凭据。核对时按同一环境、节点 ID、实例 ID 和租约关联请求与记录。[控制面节点端点](../../src/WebApi.ControlPlane/Gateway/InternalNodeEndpoints.cs) · [网关续租与上报](../../src/WebApi.Gateway/Security/WitnessedGatewaySecurity.cs)

1. 网关请求 `/internal/v1/nodes/{id}/security/material?instanceId={instanceId}`。控制面核对节点能力、账本连续性、独立时间及完整历史，让日志见证按已确认的位置预留本次租约，再向 PostgreSQL 提交不可变发行记录。交付时仍在有效期内才返回 200 和签名的环境检查点、实例租约。[租约签发实现](../../src/WebApi.Infrastructure/CredentialSecurity/SecurityLeaseIssuer.cs)
2. 网关核对签名、环境、节点、实例和检查点摘要，用独立时间与历史再次验证，并将状态持久化到本机。随后向 `/internal/v1/nodes/{id}/security/current-proof` 上报当前证明，控制面核验并接受后返回 204。[当前证明核验](../../src/WebApi.Infrastructure/CredentialSecurity/CurrentSecurityProofService.cs)
3. 租约携带待确认操作的挑战时，网关用同一实例的签名材料和挑战值向 `/internal/v1/nodes/{id}/security/ack` 逐项上报。核对操作传播须查对应的安全 ACK，普通配置发布另有自己的 ACK。[操作 ACK 核验](../../src/WebApi.Infrastructure/CredentialSecurity/SecurityAckService.cs)

| 观察结果 | 排查依据 |
| --- | --- |
| 签发返回 `503 security_evidence_unavailable` | 已认证节点在默认关闭时会得到此响应。启用后检查账本与恢复历史是否一致、时间和历史证明是否新鲜、见证是否确认本次预留，以及数据库提交和交付时是否仍在有效期内。预留回复丢失时仍需保留见证侧记录。 |
| 其他配置或身份错误 | `503 credential_governance_unavailable` 可来自控制面的见证客户端配置或写入证书。`503 security_signing_unavailable`、`503 security_signing_key_conflict` 分别提示签发密钥不可用或公钥冲突。节点身份无效返回 401，实例已替换或缺少所需协议能力可返回 409。网关启用时的信任配置或控制面 URL 无效也可能在启动时直接报错。 |
| 网关认证请求返回 `503 security_state_unavailable`，或 `/health/ready` 返回 503 | 检查本实例的租约是否有效，以及当前快照是否满足检查点记录的安全代次、最低快照版本及环境凭证策略。注册治理后，就绪检查同时要求当前路由配置及与该配置匹配的安全状态。有效安全状态中已记录撤销的 Key 返回 `401 invalid_api_key`。就绪响应仅反映被查询实例当时的状态。[认证准入](../../src/WebApi.Gateway/Security/ApiKeyMiddleware.cs) · [就绪判定](../../src/WebApi.Gateway/Security/IGovernanceAdmissionStateProvider.cs) |

## 撤销、轮换与验收

**普通双 Key 轮换。** 第一次审批发布令新旧 Key 并存。消费方切换后，在工作配置中撤销旧 Key，再次审批发布，逐环境验证当前网关已接受新 Key、拒绝旧 Key。两次发布之间，原快照仍可能接受旧 Key。[普通双 Key 轮换步骤](account-credential-operations.md#双key轮换)

**治理撤销与正式轮换。** 治理撤销先落账。查询回执时按原操作范围复核独立日志、入口覆盖以及目标实例的 ACK 和有效租约证明，结果依证据为 `FullyApplied`、`Partial` 或 `Unconfirmed`。正式轮换须另有可信租约期限证明，控制面按已登记的硬截止限制续签，Worker 按账本推进到期撤旧。[部署证据及撤销回执](credential-governance-witness.md#部署证据及撤销回执) · [正式轮换硬截止](credential-governance-witness.md#正式轮换硬截止)

**现场验收。** 专属预生产的 `accepted_preproduction` 绑定候选 `6b0cf2c` 和主线 `1ead23c`。在目标环境验收时，需用实际独立服务、受控入口及旧进程隔离证据，在两台网关上分别核验签发与续租、撤销后的业务请求、轮换硬截止及重启恢复。保留逐实例证明和真实客户端的请求结果。[预生产批准记录和生产状态](current-project-status.md#p12-项目主管批准结项)

## 安全 LKG 恢复约束

安全 LKG 与普通配置 LKG 分别持久化。演练期间保留每个网关的私有目录及独立见证状态，分别核对备份、数据库账本、见证高水位与实际业务准入。

回退数据库拒绝下发和签发时，网关可从完整签名 LKG 恢复拒绝高水位。进程独占目录、已注册节点与原租约绑定一致、运行期间未收到后续材料且本机时钟正常时，正常退出会在原目录复核 LKG 原字节后清理本进程的标记。还原一致数据库后，新实例仍须重新取得签名租约及独立时间和历史证明，上报自己的当前证明，并对待确认操作回报 ACK 后核验业务。

未清理的 `.security-running`、无法验证的高水位或失效的目录所有权均保持关闭。遇到这些情况先隔离流量并保留目录、标记、文件身份及原始字节。非干净停机按已评审的所有权与恢复流程处理，禁止通过删除标记、清空 LKG 或重建安全地板恢复准入。

## 历史范围（G01–G13）

`57f2ef2`（标签 `governance-g13-57f2ef2`）合入 G01–G13 安全组件。2026-11-10 核对的 `7626115` 上，`SecurityLeaseIssuer.IssueAsync` 固定返回 `503 security_evidence_unavailable`，网关安全准入尚未接线，`/health/ready` 只检查配置代际。PR #18 合并提交 `c65a0c0` 已接入经见证的签发和网关准入。[PR #18 合并后的凭证治理](current-project-status.md#pr-18-合并后的凭证治理)

P2-4 的 274 项安全组件检查通过，合并评审要求后续复审启用链路并核验恢复手册。P2-2 记录两条迁移路径。G13 备份演练验证临时种入的五事件链恢复，G14 最小子集和完整矩阵记录普通发布协调故障。目标数据库的升级预检须以该库迁移前的实际指纹为准，恢复演练另行保留从 Genesis 到账本头的逐条哈希证据。[数据字典](../credential-security-data-dictionary.md) · [P2-4 评审](../evidence/governance-merge/p2-4-crypto-review.md) · [P2-2 迁移](../evidence/governance-merge/p2-2-upgrade.json) · [G13 备份演练](governance-g13-upgrade-rehearsal.md) · [G14 最小子集](../evidence/governance-merge/g14-minimal.json) · [G14 故障矩阵](governance-g14-fault-matrix.md)

G13 演练记录所引的第 3 节“首次检查点引导”和第 6 节“演练验收清单”沿用当时的章节编号。[G13 当时的手册原文](https://github.com/leileipei/WebAPI-Platform-Enterprise/blob/e86aafef7940cc3922035285e6b836ad1fd9c1e8/docs/deployment/credential-security-operations.md)
