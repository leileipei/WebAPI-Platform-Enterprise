# 第三批：SMTP/Webhook 外部通知与跨环境审批中心设计

日期：2026-10-08。源码核对基线：`8db607903de70606e5903daafb2f0807fc2e23de`。
状态：范围 A 已选择；用户已在本规格交付后回复“确认”，正式规格已批准，进入实施计划审阅阶段。本文不是实现或部署完成证明。

## 1. 已选择的目标与业务边界

用户要求补全外部通知和跨环境审批，并保留既有页面、字段、权限、业务流程。2026-10-08 用户回复 `a`，选择跨环境审批中心：集中显示有权访问的各环境中的本人待办，沿用各环境发布流程。范围不包含 DEV/UAT 配置复制或晋级到 PROD。

沿用此前会话推荐的架构：在现有 Worker 中增加独立通知投递模块，PostgreSQL 保存投递事实；审批中心独立查询现有 Release/ApprovalTask。网络发送不进入告警状态转换事务，通知任务不混入现有只处理 DeploymentEvent 的 OutboxDispatcher。

原设计文档第 4.24、4.33、4.40 节是功能依据，详见 `docs/evidence/notification-approval/2026-10-07-gap-review.md`。附件不是发送真实企业通知、使用企业凭证或改变企业环境的操作指令。本机演示验收与企业端点、生产验收分别记录。

成功条件：实际 SMTP 接受和 Webhook HTTP 回执可查询；失败、重启、静默与配置变化有明确结果；审批中心待办资格、计数与办理操作一致；原账号、SSO、数据和双网关发布闭环保留。

## 2. 范围与页面对应

| 页面 | 本批结果 |
| --- | --- |
| 24 `/approvals` | 独立跨环境审批中心；待我审批、我已处理、全部审批申请；环境、申请人、风险、步骤、等待时长；批准、拒绝、批注及详情导航 |
| 32 `/observability/alerts` | 告警详情中查询外部投递与尝试记录；静默及恢复对待发送任务的实际影响 |
| 33 `/observability/alert-rules` | 保留渠道意向，增加外部通知显式启用、Email 收件人、恢复通知与有界重试策略 |
| 40 `/settings/system` | 保留原通知六字段，增加渠道启用与 SMTP TLS 模式；结构校验和真实投递测试分别显示；安全引用操作、影响预览与并发保护 |
| 发布详情及 Coverage | 共享真实审批资格；统一修正契约管理、JWT/重试/缓存与本批能力说明 |

企业 IM 保留现有意向，继续标为后续能力。通知仅来自告警生命周期；本批不新增审批提醒、营销邮件、订阅中心、审批 SLA 到期政策或自动批准。审批风险继续采用实际冻结评审摘要，不新增风险规则。

## 3. 模块与依赖

| 单元 | 职责及接口边界 |
| --- | --- |
| NotificationConfigurationService | 在已有系统设置治理中验证渠道配置、显式启用、秘密版本与不可变连接档案；复用 ETag、预览确认和幂等 |
| NotificationSecretResolver / SecretVersionProtector | 将已配置的逻辑引用映射到受控部署文件，读取分渠道秘密；保护版本摘要，不处理业务权限 |
| NotificationPlanner | 在已持有告警生命周期锁的事务内，按冻结通知策略创建任务或抑制回执；无网络发送 |
| NotificationDeliveryStore / Worker | 有界领取、租约、尝试、期限、状态协调和重启恢复；网络发送在事务外 |
| SmtpNotificationTransport / WebhookNotificationTransport | 单次发送，返回脱敏接受结果、永久错误、可重试错误或结果不确定；不自行重复发送 |
| NotificationAddressPolicy | 部署允许目标、DNS 解析与连接地址绑定、TLS、连接预算；不直接沿用 SSO 的开发环境例外 |
| ApprovalEligibilityService | 从真实身份、Role、Scope、冻结审批规则和任务状态得出可办理资格；查询与 ReleaseService 办理共享此规则 |
| ApprovalInboxService | 对已授权审批申请做分类、筛选、排序、分页及计数；不创建新审批状态机 |

