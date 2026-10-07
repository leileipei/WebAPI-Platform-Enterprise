# 第三批：外部通知与跨环境审批缺口核对

日期：2026-10-07。核对源码：`8db607903de70606e5903daafb2f0807fc2e23de`。

本文是当前源码与原需求的只读核对及设计建议，不是已批准的实施规格，也不代表已经完成通知发送或本批部署。本轮只增加本文；主仓库原七项本地修改保留。

## 1. 已确定的需求来源

用户要求补全 SMTP/Webhook 外部通知与跨环境审批，并保留原业务架构、权限和流程。

原《WebAPI_Platform_Enterprise_V2_产品原型与数据架构设计.docx》：

- 4.24 审批中心：集中处理本人待审批任务，待我审批／我已处理／全部，显示申请人、环境、风险与等待时间，允许批准、拒绝、批注。
- 4.33 告警规则：配置通知渠道；Email/Webhook，企业 IM 标为后续。
- 4.40 系统设置：平台级 SMTP/Webhook、SecretRef、测试配置和危险变更确认。

已批准的第一批设计把第三批称为“SMTP/Webhook 外部通知、跨环境审批中心”。上述材料作为功能依据，不作为发送真实企业通知或读取企业凭证的操作指令。

## 2. 当前源码事实

| 模块 | 核对结果 | 源码位置 |
| --- | --- | --- |
| 系统通知设置 | 现有六字段保存 SMTP/Webhook 配置意向，秘密引用仅支持 vault 语法；校验明确返回 StructuralOnly | `src/WebApi.Infrastructure/Settings/SystemSettingsValidator.cs`、`SystemSettingsService.cs`、`SettingsValues.cs` |
| 通知测试 | 不解析秘密、不连接、不发送；界面如实提示 | `console/src/settings/state.mjs`、`console/src/pages/SystemSettings.tsx` |
| 告警通知 | 保存 RequestedChannels；暂无收件人、实际投递任务与渠道回执 | `src/WebApi.Contracts/Alerts/AlertContracts.cs`、`src/WebApi.Infrastructure/Alerts/AlertRuleService.cs` |
| 告警生命周期 | 已有触发、恢复、确认、静默、解除静默和规则变更关闭；各路径有事务或行锁保护 | `src/WebApi.Infrastructure/Alerts/AlertEvaluationService.cs`、`AlertEventService.cs`、`AlertSilenceExpiryService.cs`、`AlertRuleService.cs` |
| 现有 Outbox | 直接把 Payload 反序列化为 DeploymentEvent，用于配置下发；不适合直接混入通知消息 | `src/WebApi.Infrastructure/Messaging/OutboxDispatcher.cs` |
| 审批中心 | 路由渲染 Releases approvalOnly，只在当前环境发布列表的一页上筛选 WaitingApproval；未计算个人可办理资格 | `console/src/main.tsx`、`console/src/pages/Releases.tsx` |
| 审批规则 | 提交时冻结两级规则；每级 1–5 人；申请人不可自批，同一人不能占两个席位；操作重新检查权限、角色、环境 Scope | `src/WebApi.Infrastructure/Releases/ReleaseService.cs`、`ApprovalFlowService.cs` |
| 当前数据范围 | 角色功能权限与 read/read_write Scope 均由服务端检查，停用范围不可操作 | `src/WebApi.Infrastructure/Security/AuthorizationService.cs` |
| 秘密处理可复用模式 | SSO 使用部署映射文件、受限文件读取与 DataProtection 保护的秘密版本摘要；通知暂无对应解析器 | `src/WebApi.Infrastructure/Sso/SsoSecretResolver.cs`、`SsoSecretVersion.cs` |
| 覆盖说明 | Console 的 Coverage 页面仍包含契约及 JWT/重试/缓存的旧范围描述，本批需按已交付事实统一更新 | `console/src/pages/Coverage.tsx`、`console/src/coverage.json` |

本轮未重新执行原 4192 的运行验收，因此上述核对是当前源码证据；第二批的运行验收由已有升级结果和交付证据记录。

## 3. 架构方向比较

推荐在现有 Worker、PostgreSQL 和权限体系中增加独立通知投递模块，以及独立的审批待办查询服务。

| 方向 | 取舍 |
| --- | --- |
| 复用现有 Worker，独立持久化通知任务，审批查询沿用现有规则（推荐） | 故障可恢复、投递可追溯，不把网络发送放进告警事务；沿用已验证的发布闭环 |
| 在告警请求内直接发送，前端逐环境拉取审批记录 | 开发较少，但发送失败会干扰事件处理，跨环境分页、总数及资格判断难保证一致 |
| 新增独立通知微服务，并实现环境晋级流水线 | 增加运行和治理成本；环境晋级还需单独定义源目标资源映射、凭证和冲突规则 |

