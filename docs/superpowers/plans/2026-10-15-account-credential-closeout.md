# 第4包账号与凭证收尾 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. 已选择当前会话逐项实施；不派发子代理。

**Goal:** 按已批准规格完成首次改密、显式密码重置、可信 Credential 使用统计、轮换和应用页面，并逐项核对原40页需求。

**Architecture:** 保留 Cookie/SSO、资源治理和不可变发布引擎。账号安全、独立统计协议、页面闭环分别形成可测试子计划；使用统计从 Gateway 异步上报，不参与认证决策，不修改业务快照。最终只在隔离环境验收。

**Tech Stack:** .NET SDK 10.0.401、现有 ASP.NET Core/EF Core/Npgsql、PostgreSQL、Redis、React/TypeScript/Vite、Node 内置测试器；不引入新外部服务或依赖包。

**Spec:** [已批准书面规格](../specs/2026-10-15-account-credential-closeout-design.md)，SHA256 `b3b0c33dd71a6fb83b28c6efe99f74a592d580995d34618f612bbc4f43413994`。用户于2026-10-15批准编写计划；本计划尚待审阅。

## Global Constraints

- 当前仅获准编写计划。本计划获批后才可实现和测试；固定 Git 快照须提供精确审阅包另行授权。提交、推送、main 合并、4192切换均不由计划批准自动授权。
- 第2包暂停；不运行其性能矩阵、不改变其 owner。原4192、账号、数据、密码、Keycloak、Gateway、已有脏文件保持；测试用新独立 owner 和动态端口。
- 规划基线 main `a30ce5bf8291539eac2640ea02839b403ccd68c8`，产品源码与 `226dfe450865cd322d81eb5747bec791a71f7519` 一致。实施前重新核对差异并建立隔离工作区，禁止覆盖本地修改。
- 现有本地账号迁移 `must_change_password=false`；仅新建本地账号/全新安装管理员为true；SSO不设本地密码。
- 改密会话非持久、不滑动，TTL=min(系统TTL,10分钟)；只允许 csrf/me/logout/change-password；服务与集合/导出路径同样阻断。
- 统计只计 ApiKey认证且API授权通过的请求；缓存命中/上游失败仍计一次，内部重试不重复；JWT/匿名/鉴权失败不计。
- LastUsed单调递增；统计不修改 Credential/Application业务Revision、UpdatedAt、发布候选、Snapshot字节及Snapshot2.0–2.3协议。
- 统计协议单独1.0；旧Register/Heartbeat报文字段和行为不变。只能使用已发表不可变快照证明身份及权限。
- 采集15秒、checkpoint1000ms、10000内存键、64MiB待发、1000条/256KiB批次、24小时补报；允许范围见B子计划，不能擅改为静默丢弃。
- UTC整点窗口；默认过去24个完整小时；当前小时单列进行中。30天保留(1–90天)，查询≤30天且≤实际保留；批次Hash25小时(2–168小时且≥补报期+1小时)。
- Available/NoData必须有完整覆盖证明；Partial显示下界和缺口；Unknown/Unavailable计数null。不得用“从未使用”描述未知历史。
- 不增加正文映射或API六步向导整合；第3包已交付策略回归须保持，原先延期的向导第五步仍延期。
- 不自动提交、自动发布或代替消费方确认迁移；每任务末尾仅产出限定差异、验证日志和待提交审阅记录。

## Review Focus

1. 浏览器旧Cookie、旧Stamp与并发改密/重置竞争：A2/A3/A4测试必须证明仅新身份生效、审计失败回滚、旧请求不能复活。
2. 网关崩溃、ACK丢失、checkpoint回退与换实例补报：B3/B5/B6覆盖去重、隔离及明确缺口，不能承诺全量exactly-once。
3. 旧工作凭证已撤销，但已发布快照仍允许调用：B3/B4按请求冻结版本验证，C2/C3区分工作配置与已发布环境。
4. UTC小时边界、未来时钟、BIGINT/JavaScript精度和权限收窄：B2/B3/B7用边界值及跨环境主体验证不溢出、不泄漏。
5. 双Key已创建但未发布、最后有效窗口在确认前变化：C1/C3必须拒绝过期影响确认，不能把待发布Key当线上替代。

---

## 1. 子计划与执行顺序

| 子计划 | 任务 | 可独立审阅的结果 | 依赖 |
|---|---|---|---|
| [A：账号安全](2026-10-15-account-credential-closeout-a-accounts.md) | A1–A5 | 新旧账号迁移、受限会话、改密/重置API与页面 | 现有账号/审计/Redis基础 |
| [B：凭证统计](2026-10-15-account-credential-closeout-b-usage.md) | B1–B7 | 协议、数据库、采集/补报、权限查询 | B1先定契约；B2→B3；B4→B5→B6；B7依赖B2/B3 |
| [C：页面与验收](2026-10-15-account-credential-closeout-c-delivery.md) | C1–C5 | 风险预览、应用/轮换UI、454项台账、固定验收包 | C1独立；C2依赖B7；C3依赖C1/C2；C5汇总A/B/C |