本批使用现有 PostgreSQL、Worker、Control Plane 和 Console，无新增生产微服务。SMTP 库在实施计划中固定版本和许可证，开发前验证目标 .NET 版本及有界取消、TLS、绑定地址连接能力；不得为了库的默认行为降低本设计约束。

## 4. 平台渠道配置与秘密

### 4.1 配置字段

原 `smtpHost/smtpPort/fromEmail/smtpSecretRef/webhookUrl/webhookSecretRef` 原样保留，新增：

| 字段 | 语义与默认 |
| --- | --- |
| smtpEnabled | boolean，默认 false；只控制自动告警 Email，不影响明确发起的配置测试 |
| smtpSecurity | `StartTlsRequired` 或 `TlsOnConnect`；默认 StartTlsRequired；禁止机会式 TLS 和明文回退 |
| webhookEnabled | boolean，默认 false；只控制自动告警 Webhook |

保留原 SMTP 四字段完整性、Webhook HTTPS/禁止 userinfo/query/fragment 的验证。部署目标策略另行限制端口和允许地址。保存未启用的完整配置可以只做结构校验；启用时须解析秘密、建立受保护版本指纹并通过部署地址策略，但保存不产生网络发送。真实投递测试独立执行，不是启用的隐含副作用。

启用、禁用、连接字段或秘密引用变更都显示影响预览并沿用五分钟确认凭证、ETag、幂等与事务内重新鉴权。禁用影响未启动的自动投递；连接或秘密版本变化使旧连接档案的任务终止为配置已变更，不把旧消息改投到新地址。另一渠道的变更不影响本渠道档案。

### 4.2 秘密与连接档案

保留 `vault://<provider>/<path>` 逻辑引用格式及 Keep/Replace/Clear。部署配置按完整引用精确映射到本地受控秘密文件；这是 LocalFileMapping 提供方，不执行 Vault 网络请求，也不声称接入 Vault 产品。没有匹配项返回 SecretUnavailable，不自行搜索环境变量或读取任意用户路径。

SMTP 文件为严格 UTF-8 JSON `{username,password}`；username 1–320 字符，password 1–4096 字符，拒绝 NUL/CR/LF。Webhook 文件为严格 JSON `{keyBase64}`，解码后 32–64 字节。文件总大小 1–16KiB，拒绝重复、额外属性与过深 JSON；空白仅按 JSON 规则解析，不改写密码。全部路径组件拒绝符号链接，打开时防止叶文件替换；文件仅服务账号可读，不允许 group/other 权限。

不可变连接档案冻结渠道地址、发件人、TLS 模式、逻辑引用、渠道配置摘要与受保护的秘密版本指纹。表不存秘密明文，公开 DTO 不返回引用全文或指纹。发送时精确重新解析引用并验证指纹，文件被替换视为 SecretVersionChanged，不使用新秘密代发旧消息。管理员通过新的设置预览与保存显式激活轮换；即使引用文字不变，也能重新固定版本，旧任务不重定向。

将持久化 DataProtection 配置放到 Control Plane/Worker 可共享的基础模块：同 ApplicationName、同原有 dp-keys 卷和用途隔离。Control Plane 保持原应用名和既有 SSO 用途，Worker 挂载同密钥目录，确保创建及读取通知档案均跨重启有效。原 SSO 指纹、会话保护和历史密钥完整保留。冷备纳入原 dp-keys 及新通知秘密卷；缺失密钥导致通知暂停并报告不可用，不能静默换钥。

### 4.3 出站连接与数据最小化

部署管理员配置允许的 SMTP host:port、Webhook origin/path 和允许的私网 CIDR；无配置则拒绝出站。DNS 的全部结果必须通过地址策略，实际 socket 使用本次已验证地址，TLS 仍按原主机名校验证书。拒绝未指定、组播、链路本地和元数据地址；私网需显式 CIDR。禁止自动重定向、Cookie、系统代理和无效证书绕过。

SMTP 收件地址还须命中部署配置的完整邮箱或域名名单；平台设置展示名单及其部署来源，规则管理者不能修改此名单。单条规则最多 20 个不同合法邮箱，每项至多 254 字符；不接受显示名、多地址拼接、CR/LF、Cc/Bcc 或从用户自报 Email 自动推导目标。

