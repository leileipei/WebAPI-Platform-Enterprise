# 运维与交付资料目录

先核对[当前源码与验证状态](current-project-status.md)，再选择操作手册。安装结果、交付索引和审查记录均按各自固定版本解释；页内“当前”“最新”不表示今天的运行状态。

## 开始操作前

- 版本与运行边界：[项目总目录](../README.md)、[基础运维](../operations.md)、[持久环境运行手册](local-runtime-runbook.md)。
- 凭证普通轮换、紧急安全链路是否启用：[账号与凭证操作](account-credential-operations.md)、[凭证安全操作](credential-security-operations.md)。
- 对既有服务执行维护前，核对实际安装记录中的源码、镜像、维护工具和私有恢复资料。不能直接把新工作目录里的脚本覆盖到旧安装。
- 当前仓库的恢复入口需要显式预算和真实硬配额验证，默认集成仍缺失；不能直接执行旧恢复示例，详见[阻断条件](local-runtime-runbook.md#恢复目前的阻断条件)。
- 下列旧 ZIP 的下载文件不在仓库中；原包名及摘要是历史身份，不代表文件已经取回。

## 全部部署资料

本目录纳入以下 64 份已有部署文档。操作手册提供流程参考，不单独证明功能已在持久环境部署。

### 当前状态和自动检查

只有统一状态页记录本轮实际版本；CI 两批文档保留各自本地验证输入，远端运行结果另核对。

- [前端自动检查第一批：配置与本地验证](console-ci-phase1.md)
- [后端自动检查第二批：配置与验证](contracts-ci-phase2.md)
- [当前源码与验证状态](current-project-status.md)
- [GitHub 自动检查与主干保护提案](github-quality-gates-proposal.md)

### 操作手册

- [账号生命周期与 Credential 使用统计操作说明](account-credential-operations.md)
- [契约管理补全本机升级操作约定](contract-management-completion-runbook.md)
- [凭证安全操作手册（G 系列 G01–G13）](credential-security-operations.md)
- [环境访问地址运行手册](environment-access-runbook.md)
- [JWT／重试／缓存运行维护](gateway-jwt-retry-cache-runbook.md)
- [4192 升级后维护入口](gateway-readiness-upgrade-runbook.md)
- [网关重启运行确认说明](gateway-restart-readiness-runbook.md)
- [本机演示联调使用说明](local-demo-guide.md)
- [本机演示恢复资料核对清单](local-demo-recovery-checklist.md)
- [本机 Keycloak 持久演示运维](local-keycloak-demo.md)
- [本机运行工具与固定安装维护](local-runtime-runbook.md)
- [CORS、IP 与 JSON Transform 独立验收操作说明](network-policies-runbook.md)
- [通知与审批的数据保留恢复桥接](notification-approval-recovery-runbook.md)
- [本机隔离观测栈](observability-local.md)
- [真实观测与告警运行手册](observability-runbook.md)
- [跨环境 API 发布晋级运行手册](release-promotion-runbook.md)
- [工作包 1：登录保护与 Header 信任边界运行手册](security-hardening-runbook.md)
- [企业单点登录 OIDC 操作与部署说明](sso-oidc-runbook.md)
- [流量策略运行手册](traffic-policies-runbook.md)

### 历史安装与交付记录

- [契约管理第一批部署结果旧入口](contract-management-completion-upgrade-result.md)
- [WebAPI Enterprise 历史本机交付索引 2026-10-05](current-local-delivery-index.md)
- [当前本机运行与能力范围](current-local-status.md)
- [SSO 升级后的历史本机交付索引](current-sso-delivery-index.md)
- [系统设置升级后的历史本机交付索引](current-system-settings-delivery-index.md)
- [外部通知 N8 本机验收结果](external-notifications-local-result.md)
- [全项目集成与发布记录（2026-10-16）](full-project-integration-20261016.md)
- [JWT、重试与缓存：固定版本安装前验收](gateway-jwt-retry-cache-pre-install-result.md)
- [JWT、重试与缓存：4192 升级验收](gateway-jwt-retry-cache-upgrade-result.md)
- [WebAPI Enterprise 网关重启就绪：本机升级结果](gateway-readiness-upgrade-result.md)
- [网关重启就绪修复结果（升级前）](gateway-restart-readiness-result.md)
- [G 系列(凭证治理)维护窗口演练:全量 PG 备份保住安全账本哈希链](governance-g13-upgrade-rehearsal.md)
- [G14 完整协调式故障矩阵(维护窗口协调循环容错验证)](governance-g14-fault-matrix.md)
- [历史本机交付索引 2026-10-05](local-delivery-index.md)
- [WebAPI 本机持久 Keycloak SSO 接入结果](local-keycloak-demo-result.md)
- [4192 网络策略部署结果](network-policies-4192-installed-226dfe4.md)
- [4192 网络策略升级演练结果](network-policies-4192-upgrade-rehearsal.md)
- [通知与跨环境审批：安装前联合验收](notification-approval-pre-install-result.md)
- [通知与跨环境审批：原4192升级结果](notification-approval-upgrade-result.md)
- [4192 升级后观测复验](post-upgrade-observation-result.md)
- [SSO/OIDC 本批能力边界](sso-oidc-capability.md)
- [SSO/OIDC 本批交付索引](sso-oidc-delivery-index.md)
- [SSO/OIDC：4192 本机环境升级结果](sso-oidc-upgrade-result.md)
- [系统设置交付索引](system-settings-delivery-index.md)
- [系统设置：4192长期环境升级结果](system-settings-upgrade-result.md)
- [平台系统设置验收记录](system-settings-validation-result.md)
- [流量策略：4192 本机长期环境升级结果](traffic-policies-upgrade-result.md)

### 历史方案与审查

- [固定提交验收清单](gateway-jwt-retry-cache-qa-checklist.md)
- [本轮实施裁决与代价](gateway-jwt-retry-cache-rulings.md)
- [网关重启就绪修复接入4192：升级审阅方案](gateway-restart-readiness-upgrade-review.md)
- [本机持久 Keycloak SSO 演示接入方案](local-keycloak-demo-proposal.md)
- [4192 网络策略升级审阅包](network-policies-4192-upgrade-review.md)
- [本批能力与验收边界](notification-approval-capability-coverage.md)
- [第三批通知验收检查点（尚未交付）](notification-approval-notification-checkpoint.md)
- [通知与跨环境审批交付裁决](notification-approval-rulings.md)
- [通知与跨环境审批整分支审查及集中修复](notification-approval-whole-branch-review.md)
- [4192 工作包 1 升级准备](security-hardening-4192-upgrade-preparation.md)
- [4192 SSO/OIDC 升级提案（待另次授权）](sso-oidc-upgrade-proposal.md)
- [系统设置接入4192：升级审阅方案](system-settings-upgrade-review.md)
- [流量策略接入 4192：升级审阅方案](traffic-policies-upgrade-review.md)
- [版本比较与风险评审：主目录合并及4192升级审阅方案](version-comparison-upgrade-review.md)

## 缺失文件与历史引用

三个 2026-10-05 ZIP 见[早期策略交付索引](current-local-delivery-index.md)；另一个 2026-10-08 ZIP 见[通知与审批安装结果](notification-approval-upgrade-result.md)。这些文件当前不可下载，不能用新空包替代。

原始证据和归档中的其他失效引用保留原样，清单见[资料索引](../documentation-index.json)。同名“当前索引”仅作阶段历史入口；今天的结论统一回到[状态页](current-project-status.md)。
