# 第三批联调、恢复与4192交付 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将两个独立功能模块经过固定提交全回归、恢复验证和一次整分支审查后，合并并升级原4192，保留数据、账号、SSO和历史工具，交付可核对结果。

**Architecture:** 先完成[审批A1–A3](2026-10-08-cross-environment-approvals.md)，再完成[通知N1–N8](2026-10-08-external-notifications.md)，最后D1–D3统一验收与安装；独立模块可以分别测试和否决。使用不可变源码归档构建候选及仅用于恢复的兼容桥接镜像；恢复过程中保留升级后新增业务数据。

**Tech Stack:** 既有固定.NET10 SDK/运行时、PostgreSQL/Redis、Keycloak/Collector/Prometheus/Loki/Tempo镜像，既有Node24.19/React19.2/Vite6.4及固定MailKit4.17.0；Shell/Node运行工具、xUnit、真实CUA。

**Spec:** [已确认正式规格](../specs/2026-10-08-notification-cross-environment-approvals-design.md)，用户回复“确认”；本计划覆盖§9/§10交付及两模块的联合验收。设计提交`adc7dfd5476ae8ae21aa405efeb9248df3085937`，此前运行软件`c1c271555e3a125d22184697c17320c94a4db07c`仅为待重新核对的原安装基线，不把本批文档SHA当成运行软件。

状态：用户已确认实施计划；正在执行。沿用此前确认的Native：同一会话逐任务实施、精确本地提交，一次独立整分支审查；不重复要求选择执行方式。本批共14个任务，顺序A1–A3→N1–N8→D1–D3。

## Global Constraints

- 本批只补SMTP/Webhook及跨环境审批中心；原生产两级、每级1–5人、独立席位、候选冻结和双网关ACK流程保持。
- 现有渠道和旧规则默认关闭，无旧事件追溯发送；本机演示仅向已声明的独立本机收件服务发送。
- 主目录七项原修改、所有历史untracked/账号/SSO/密码/固定工具/业务文件及历史ZIP逐SHA保留，精确暂存，无push/reset/stash/clean。
- 每一套实际结果绑定不可变40字符软件提交和镜像sha256；安装、文档、桥接和运行软件身份分别记录，不能重贴材料身份。
- 所有生产网络发送在数据库事务外；秘密1–16KiB/私有卷，DP持久钥匙保留；完整cold backup回读校验，不以旧DB覆盖新增业务。
- 旧通知六字段解码器不接受扩展JSON，禁止未经验证直接降级；桥接仅为恢复，通知及规则管理期间暂时只读，不把它计为新功能交付。
- 原4192数据只做已批准维护/升级，不写QA申请/告警；实际基线表/文件数量实时盘点，不沿用此前52表/792文件为当前事实。

## Review Focus

1. 升级前服务有未完成发布/人工编辑：阻止维护切换或记录并等待安全状态，不把旧基线强套正在变化的业务；D3测试。
2. 桥接旧worker产生新告警或管理员编辑旧UI：缺省冻结关闭，通知/规则写入口只读，不丢新增通知定义；D1测试。
3. 原包工具字节固定、新维护工具尚未写齐就启动：从候选固定归档生成独立入口，原入口/manifest不改；D3测试。
4. 冷备只含数据库不含DP/通知secret/Keycloak，或finally重启遗漏通知fixture：完整卷清单/内部manifest/恢复就绪均验证；D1/D3测试。
5. 包含实际秘密或被重贴SHA的“成功”材料：内部身份、实际协议观察、浏览器动作及私有值扫描同时通过才可交付；D2/D3测试。

---

## 文件职责、执行记录及阶段关系

执行时先按using-git-worktrees复用`.worktrees/gateway-restart-readiness`，检查tracked clean及未跟踪冲突，再同步本批计划。需要桥接checkout时先用原生工具检查可复用附件，选用隔离的`c1c2715`基线工作区；不改主目录源码来生成桥接。