只发送事件 ID、发生次序、环境标识、级别、规范化指标条件、触发或解决时间与受控控制台链接。不得包含凭证、请求/响应正文、Bearer、Cookie、API Key、个人声明、任意用户批注或静默/解决原因自由文本。链接来自部署 ConsoleBaseUrl，收件人访问后仍需正常登录和 Scope 授权。消息体最大 16KiB，超限任务终止并保留 PayloadTooLarge 回执，不截断成无效消息。

## 5. 规则通知策略与事件冻结

扩展既有 NotificationIntent，保留 `inConsole=true/requestedChannels`。新增 `externalEnabled=false`、`emailRecipients=[]`、`notifyRecovery=true` 及规则级重试配置：

| 字段 | 范围 | 默认 |
| --- | --- | --- |
| maxAttempts | 1–5，包含首次实际发送 | 5 |
| baseDelaySeconds | 1–300 | 30 |
| maxDelaySeconds | baseDelaySeconds–3600 | 900 |
| expiresAfterMinutes | 5–1440，自任务创建计 | 1440 |

externalEnabled=true 时至少选择 Email/Webhook 中一个；Email 必须有通过名单验证的收件人；企业 IM 的意向不算有效渠道。可以保留渠道暂未启用的规则配置，但显示“渠道未启用”，新事件创建抑制回执，不虚报投递。仅修改通知字段继续不增加 logic_revision，不重置告警持续计时；规则 revision 正常递增。

每个新告警 occurrence 冻结当时规则通知定义和所涉连接档案 ID，后续规则编辑只影响新 occurrence。站内告警一直生效，外部开关独立。规则禁用、判断逻辑或 Scope 变更先关闭原事件并取消其未开始任务，不能借用新的收件人发送旧事件。

| 生命周期 | 外部行为 |
| --- | --- |
| 新 Triggered 转换 | 按冻结策略生成每个 Email 收件人各一条任务、Webhook 一条任务；不匹配配置的渠道生成明确抑制结果 |
| Ack | 保留已规划的触发任务；不新增确认通知 |
| Silence | 同锁内使尚未开始的 Triggered 任务进入 Paused；不新增静默通知 |
| Unsilence / SilenceExpired | 对仍未解决、未过期且配置仍有效的 Paused 任务恢复原任务；不新建第二份触发任务 |
| 条件恢复或用户 Resolve | 取消尚未开始的触发任务；只对该渠道/目标存在 Accepted 或可能已送出的 OutcomeUnknown 尝试且 notifyRecovery=true 时，规划配对的 Resolved 通知 |
| RuleChanged / RuleDisabled / ResourceRetired | 终止未开始任务，保留站内关闭事实；不把治理关闭描述为指标恢复 |

Resolved 通知通过 occurrence、渠道、目标和父 TriggeredDeliveryId 配对，使用原冻结目标；用户 Resolve 明确表示人工关闭，不表示指标恢复。Resolved 前处于静默时不发送解决通知，保留 SuppressedSilenced 回执；不在解除静默后补发过期的解决消息。若恢复时 Triggered 尝试仍在进行，先保持恢复协调 Pending：待其结束或租约回收后再按 Accepted/OutcomeUnknown 决定配对，不能产生“先恢复后触发”。

恢复协调由持久事件的 Resolved 转换、冻结通知快照和父任务状态推导，不能只放在内存回调中。Worker 每轮有界扫描待协调事件，尝试写回后也协调相应事件；重新启动仍能找回尚未完成的配对。Resolved 转换唯一键保证重复协调只生成同一任务或抑制回执。静默判断采用该转换的 FromStatus，解决后的 Status=Resolved 不得抹掉此前静默事实。解决通知的期限从其自身创建时间计，冻结的策略参数不变。

升级时已有 occurrence 不追溯创建通知。所有旧规则即使有 requestedChannels，也默认 externalEnabled=false；启用渠道不会替旧规则自动开启发送。

## 6. 持久化模型与投递协议

### 6.1 新事实表

