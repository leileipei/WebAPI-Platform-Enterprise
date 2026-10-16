# 执行裁决与代价

本批按已授权范围逐任务实施；以下逐项保留执行裁决，不代替升级批准。

1. Ruling: native create_worktree returned Not a git repository because chat root contains the enterprise subrepository; use ignored project-local Git worktree from approved baseline. App attachment reported not managed; ordinary Git isolation remains valid. Cost if wrong: UI cannot manage/archive this checkout automatically, cleanup must inspect Git ownership.

2. Ruling: plan has no commit steps by user preference. Preserve ledger and uncommitted source manifests rather than deleting scratch or claiming BASE..HEAD captures implementation. Cost if wrong: final reviewer must inspect untracked files as well as git diff.

3. Ruling: bootstrap RED ran with existing isolated settings test runner before the new SSO runner existed; the SSO runner now creates owned projects and verifies both containers and volumes. No behavior weakened; cost if wrong: RED provenance must distinguish bootstrap project from final SSO project.

4. Ruling: foreign keys/checks live in four focused entity configurations and generated migration, rather than unrelated CoreConstraints edits; EF applies assembly configurations. Cost if wrong: pending-model and live PG constraints tests fail.

5. Task 1: Ruling: regenerate complete idempotent deploy/initial-core.sql, as existing persistence contract requires; do not weaken test or skip migration. Cost if wrong: script consumers must retain additive empty SSO schema, no seeds or account changes.

6. Task 2: Ruling: unknown-kid single-refresh and token jku/x5u behavior are verified by Task 6 protocol tests; Task 2 verifies bounded metadata, DNS pinning, public keys and refresh semantics. Metadata interface has no token input, so testing JWT behavior here would exercise an unused test-only API. Cost if wrong: final protocol test coverage must be audited explicitly.

7. Task 4 RED: 8 failed behavior assertions. First GREEN: 37/38; existing global unique email blocks display-only SSO email. Ruling: preserve local email uniqueness with a local-only partial index, permit explicitly separate SSO identities to share display email, add migration and preserve all existing rows/hashes. Cost if wrong: schema rollback to the legacy global index requires no duplicate display emails or the authorized cold backup, never silent data edits.

8. Task 8 RED 7 actual stub failures, GREEN console61/61 and TypeScript/Vite build. Ruling: pnpm11 default dependency verification attempted install against the borrowed dependency symlink. Use pnpm_config_verify_deps_before_run=false for immutable existing dependencies; never purge the linked directory. Task 9 RED 7 actual stub failures.

9. Task 10 RED: fixture valid configuration assertion fails (1 failed, 2 negative guard tests already pass on stubs); exact-origin real configuration test fails on missing override. Ruling: configuration flattens URI colons, so a Development + FixtureEnabled raw JSON adapter preserves exact dictionary keys. Production ignores this adapter; URL/Host/issuer and Task 2 network policy remain unchanged. Keycloak26.8.0 official multiarch digest resolved and pulled, immutable lock written.

10. Ruling: preserve private scratch and frozen-source after delivery because no commits are authorized; deleting the only uncommitted provenance would lose the review record. Cost if wrong: local storage remains until user-approved cleanup.

11. 受保护的秘密摘要增加一列，不将客户端秘密或裸摘要持久到业务库。按 ProviderID/auth_revision 隔离 DataProtection 用途；运行时每次核验。代价：所有控制实例须共享私有密钥环，无法解密时 SSO 拒绝；升级/灾备须携带该环。

12. 重复回调未成功 Claim 时只可失败 Pending，不能改变 Processing；合法请求拥有 Processing 后的协议失败仍终止本次尝试。重复拒绝保留脱敏审计。代价：重复回调与合法成功可以各有一条审计，但只有一次兑换和成功。

13. 命令 busy 所有权独立于详情 epoch；成功权限复查在命令执行期间不重新读取详情。权限失效立即清空，旧命令不能释放后续新命令。代价：命令运行期间详情复查顺延到其结果/后续读取。

14. 打包冻结工具并核对当前源码/工具字节与各证据来源；清理须对应最终验收 owner。ZIP 回读再次验证两份清单和证据身份。代价：任何封包前字节变化必须重做相应证据/扫描，不能复用过期 complete 布尔值。

15. 原 Minor 的别名/名称限制升级为必修项，因为合法配置无法编辑；UI 与 API 统一字母开头、点/横线/下划线别名和 128 字符限制。代价：既有前端错误接受的数字开头别名会提前被原生表单拦截，后端规则不放宽。

审查边界裁决：企业 IdP 与生产 TLS/PKI 没有实际验收；4192 冷备份、恢复和升级没有执行，需另次明确授权；IdP 全局登出、自动开户、SCIM 仍不在范围内。无延期的审查 Minor；所有初审必修项在作者一次修复记录列明 RED→GREEN 与完整回归。保留隔离工作树与私有审阅 scratch，不提交、不合并、不推送。