每任务按其5个步骤记录实际RED/GREEN命令、exit、零skip、提交及偏差；Native执行账本保存在ignored `.superpowers/sdd/<本计划标识>/`，结果归档到本批私有执行目录再清理重复scratch。所有 Ruling 和 cost if wrong，以及延期Minor保留于中文最终结果。不把接口类型未定义、fixture失败或sandbox EPERM当产品RED；本机监听和Docker按工具审批执行。

同一执行者顺序实施，A与N交付物独立；D前要求两模块验收通过且受影响锁文件已固定。每任务只提交其Files实际清单；共享DI/ModelSnapshot修改按实际依赖同步，不覆盖相邻任务已提交内容。用户已授权本地提交、合并或部署的流程持续有效；本计划审阅确认后才进入实现。

## Task 1 (D1)：数据保留的兼容桥接恢复路径

**Files:** Create `scripts/notification-approval/{bridge,recovery}.mjs`、`tests/notification-approval/recovery.test.mjs`、`docs/deployment/notification-approval-recovery-runbook.md`；桥接隔离checkout只Modify `src/WebApi.Infrastructure/Settings/{SettingsValues,SystemSettingsService}.cs`、`src/WebApi.Infrastructure/Alerts/AlertRuleService.cs`及所需DI/构造注入，Create `src/WebApi.Infrastructure/Notifications/CompatibilityNotificationMode.cs`。候选库不引用旧桥接源码；只以固定桥接提交/镜像receipt使用。

**Interfaces:** `buildNotificationBridge({baselineRevision,directory,owner}) -> Promise<BridgeReceipt>`，BridgeReceipt含基线/桥接40字符SHA、imageId、精确patch摘要及锁文件身份；`runDataPreservingRecovery({candidate,bridge,cloneDirectory,fixtureDirectory}) -> Promise<RecoveryReceipt>`。桥接 `CompatibilityNotificationMode.ReadOnly` 只在固定恢复模式启用；旧SettingsValueCodec接受精确原6或新9字段，验证三扩展字段的类型/枚举后投影原6读取；通知设置保存、全部规则更新/启停返回423 `notification_maintenance_mode`，避免旧UI写回覆盖扩展字段。其他原业务写入保留。

- [x] **Step 1：写门禁及桥接RED。** `bridgeRejectsUnknownJsonAndRuleWrites`、`recoveryRequiresExactBridgeAndCandidateIdentity`、`newBusinessRowsAndNotificationDefinitionsSurvive`；模拟错误桥接/归档污染/遗漏secret或DP manifest必须throw；真实旧镜像读取新JSON报错确认现有不兼容。Run `N --test tests/notification-approval/recovery.test.mjs`；桥接读写保护使用同SDK对应集成用例验证行为RED。
- [x] **Step 2：实现桥接并GREEN。** 原`c1c2715`隔离源码精确改动、独立提交，固定Gitarchive构建桥接。不移除新JSON键、不回写旧通知定义、不删除新表；桥接只读通知能力清晰标示。新增AlertEvent冻结列须有关闭默认，旧worker插入新行仍默认不发外部通知。
- [x] **Step 3：实际克隆恢复。** 候选运行→正常停止发送并等待当前尝试预算→桥接→候选。克隆里升级后创建的新业务标记、账号/附件、通知定义和投递历史精确保留；桥接可读取五组设置，通知/规则写423，原其他业务写入正常；旧worker新告警关闭冻结，候选恢复后不追溯发送。通知停启与临时运行状态变化写receipt，不变更平台持久开关替代原设置。
- [x] **Step 4：恢复验收。** 同门禁及实际RecoveryReceipt、全部coldbackup内部SHA回读、DP/SSO跨重启、SMTP/Webhook新任务在候选恢复后正常；没有真实证据则阻止D3升级。发布安装前结果明确桥接限于恢复且通知规则管理暂时只读。
- [x] **Step 5：提交。** 主执行分支只提交桥接/恢复驱动和脱敏结果，`test(delivery): verify data-preserving notification recovery bridge`；桥接自身源码提交单独记录，不能把它合并成候选功能实现。