| 表 | 主要字段与约束 |
| --- | --- |
| notification_channel_profiles | UUID、channel、配置摘要及私有冻结 JSON、ProtectedSecretFingerprint、创建人/时间、settings_revision；不可变 |
| notification_channel_states | channel 主键、当前 profile_id、enabled、revision；与系统设置在同一治理事务更新，只记录有效档案指针 |
| notification_deliveries | UUID、kind(Alert/Test)、event/transition/parent ID、真实 Scope、channel、目标、profile_id、冻结 payload、策略/到期时间、状态/原因、attempt_count、next_attempt_at、lease_owner/token/until、revision、创建/完成时间 |
| notification_delivery_attempts | delivery_id、attempt_no、lease_token、开始/结束时间、结果码、SMTP 接受码或 HTTP 状态、OutcomeUnknown 标记；delivery_id+attempt_no 唯一 |

Alert 任务必须属于事件和转换的实际 Scope；唯一键为 event_id+transition_id+channel+规范化目标摘要。Email 每任务仅一个信封收件人，避免部分收件人成功导致重复群发。原始目标留在私有任务中；普通告警 DTO 只显示掩码和接收目标数量，规则管理员可在自身授权范围查看其已配置邮箱。资料包和一般审计不包含原始收件地址。

缺少有效渠道档案的抑制回执允许 profile_id=null；只有 Suppressed/Expired 且明确记录配置缺失等原因的记录允许缺失档案，任何实际发送任务必须有档案。Test 任务没有事件/转换与业务 Scope，必须有创建者和平台归属；测试的幂等键与限流事实在同一治理事务持久化。

AlertEvent 增加冻结通知策略 JSON。旧行填禁用快照，不解释缺少字段为开启。新表增加租约扫描、事件详情和当前状态所需索引，并限制状态、尝试号、期限及字符串/JSON 大小。现有部署 outbox、release_records 和 approval_tasks 的业务含义保持。

### 6.2 领取、发送与重试

状态为 Queued、Sending、RetryScheduled、Paused、Accepted、Failed、Suppressed、Expired；终止原因独立枚举，不塞入 SMTP/HTTP 异常原文。

Worker 默认 4 个并发发送，部署可设 1–8；每轮最多领取 50 条，1 秒轮询。每次只为即将执行的任务分配 60 秒租约，不能提前批量锁住大量等待任务。使用数据库时间、FOR UPDATE SKIP LOCKED 和单调 lease_token；记录实际 attempt 开始，再在事务外发送。发送整体预算 10 秒（含 DNS、连接、TLS、协议和读取），响应最多读取 4KiB，进程取消终止连接；租约到期后的旧执行者不能覆盖新结果。

在短事务内按既有治理→规则→事件→任务顺序检查静默、有效 Scope、当前渠道状态/档案、任务期限与次数，确定发送开始点，然后释放事务并联网。静默或禁用发生在该点之前时不发送；发生在其后时只能阻止后续尝试，已开始的外部发送可能完成，UI 明确区分“停止后续投递”和“已发送”。不持有数据库锁等待网络完成。

SMTP 最终 DATA 接受码、Webhook 2xx 记为 Accepted；只代表服务接受，不代表邮件阅读或接收方业务完成。Webhook 3xx、非重试 4xx、SMTP 5xx、TLS/地址/配置错误记永久失败或抑制；429/408/5xx、SMTP 4xx 和网络暂时失败可在预算内重试。网络断开不能确定远端是否已接受、进程在发送后写回前退出或超时租约回收，都记 OutcomeUnknown，承认潜在重复。

退避为 min(maxDelaySeconds, baseDelaySeconds × 2^(attempt_no−1)) 加 0–20% 有界正抖动，再限于 maxDelaySeconds。Webhook 有效 Retry-After 取较长延迟，超过部署上限 3600 秒则 Failed/RetryAfterExceedsBudget，超过任务剩余期限则 Expired；不截短 Retry-After 提前发送。达到 maxAttempts 终止为 Failed/AttemptsExhausted。

Webhook 使用稳定 `X-WebAPI-Delivery-Id`，每次尝试更新 Unix 秒时间戳和签名；签名为 HMAC-SHA256(secret, UTF-8(timestamp + "." + deliveryId + ".") || 精确 body 字节)，输出小写十六进制。JSON body 固定 v1 消息 schema，与任务冻结字节一致。接收端应校验签名、合理时间窗并按 deliveryId 去重；本机接收器证明协议，不能替未知企业接收端宣称去重有效。SMTP 使用稳定 Message-ID，但 SMTP 不保证重复消除。