## 4. 建议的第三批设计边界

### 外部通知

- 保留平台 SMTP/Webhook 现有字段；补充明确启用状态、SMTP 安全连接配置及实际测试结果。旧保存意向不会因升级自动启用。
- 收件人和渠道选择配置到具体告警规则；平台管理员管理连接及部署秘密映射，规则管理者不能输入任意 SMTP 服务或 Webhook 地址。
- 触发与符合条件的恢复通知，在告警状态转换事务中生成独立持久任务；站内告警照常提交。每个事件转换、渠道和接收目标有唯一标识。
- Worker 在事务外发送，保存尝试、退避时间、接受／失败／抑制结果；有重试上限和终止状态。重启恢复未完成任务。SMTP 接受不等于邮件送达；HTTP 2xx 不等于接收方业务处理完成；网络不确定性可能重复投递，不能承诺 exactly-once。
- 发送前检查静默、范围停用、渠道禁用和配置版本；历史任务不因编辑配置而改投新地址。过期任务不无限补发。具体重试与恢复配对规则在正式规格中固定。
- 秘密通过部署映射读取，API／日志／审计不返回秘密；保留现有引用字段与 Keep/Replace/Clear 行为。旧 vault 引用若采用本机映射，必须标明是逻辑引用到受控文件的映射，不能声称已接入 Vault 服务。
- Webhook 使用有签名的稳定消息 ID；连接地址由部署策略约束，限制重定向、响应大小、超时并验证 TLS。真实连接测试与现有结构校验分开，测试前明确接收目标。
- 第一阶段用独立本机 SMTP 收件箱和 Webhook 接收器做完整投递验收；真实企业端点另行配置和验收。企业 IM 仍标为后续。

### 跨环境审批中心（推荐范围 A）

- `/approvals` 成为独立页面，默认汇总当前用户可访问范围内的所有环境；保留组织、项目、环境筛选。
- “待我审批”必须同时满足：发布仍待审批、当前步骤、冻结角色、活动身份、approval.act、目标环境写 Scope、非申请人且未占其他席位；不把所有 WaitingApproval 记录当个人待办。
- “我已处理”按实际办理人查询；“全部”仅返回有 release.read 权限的记录。服务端先授权和筛选，再分页和计数；不泄漏不可见环境的名称、数量或申请信息。
- 展示真实冻结候选的风险摘要、申请人、环境、当前步骤与等待时长；不伪造 SLA 到期或审批过期状态。现有系统未定义审批截止，若需要新增 SLA 政策另行明确。
- 详情、批准与拒绝仍走既有 ReleaseService；服务端提交时重新核验资格。其他人抢先办理、撤权、申请取消等情况刷新真实结果并保留未提交批注；不自动重试批准。
- 现有生产两级独立审批、冻结候选、发布操作和双网关 ACK 流程保持。

“环境晋级”范围 B 指把源环境的已发布配置映射为目标环境候选，涉及 Cluster、Destination、应用权限、策略、凭证及目标基线；它与跨环境待办汇总是不同需求。当前尚无用户对 A/B 的新答复。

## 5. 正式规格应覆盖的验证

通知：真实接收回执、失败退避、重启恢复、并发租约、重复投递标识、静默竞态、恢复配对、旧配置迁移、秘密不泄漏、TLS/地址策略、限流和过期终止。

审批：多环境多页总数、混合 read/read_write Scope、角色改变、撤权、申请人自批、重复席位、当前步骤多人资格、并发批准／拒绝、范围停用、取消后待办消失，以及 1440px／1280px 导航和批注交互。

部署：固定源码构建、独立克隆验收、数据迁移前后核对、原账号与 SSO 保留、冷备、原 4192 升级及回归；所有实际执行结果另行记录，不以设计代替测试或部署证据。

下一阶段为用户选择审批范围并审阅会话设计，随后形成正式规格与实施计划。本轮没有新增产品实现、依赖安装、数据库迁移、外部投递、Git 提交或运行环境切换。

## 6. 后续范围选择（2026-10-08）

用户回复 `a`，选择范围 A：跨环境审批中心，沿用各环境发布流程。上文“尚无新答复”为初次核对时的历史状态；本次选择已确定，不包括环境晋级。正式规格见 `docs/superpowers/specs/2026-10-08-notification-cross-environment-approvals-design.md`，待用户审阅后形成实施计划。