## Task 2 (D2)：固定提交完整回归与唯一整分支审查

**Files:** Create `scripts/notification-approval/{acceptance,evidence,verify-suites}.mjs`、`tests/notification-approval/evidence.test.mjs`、`docs/deployment/notification-approval-pre-install-result.md`；Modify `console/src/pages/Coverage.tsx`、`console/src/coverage.json`、相应工作副本中文能力说明。公开材料至 `docs/evidence/notification-approval/`；私有值只进`.runtime/notification-approval-<uuid>/`。

**Interfaces:** `validateNotificationApprovalEvidence(proof,files,{revision,imageId,bridgeRevision,privateValues}) -> AcceptanceResult`；本批JS AcceptanceResult固定 `{passed:boolean,errors:string[]}`，ScenarioResult必须含内部身份、每项checks、protocol observations与截图/动作manifest；`verifySuiteLogs(logs,{revision}) -> SuiteReceipt`复用已存在日志解析函数，不复用旧Passed标签。

- [x] **Step 1：材料门禁RED。** 缺恢复/混合Scope/5xx实际次数/静默竞态、错源镜像/桥接SHA、伪共享密钥证明、UI截图无实际动作、公开秘密/邮箱、旧1629数字重贴、非UUID克隆等必须拒绝。Run `N --test tests/notification-approval/evidence.test.mjs`确认真实行为失败。
- [x] **Step 2：实现及GREEN。** 汇总N8/A3/D1内部证据，逐SHA/CRC/真实身份核对；更新Coverage仅声明已有实际证据的能力，企业IM/企业端点/生产验收仍边界明确。测试log归属当前源码，镜像inspect包括5运行应用及Console静态资源。
- [ ] **Step 3：完整实际执行。** 精确提交产品及driver后Gitarchive构建锁定7应用/Console和自有fixture镜像。Run `./scripts/check-contracts.sh domain`、`integration`、`gateway`、`./scripts/check-console.sh`及`N --test tests/runtime/*.test.mjs`五套完整回归，exit0/零fail/skip；本批新增Node测试另计，不能漏算。执行N8/A3/D1完整实际克隆+两级发布/双ACK、SSO、所有观测源、JWT/retry/cache已有能力回归及1440/1280CUA，计数来自本次日志。
- [ ] **Step 4：唯一整分支审查。** 用requesting-code-review按已确认Native做一次独立整个分支审查，覆盖全部A/N/D及桥接patch。所有Critical/Important集中一次修复批；每项写Ruling和cost if wrong，延期Minor逐项列出。变更后固定新SHA、重建受影响镜像/实际用例、重新取得必要完整日志，不改标旧证据，不追加逐任务二审。
- [ ] **Step 5：封存提交。** 实际门禁通过后`test(delivery): record verified notification and approval candidate`；pre-install只写候选/克隆验收，尚未安装原4192。保留历史成功与失败材料的身份与摘要。

## Task 3 (D3)：精确合并、完整冷备、原4192升级和交付

**Files:** Create `scripts/notification-approval/delivery.mjs`、`tests/notification-approval/delivery.test.mjs`、`docs/deployment/notification-approval-upgrade-result.md`、`docs/deployment/notification-approval-rulings.md`、本批公开 `installed-status.json/package-status.json/cleanup-status.json`；私有运行工具、备份、baseline/upgrade receipts进原 `.runtime/`，ZIP至 `deliverables/`。