### 6.3 人工恢复与证据

具备该事件 alert.operate 的用户，可对 RetryScheduled/Paused/Failed 暂时性失败发起一次幂等 retry 命令；仍受原冻结目标、当前静默/范围/档案、原截止时间和剩余 maxAttempts 限制。命令不重置次数、不增加截止、不换地址、不重复创建任务。次数耗尽、永久失败、已 Accepted/Expired/Suppressed 或配置版本失效时按钮不可用，服务端也拒绝。测试任务仅 system.manage 的平台管理员可操作。

人工命令也不能越过既有退避下界或 Retry-After；在等待窗口内仅显示已安排的下一次时间，不额外创建尝试。Paused 必须已解除静默，才能请求恢复。Failed 若无剩余次数或不属于暂时性错误，即使按钮请求被伪造也不能重试。

回执输出尝试号、状态码、结果分类、时间及可能重复标记，不输出响应体、SMTP 文本、秘密引用、原收件人或异常堆栈。Worker 系统审计只记任务 ID、Scope、渠道和安全原因；规则编辑审计改为通知摘要、收件人数及变更字段，不保留邮箱自由文本。

## 7. 配置测试与 API

保留已有 `/settings/system/notification/validate` 为 StructuralOnly。“校验配置”不发送。新增真实测试采用已保存配置、明确选择渠道和测试目标；未保存草稿先保存，不能任意输入独立 URL 或 SMTP 地址绕过治理。

管理员测试必须通过相同秘密、地址、收件目标和 TLS 策略。测试无需打开自动通知开关，但保存配置的渠道档案/版本须有效；未启用配置在测试创建时固定专用不可变档案，测试档案不写入当前启用档案指针。Test 在发送前比较其冻结渠道配置与当前已保存配置，并校验秘密版本，不能使用被编辑后的配置继续测试旧值；自动开关关闭不是 Test 的抑制条件。生成独立持久 Test 任务，默认单次尝试、5 分钟期限，每用户/渠道每分钟最多一次、平台同时最多 10 条未终止测试任务。重复 Idempotency-Key 返回原任务；排队响应 202 不显示发送成功。查看后续回执才显示接受或实际失败。

| API | 授权及语义 |
| --- | --- |
| POST `/api/v1/settings/system/notification/tests` | system.manage、If-Match、Idempotency-Key；saved revision+channel+Email 测试收件人；Webhook 使用已保存目标 |
| GET `/api/v1/settings/system/notification/tests/{id}` | system.manage；安全结果，no-store |
| GET `/api/v1/alerts/{id}/notifications` | 实际事件 Scope 的 alert.read；分页投递摘要 |
| GET `/api/v1/notification-deliveries/{id}/attempts` | Alert 任务需其真实 Scope 的 alert.read；Test 任务 system.manage；无权 ID 返回 404 |
| POST `/api/v1/notification-deliveries/{id}/retry` | Alert 任务 alert.operate；Test 任务 system.manage；If-Match、Idempotency-Key，重新检查预算和全部约束 |
| GET `/api/v1/approvals` | 活动会话；见下节；no-store，无权筛选 ID 返回 404 |

保存、测试、重试全部沿用 CSRF、请求大小限制、规范 JSON、ETag/412、幂等请求绑定及审计。通知配置响应和校验/预览不得回显秘密或引用原文。

## 8. 跨环境审批中心

### 8.1 数据集合与共享资格

只聚合已有 ApprovalTask 的发布/回滚申请，不把非生产直接 Ready 的记录、Draft 或任意发布列表都当审批任务。每个 Release 在列表中仅占一行，即使一级有多个 Pending 席位。

列表所需基本可见性为 release.read 和实际环境可读 Scope。`PendingMine` 还必须满足 WaitingApproval、当前最小 Pending 步骤、冻结角色（或现有 PlatformAdmin 规则）、approval.act、实际环境 read_write Scope、活动组织/项目/环境、非申请人，以及未在此申请占任何已批准席位。不因管理员身份绕过独立审批。

