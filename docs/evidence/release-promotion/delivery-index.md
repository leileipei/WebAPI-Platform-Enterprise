# 发布与交付中心交付记录

2026-10-09 已完成环境访问地址A6任务及发布晋级B11任务，并安装至原[4192控制台](http://127.0.0.1:4192)。运行源码固定为 `a445534f82f11ae9a45c3b2904bd07108bd165a9`，镜像 `sha256:02a1c8782a00a04e6ad1c065560360efccffbee6db1a2039699c9cb8e3c14378`；后续文档封档提交不改变该运行身份。

已接入环境地址/前缀、不可变制品、人工报告与独立测试验收、目标资源/凭证映射、环境差异和预检、两级生产审批、Gateway执行与ACK、独立生产业务验证、快照/历史及人工审批回滚。保留旧入口，原项目默认Legacy；显式启用PromotionRequired后正常生产发布必须经过晋级。

|证据层级|最终实际结果|
|---|---|
|固定源码完整回归|Domain753 / Integration971 / Gateway142 / Console245 / runtime与delivery153 / 真实备份集成2：合计2266通过，零失败、零跳过；TypeScript与控制台构建通过；专项6回归属于同套件，不重复计数|
|独立整分支审查|fresh reviewer：0 Critical、2 Important、0 Minor；两项在唯一修复批次完成真实RED→GREEN及全套回归；原审查未改写，没有二轮审查|
|真实隔离交付|TEST与PROD各2个实际Gateway、不同后端和入口，实际目标TLS200、来源凭证目标401；独立验收、2名审批者、发布者、独立验证者走完流程|
|故障与恢复|9类实际故障检查通过：部分ACK、真实超时、持久重启、失败重试、验证失败/人工回滚、共享应用影响、排队发布撤权、排队来源验收撤销及连接修订；实际受控报告冷备恢复下载摘要和4个恢复Gateway调用通过|
|浏览器与清理|隔离18张1440/1280截图逐张查看并核验摘要，原安装/恢复10张另列；本计划演练及测试资源清理0，不删除其他项目资源|
|原4192安装|固定包实际安装；原57表/891受保护行、802私有文件及353历史维护文件保全；原账号/SSO、双网关、4观测源复验；3个新权限仅默认给平台管理员|
|维护与备份|新增独立固定维护入口包含报告卷；最终冷备39文件/12卷归档，原IdP冷备另存；旧工具及私有秘密保留|

固定证据：[修复与完整回归](a445534f82f11ae9a45c3b2904bd07108bd165a9/review-fix-evidence.md)、[真实双环境闭环](a445534f82f11ae9a45c3b2904bd07108bd165a9/b11-runtime/README.md)、[原安装与恢复](a445534f82f11ae9a45c3b2904bd07108bd165a9/original-install/README.md)、[独立审查原文](final-review.md)、[完整执行记录](execution-ledger.md)、[67条执行裁定](decisions.md)、[运行手册](../../deployment/release-promotion-runbook.md)、[环境访问地址A阶段历史交付](../environment-access/delivery-index.md)。失败尝试按实际源码保留，不作为最终通过证据。

最终分层：`sourceVerified=true`、`isolatedAcceptance=true`、`localInstalled=true`、`productionAcceptance=false`。原业务项目保留Legacy；原安装只在独立合成项目保存TEST/PROD环境元数据及连接，不把原生产环境改作测试。完整晋级实际流程由单独四Gateway验收证明。

本批未实现任意Pipeline/Stage编排、CI/CD接入、紧急免审发布、自动指标验证、灰度或自动回滚。人工报告登记不证明报告内容真实，保存URL不自动开通域名；企业DNS/TLS/LB、真实上游、容量、HA及外部通知需在目标部署环境另行验收。

保留隔离分支 `feature/api-delivery-promotion`；原源码本地修改未变，未合并、未推送。