**Interfaces:** `runNotificationApprovalDelivery({approvedRevision,bridge,baseline,verification,originalDirectory}) -> Promise<DeliveryReceipt>`按check/merge/backup/upgrade/final/package/cleanup阶段执行；`captureNotificationApprovalBaseline({originalDirectory}) -> Promise<ProtectedBaseline>`复用既有保护表投影及账号/文件SHA核对，但动态包含本批新增表、通知档案及所有secret卷，明确哪些自动时间/lease字段允许变动。

- [ ] **Step 1：写安全门禁RED。** 目标不是原owner、原仓库有非文档产品修改、后台发布未安全完成、计划软件不是验收软件、冷备缺卷/manifest、用户文件SHA改变、状态恢复只看HTTP200、兼容恢复未验证、旧维护入口被重写、ZIP含secret/原邮箱均拒绝。Run `N --test tests/notification-approval/delivery.test.mjs`。
- [ ] **Step 2：实现及GREEN。** 精确merge检查/备份/升级/恢复finally，兼容桥接只在已验证允许状态使用；原`manage.sh/manage-policies.sh`及原manifest字节不改，本批生成固定Git归档工具目录和`manage-notifications.sh`入口/manifest。核心runtime.json严格schema保持，新通知元数据使用N8同归属sidecar。新维护入口可独立status/start/stop/restart/up，纳入新增fixture生命周期/Secret/readiness，旧入口作为原功能/历史工具保留；丢失私有钥匙fail closed不重新生成覆盖。
- [ ] **Step 3：原只读基线、合并和coldbackup。** 核对M分支/HEAD/七项修改与全部untracked/业务/账号/SSO/旧工具/历史ZIP；精确快进或审阅合并候选提交，不push；SHA复核七项和原文件。安全窗口暂停已有授权服务做全冷备，升级前只盘点实际已存在的全部卷（PG、LKG双节点、dp-keys、三源data、cache-secret以及Keycloak数据/secret）；不存在的新通知卷不得伪造备份成功。逐内部SHA回读后finally恢复就绪。此阶段不在原4192造QA数据。
- [ ] **Step 4：升级与原实例验收。** 仅安装D2固定候选，自动渠道/原规则保持原默认关闭，真实本机演示测试只发向已声明fixture。确认软件/镜像/静态SHA、真实本地账号和Keycloak登录、只读403/注销401、两网关200/全部观测源、原业务desired/sequence、全部业务行与附件保护、通知长期管理入口和跨环境审批入口。升级后按实际卷清单补齐新通知secret/fixture状态和全部原卷的coldbackup，内部manifest逐SHA回读且finally恢复所有服务就绪；前后备份数量可不同，差异须来自明确新增卷。验证前后冷备及兼容恢复receipt；不可宣称此时重新完成企业端点验收。
- [ ] **Step 5：交付封存与清理。** 精确提交脱敏安装/能力/裁决结果；生成含源码/计划/公开证据/中文维护说明/逐文件摘要的ZIP，CRC和私有canary扫描通过，实际ZIP整包SHA记录。仅清理UUID自有测试资源；先完整归档执行账本/日志/失败尝试并逐SHA回读，再删除重复scratch；保留原worktree/历史资料、所有coldbackup和恢复/候选镜像。最终报告区分本地提交、主仓库合并和原4192实际安装，列全部Ruling/cost if wrong与延期Minor。

## 计划自查与用户审阅

已将独立子系统拆为两份功能计划，顺序和共同恢复/验收在本文明确；每项有接口、实际行为RED/GREEN、Files和精确提交。规格§1/2/3→两功能计划及D2，§4–7→N1–N8，§8→A1–A3，§9→N8/D1/D3，§10→各任务及D2/D3。原权限、默认关闭、域名/秘密/TLS、重启协调、分页/风险投影、真实浏览器和恢复均有归属；本计划不把设计自查当测试完成。

用户审阅本文及两份功能计划并确认后，按保留的Native方式开始A1。若恢复方案不满足已确认规格，D1失败即阻止D3，不绕过门禁安装原4192。