`HandledMine` 按已 Approved/Rejected 任务中的实际 AssigneeUserId 查询，再按当前可见性过滤；撤销角色但保留 release.read 时可查看历史，撤销 Scope 后不返回历史。`AllVisible` 返回全部有审批任务且当前可读的申请，包括 Ready/Published/Rejected/Cancelled/Failed 等真实状态。

ApprovalEligibilityService 作为查询和办理的共同规则入口。审批动作在现有治理事务内重新检查身份、角色、真实 Scope、冻结规则和当前 Pending 席位；列表资格不是操作凭据。新查询不改冻结 CandidateBytes、生产两级、各级人数或现有拒绝/取消语义。

release.read 是列表与详情的可见性条件，不给既有 approve/reject 命令新增读取权限门槛；共享办理资格仍保留原 approval.act 契约。列表将可见性与办理资格取交集，不能因为功能权限中的一项缺失而借查询新授予另一项。

### 8.2 查询、计数与保护

GET `/approvals` 参数：`view=PendingMine|HandledMine|AllVisible`，可选 organizationId/projectId/environmentId、真实 status、page>=1、pageSize 1–100（默认 50）。层级关联必须真实；允许单独指定一个已授权环境并在服务端解析祖先。没有 Scope 筛选时，默认跨当前用户所有可访问组织/项目/环境，不被 Shell 当前环境隐式截断。

服务端从授权集合得到环境和角色关系；合并功能权限与 Scope 后，再选择申请、分类、计数、排序与分页。禁止前端逐环境拉取拼接，禁止先分页再做资格过滤，禁止未授权全局总数。查询、各标签计数和列表使用同一数据库一致性快照；新会话/撤权后重新读取，不凭旧前端 Scope 缓存扩大访问。

排序固定 CreatedAt DESC、ReleaseId DESC。计数按申请数，分别返回 pendingMine/handledMine/allVisible；Scope 筛选一致，status 筛选仅作用于当前列表且在 UI 标示，避免跨标签历史状态导致误解。大数据查询应使用参数化 SQL/EF 查询与适当索引完成分页，不能无界加载全部候选或逐条调用完整 ReleaseDtoAsync。查询预算 10 秒；超时返回可重试错误，不截断后伪造正确总数。

响应仅含申请 ID/编号/类型/状态、实际组织项目环境、申请人安全显示名、时间、当前步骤/已通过与所需人数、本人操作资格和实际冻结风险摘要。列表不含候选正文、凭证、批注、收件信息或无权 API 风险细节。风险资料继续采用现有 API/version/schema 权限投影；无资料权限显示“受限”，不以零风险代替未知。仅有 approval.act 而无 release.read 不新增读取权限。

### 8.3 UI 与竞争处理

`/approvals` 使用独立组件，不再别名 Releases。三个标签、授权范围筛选与分页保存在 URL；默认“待我审批”。显示真实申请人、环境、等待时长、风险、当前步骤和所需人数，不显示没有业务依据的审批到期或 SLA Badge。

可直接打开同一发布详情；跨环境导航使用其真实 Scope，保留从审批中心返回时的 URL 上下文。批准/拒绝弹窗显示申请及冻结候选摘要，批注允许沿用现有 10000 字符限制；按钮以服务端资格为准。每次语义操作使用独立幂等键，失败重试同一操作只复用同一键，不自动批准刷新后的新步骤。

他人抢先办理、申请取消、角色/Scope 改变时显示明确 409/403 等结果并刷新待办。普通冲突保留未提交批注；会话失效或失去该申请读取权限则关闭弹窗并清除申请详情、批注及旧列表，防止权限降级后继续显示受限内容。轮询每 5 秒，仅对当前可见页面执行；身份、筛选变化取消旧请求，迟到响应不得覆盖新范围。

继续沿用统一设计系统、1440px 优先；1280px 可完整操作。密集表格、环境标签、风险摘要与结果可读，键盘可操作标签、筛选、弹窗、批准/拒绝及详情返回。

## 9. 迁移与本机部署

迁移只增加上述事实表、AlertEvent 通知冻结列及所需索引；扩展 system.notification JSON 时保留原六字段精确值并补充关闭默认。旧规则 notification JSON 保留 RequestedChannels，补齐 externalEnabled=false、空收件人、默认有界策略。迁移可以正常读取先前合法配置；未知/非法 JSON 失败并出具定位信息，不能以空值偷偷修复。

