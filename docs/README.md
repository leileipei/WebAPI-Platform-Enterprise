# 项目资料总目录

先从[当前源码与验证状态](deployment/current-project-status.md)确认版本，再按用途阅读以下资料。GitHub 固定提交、当前工作目录、安装中的软件和验证证据分别记录；不按文件名里的“当前”或不同工具的完成说明判断交付状态。

## 功能与源码

| 主题 | 资料与源码入口 |
| --- | --- |
| 页面与需求 | [40 页能力映射](console-coverage.md)、[需求台账](evidence/work-package4/requirements-status.md)、[控制台源码](../console/src/) |
| 组织、权限与会话 | [会话安全](session-security.md)、[治理安全](governance-security.md)、[授权实现](../src/WebApi.Infrastructure/Security/AuthorizationService.cs) |
| API、路由与契约 | [目录与路由](catalog-routing.md)、[OpenAPI 与凭证](openapi-credentials.md)、[契约管理手册](deployment/contract-management-completion-runbook.md) |
| 应用与凭证 | [账号与凭证操作](deployment/account-credential-operations.md)、[安全操作及关闭边界](deployment/credential-security-operations.md)、[应用服务](../src/WebApi.Infrastructure/Applications/ApplicationService.cs) |
| 审批、发布与回滚 | [发布审批](releases-approval.md)、[可靠发布契约](contracts/durable-publish.md)、[发布协调器](../src/WebApi.Infrastructure/Releases/PublishCoordinator.cs) |
| 跨环境交付 | [晋级手册](deployment/release-promotion-runbook.md)、[流水线证据入口](evidence/release-pipeline/delivery-index.md)、[交付源码](../src/WebApi.Infrastructure/Delivery/) |
| 网关策略 | [策略数据字典](traffic-policy-data-dictionary.md)、[网络策略手册](deployment/network-policies-runbook.md)、[网关实现](../src/WebApi.Gateway/) |
| 观测与外部通知 | [观测数据字典](observability-data-dictionary.md)、[观测手册](deployment/observability-runbook.md)、[通知恢复手册](deployment/notification-approval-recovery-runbook.md) |
| 数据与运行协议 | [核心数据库](database-core.md)、[凭证安全数据字典](credential-security-data-dictionary.md)、[运行快照](contracts/runtime-snapshot.md)、[网关代际](contracts/gateway-generation.md) |

## 构建、运维与验证

- [工程入口与验证命令](../README.md)、[基础配置与运维](operations.md)。依赖版本以代码中的版本文件及锁文件为准。
- [运维与交付目录](deployment/README.md)：操作手册、安装结果、历史交付和方案分别查阅。
- [前端 CI 配置与本地验证](deployment/console-ci-phase1.md)、[后端 CI 配置与本地验证](deployment/contracts-ci-phase2.md)、[主干保护提案](deployment/github-quality-gates-proposal.md)。远端实际状态见当前状态页，不能用本地结果替代 Actions 结果。
- [验收口径](acceptance.md)、[账号凭证关闭口径](acceptance/account-credential-closeout.md)。固定版本的测试数不能相加为当前版本的全量通过数。

## 历史资料怎么读

| 资料 | 用途 | 阅读限制 |
| --- | --- | --- |
| [2026-10-16 集成记录](deployment/full-project-integration-20261016.md) | 当次代码集成、验证与部署边界 | 不代表今天的安装状态 |
| [历史本机状态](deployment/current-local-status.md)、[早期交付索引](deployment/current-local-delivery-index.md) | 安装当时的入口、版本和回执 | 文件名中的“当前”是当时的当前 |
| [2026-11-10 网络策略交付](evidence/network-policies/2026-11-10-c946bbb/delivery-index.md) | 固定 `c946bbb` 的验收及工件清单 | 不是本地后续修复的全量验收 |
| [架构评审](../architecture-review.md) | 2026-11-07 基线的评审和后续补记 | 功能状态冲突时回到固定代码核实 |
| [原始证据目录](evidence/) | 日志、截图、回执、初次失败和最终结果 | 保持原文件、原版本及原摘要 |
| [历史归档](archive/)、[设计和计划](superpowers/)、[早期规划](planning/) | 原始设计决策与阶段记录 | 不按计划中的完成勾选推断实现或验收 |

契约管理第一批结果统一以[证据原文](evidence/contract-management-completion/result.md)为正文；原部署路径保留跳转说明。其他相同内容的证据文件仍保留各自身份，尤其不能删除备份前后两份相同摘要的证明。

## 资料维护规则

1. 每项能力优先引用具体源码、提交和对应证据，不以工具名称或一次聊天总结作为实现依据。
2. 当前事实只在[当前状态页](deployment/current-project-status.md)集中维护；历史报告保留日期、原结果、失败和 `Unknown`，不改标成新版本。
3. 安装说明同时记录源码、镜像、维护工具及核验时间。没有现场记录时不写“当前健康”或“已升级”。
4. 新文档放入已有主题；同一报告使用一个正文入口。改名或归档前检查引用与工件摘要，优先保留稳定路径。
5. 本地未提交资料、私有运行文件和缺失交付包单独记录。缺失文件标记不可用，不创建空占位包，不上传运行秘密。

[机器可读资料目录](documentation-index.json)记录分类、重复文件的正文归属以及现存历史失效引用。它是检索清单，不是能力验收或生产完成率。