建议顺序 A1→A2→A3→A4→A5→B1→B2→B3→B4→B5→B6→B7→C1→C2→C3→C4→C5。A/B可分别失败而不被对方成功掩盖。C5固定构建前有单独授权门禁。

## 2. 文件所有权与接口约定

所有相对路径以 `enterprise/` 为根。每子计划列出准确文件及职责；现有大文件只接入对应功能，不顺带重构。

- A拥有 `Security/PasswordLifecycleService`、`PasswordLifecycleGuard`、账号迁移、Identity新增字段及改密/重置UI；B/C使用该身份契约，不重复实现鉴权。
- B拥有 `Contracts/CredentialUsage` 线协议/查询DTO、`Domain/CredentialUsage`纯规则、Infrastructure统计存储、Gateway异步采集；C只消费查询DTO。
- C拥有应用汇总、风险预览/确认契约、应用页面与台账；已有发布/审批/ACK仍是唯一发布实现。
- 通用类型沿用 `ActorContext(Guid UserId,string TraceId)`、`IdentityDto`、`CommandResult<T>`、`PageResult<T>`、`NodeIdentity` 和 `CancellationToken`。不得往ActorContext塞入未验证的客户端强制改密标记。
- 新增DTO的名称、签名和字段在各自产生任务定义；后续任务引用前任务，不另起相似DTO。测试辅助方法与夹具在对应任务文件内定义，不进入生产接口。

## 3. 通用执行与验证方法

- [ ] 执行前读取规格和全部三个子计划，使用 `superpowers:using-git-worktrees` 选择/建立隔离工作区；记录main、工作树、4192控制清单、原有文件hash/status和owner保护清单。已有敏感文件只留私有摘要，不导出内容。
- [ ] 每个实现任务执行命名测试的RED→最小实现→GREEN，再运行所属完整套件；保存最初失败和最终结果，不能以单测成功替代固定版本验收。
- [ ] 每任务结束只准备精确文件差异与验证日志；此处替代skill示例的自动commit步骤，以符合本轮授权。发现新增设计分歧先记录具体影响，已批准范围内的常规实现选择自行完成。

子计划代码块为对应命名测试的关键断言，不是可单独执行的完整测试文件；Arrange使用任务列出的真实数据库/服务/文件夹具，测试辅助变量只在该测试定义。失败预期必须来自缺少行为或断言不符，不能以语法错误、环境未配置或fixture未启动作为RED证据。

在隔离仓库根运行后端命令：`bash scripts/check-contracts.sh integration --filter FullyQualifiedName~PasswordLifecycleTests`；domain/gateway对应替换首参数，筛选名见各任务。该脚本生成独立owner并检查清理所有权；保留数据库故障和失败日志。前端：`bash scripts/check-contracts.sh console`。Node新增夹具：`node --test tests/account-credential/*.test.mjs`。运行时使用项目已固定Node和SDK路径，不升级锁文件。

## 4. 验收映射

| 规格验收 | 实现与测试任务 | 固定门禁 |
|---|---|---|
| P4-01 新旧账号/SSO | A1/A2/A5 | C5空库、旧库迁移、SSO |
| P4-02 受限会话/并发/故障 | A2/A3/A5 | C5真实Redis、HTTP、浏览器 |
| P4-03 重置/幂等/秘密 | A4/A5 | C5旧会话和实际秘密扫描 |
| P4-04 计数语义 | B4/B6 | C5双Gateway实际转发 |
| P4-05 去重/伪造/实例 | B1/B3/B6 | C5真实PostgreSQL事务及多实例 |
| P4-06 崩溃/补报/恢复 | B5/B6 | C5私有持久卷和故障注入 |
| P4-07 边界/保留/权限 | B2/B3/B7 | C5查询、SQL数及字节保护 |
| P4-08 轮换/发布/ACK | C1/C3 | C5审批链及两次独立发布 |
| P4-09 应用/脱敏/1440px | A5/C2/C3 | C5实际页面交互截图 |
| P4-10 454单元与保护 | C4/C5 | 台账机器校验及原环境对比 |

## 5. 执行边界和交付

本计划批准后允许A1–C4实现、必要的隔离测试及C5审阅包准备。C5固定源码快照需要用户另行授权具体文件；获授权后继续本计划的固定验收。未取得授权不能将工作区诊断称为固定交付，也不能自行提交来使构建可运行。

最终交付必须同时列明已实现、固定验收通过、仍待验证/经确认延期，以及失败保留。不得自动把第2包、第5包或真实生产SSO/DNS/TLS/HA/容量工作计为关闭。