本机新增独立通知秘密卷，Control Plane/Worker 只读挂载；dp-keys 沿用原卷。本机验收使用受控 SMTP 收件箱和带可信 fixture TLS 的 Webhook 接收器，具备接受、暂时错误、永久错误、延迟与断连模式；按部署白名单准许确切本机服务。fixture 例外只能由独立测试/本机演示配置显式开启；通用 Development 环境不自动允许任意明文或私网目标。演示 SMTP 如使用无认证或明文，必须限定 UUID 验收实例并标明此分支，正式渠道仍验证强制 TLS 和凭证。

原 4192 若安装演示收件服务，需纳入固定源码运行工具、服务状态、冷备/恢复、启动顺序和维护说明，服务及接收端口仅本机可访问；不会向未提供的企业地址发送。已有渠道意向不自动启用，原业务配置版本/部署序列无需因本批自动重发布。

实施后沿用固定 Git 提交构建、独立克隆验收、精确本地合并、原部署冷备和升级。先核对所有原账号、SSO、业务行、附件与历史工具，再比较部署后事实。不能用代码测试通过代替原 4192 安装成功。已核对的旧 SettingsValueCodec 严格要求通知六字段，因此本批扩展 JSON 后不支持直接切回旧镜像。部署计划必须在升级前验证保留新业务数据的恢复路径；采用兼容桥接版本时独立固定源码并验证其能读取迁移后配置，否则保留候选修复前进，不能宣称直接降级，不能用旧数据库覆盖新增业务数据。恢复方案及限制必须写入安装前结果供用户审阅。

## 10. 验收矩阵

| 层次 | 必须取得的证据 |
| --- | --- |
| Domain | 字段和预算、旧配置迁移、状态/退避/静默/恢复配对、稳定签名、秘密指纹与共享审批资格 |
| Integration | 状态转换和任务原子写入、唯一键、并发 Worker 租约与过期 fencing、重启、不确定结果、配置/秘密轮换、永久错误、权限/Scope/撤权、幂等和 412、只读 RuleTest 不发通知 |
| 真实投递 | 单收件人 SMTP 接受、TLS 强制、真实 Webhook 签名和 HTTP 回执、失败重试次数及时间、断连可能重复、被白名单/证书/DNS/重定向拒绝、不泄漏秘密 |
| 审批查询 | 多组织项目环境、多于一页、多席位去重、标签总数、当前角色/步骤、自批/重复席位、只读 Scope、环境停用、取消/并发办理、受限风险数据、查询与办理一致 |
| Console | 设置结构校验与测试 202/回执区别、保存及秘密 Keep/Replace/Clear、旧渠道意向关闭、通知记录/重试状态、跨环境标签筛选和 URL 返回、冲突批注保护/撤权清空、1440/1280及键盘交互 |
| 本机原部署 | 固定软件/镜像/静态资源身份、新旧卷冷备可读、账号及 Keycloak 登录保留、业务与文件基线核对、双网关和观测源健康、通知长期维护工具、原 4192 页面截图及验证结果 |

测试凭证、原密码、Cookie、秘密卷、私有投递目标和冷备只保存在私有运行目录；公开证据脱敏，交付 ZIP 排除这些材料。记录源码、镜像、迁移、测试日志和本机结果各自身份；Coverage 只声明已经取得对应证据的能力。

## 11. 自查与下一阶段

规格自查已覆盖：新旧通知不自动启用；发送在事务外且有原子任务；已开始发送的静默边界如实表达；恢复消息配对涵盖不确定及进行中尝试，重启后可重新协调；测试不受自动开关阻止但仍固定已保存配置；秘密轮换不改投；Worker/Control Plane 持久密钥一致；查询、总数和办理共同鉴权而不改原操作权限；既有生产两级和业务发布保持；旧通知严格解码导致的直接降级限制明确；资料不代替企业端点验收。无未填写占位章节。本自查为设计一致性检查，不是产品测试通过证明。

参数与枚举为本规格提出的具体实现规则。用户已在本规格交付后回复“确认”；按本规格形成实施计划，用户审阅计划后按已确认的执行方式进行开发、验证和交付。
