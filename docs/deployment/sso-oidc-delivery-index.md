# SSO/OIDC 本批交付索引

交付包：`deliverables/WebAPI_Enterprise_SSO_OIDC_20261005.zip`，旁置 `.sha256` 与 `.manifest.json`。本批只有一次独立初审；初审阻塞项由作者一次修复并验证，无第二次独立复核。最后封包校验见 `docs/evidence/sso/final-verification.json`；在该文件生成且 complete=true 前，本索引不表示封包已经通过。

| 内容 | 位置 |
|---|---|
| 实际构建源码与逐文件摘要 | ZIP内 `source/`、`docs/evidence/sso/source-manifest.json` |
| 冻结工具与逐文件摘要 | `scripts/`、`docs/evidence/sso/tool-manifest.json` |
| 已锁定依赖与镜像 | `source/**/packages.lock.json`、`source/console/pnpm-lock.yaml`、`source/deploy/*images.lock.json` |
| 中文操作说明与能力边界 | `docs/deployment/sso-oidc-runbook.md`、`sso-oidc-capability.md` |
| 真实Keycloak及回归 | `docs/evidence/sso/verification.json`、`regression.json` |
| 原生浏览器与逐图设计QA | `docs/evidence/sso/ui/qa.json`、`final-report.md`、截图与脱敏Cookie布尔证据 |
| 独立初审、作者一次修复、裁决及边界 | `docs/evidence/sso/independent-review.json`、`independent-review.md`、`execution-decisions.md` |
| 原环境保护、秘密扫描及清理 | `preservation-verification.json`、`privacy-scan.json`、`cleanup.json` |
| 原4192的另次升级提案 | `docs/deployment/sso-oidc-upgrade-proposal.md` |

本包用 `actual-uncommitted-build-source` 标识来源。基线 f4e2cc3 是开发起点；它不包含本批完成状态，也不能单独用于构建SSO。源码与工具都由字节SHA256固定，公开包不含运行数据卷、合成密码、客户端秘密、密钥、node_modules或私有上下文。

## 复验目录

包为审阅与复验交付，`source/`保存实际构建项目根。需要复跑时使用一个新的本地隔离候选目录，从本机原仓库克隆并检出基线，将包内`source/`内容覆盖到候选项目根，再将包内`scripts/`和`docs/`放到候选项目根相同路径。不得覆盖主仓库或长期运行目录。Git只用于基线信息，实际新增源码仍由manifest验证。

前端须按锁文件准备依赖；本次开发借用的本机依赖软链接未封入包。明确配置已有WEBAPI_NODE/WEBAPI_PNPM后，可按runbook执行check-sso.sh。fixture会在新候选目录创建自身owner及随机loopback端口，仍不得复用4192数据或密钥。网络/官方镜像与依赖准备完成后才可重新验收，不将准备阶段当作结果。

单独核验封包可执行 `python3 scripts/package-sso.py --verify <ZIP绝对路径>`，ZIP及旁置manifest/SHA须在同一目录。最终校验会验证 CRC、每文件 hash、源码和工具清单、各验收证据来源及包 hash。初审阶段截图保留在 ui/initial-review-phase 并明确标记旧源清单；最终受影响流程有新的原生浏览器补验，不将旧截图换标为新构建。

本轮原4192环境继续保留，升级需新的明确授权。企业IdP、生产HTTPS/PKI及生产灾备未验收。
