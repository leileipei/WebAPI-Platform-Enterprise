# 发布与交付中心：TEST 到 PROD 晋级首期设计

日期：2026-10-09。核对源码：`4ceda71ef2b8c5eabb42a882de6734fc3422a530`，另有既存本地修改。

状态：用户先回复“都安装推荐继续”，按“都按推荐继续”理解选择先完成晋级闭环、再开放流水线；随后回复“确认设计”，本正式规格已批准，进入实施计划审阅。实现、部署和生产验收尚未执行。

前置规格：`2026-10-09-environment-access-address-design.md`。两部分分别制定实施计划，先建设地址元数据，再建设本晋级闭环。

## 1. 目标与首期范围

把项目内相互独立的配置发布连接成可追溯的交付流程：不可变制品 → TEST 发布与验证 → 测试验收 → PROD 环境映射与预检 → 独立生产审批 → Gateway 下发与确认 → 业务验证 → 交付完成。

首期支持同一组织、项目的一条已配置测试环境到生产环境的连接，不以环境 Code 判断角色；环境须启用，来源非生产，目标 IsProduction=true。TEST 与 PROD 保持独立入口、后端、授权和运行配置。API/version 是现有项目级资源，跨环境引用同一封存版本，环境 Route、Cluster 和授权分别解析。

复用 Release、现有生产两级审批、SnapshotCompiler、PublishCoordinator、Worker、ACK 和回滚。保持普通开发测试发布。新增制品、验收、目标映射计划和晋级编排；不新增生产微服务。

成功条件：生产实际运行配置可追溯到具体测试发布、不可变制品、测试证据、目标候选、审批及节点确认；不能靠旧测试结果批准改过的内容；目标不存在所需 Route 时也能通过明确映射计划准备并发布，无需在生产重复编辑 API 契约。

首期不含可编程流水线、任意阶段图、多条环境分支、定时发布、流量百分比灰度、自动指标门禁、自动回滚、CI/CD 平台接入和紧急变更快捷通道。现有人工审批回滚保留，不赋予晋级审批以外的新豁免。

## 2. 产品入口和边界

一级入口更名“发布与交付中心”，首期提供交付总览、发布制品、环境发布、发布晋级、审批中心、配置快照、发布历史和回滚管理。原发布/审批/快照 URL 保留兼容导航。发布执行作为具体发布详情的区域，不另造一套下发列表或状态机。

“发布流水线”首期仅提供本项目 TEST→PROD 连接及门禁设置，不显示可编辑任意 Stage 的引擎。阶段编辑器放入后续范围，不显示未完成的功能为已上线。审批中心沿用待我审批/我已处理/全部申请视图，通过申请类型识别生产晋级，测试验收在制品详情办理。

## 3. 现有基础与必须调整的边界

- 冻结候选包含 EnvironmentId、集群、策略、应用和凭证，不能直接跨环境使用。
- SnapshotCompiler 已校验环境、基线及资源范围；继续按目标环境编译。
- PublishCoordinator 已有环境锁、基线/版本、节点与风险评审检查；晋级新增检查在启动和 Worker 构建两处执行。
- AckService 将全部冻结目标成功确认记为 Release.Succeeded。该语义保留；新 Promotion.Completed 另需发布后业务验证。
- 现有跨环境审批中心聚合原 ApprovalTask，不包含跨环境复制或晋级。
- 凭证实体属于应用，环境隔离主要通过授权实现。首期不能假定所有凭证已按环境独立存储。

## 4. 制品、证据与环境映射

### 4.1 不可变制品

从成功来源发布的冻结内容抽取 selected API/version、参数/Schema、逻辑路由键、方法/路径/优先级/启用状态、策略类型/执行顺序和与环境无关的规范化行为，保存 canonical JSON 与 SHA-256。来源 SnapshotHash 另行记录，不能将它作为跨环境制品 Hash。

不包含来源 Gateway/Cluster/Destination ID、地址、节点、应用 AccessKey、SecretHash、JWT 公钥材料或秘密引用原文。策略使用显式类型定义拆分模板和环境参数；不能以全文复制再正则替换 URL 实现晋级。首期只支持现有执行能力；出现无法分类的策略字段阻断制品生成。

环境参数允许项按策略类型限定：Cluster/Destination、timeout 数值、限流配额、JWT issuer/audience/可信公钥来源和应用映射。认证模式、路径/方法、Schema、重试条件及缓存语义等行为字段冻结；改变它们生成新制品并重新测试。环境参数允许项不代表免验证，目标差异需预检和审批。首期不复制或新建凭证。

制品由成功来源发布生成，来源冻结正文或 Snapshot 无法校验时拒绝。同一来源 Release+制品摘要唯一，内容不可更新。更改工作配置不改制品；封存版本若异常变化，晋级阻断。首期不支持跨项目/组织晋级。

### 4.2 测试证据和验收

首期支持人工登记测试结果和受控上传报告，不自行宣称已运行自动测试。报告保存为平台管理附件，不自动读取外部链接。记录验证人、类型、制品 Hash、来源 Release/ConfigVersion/DeploymentSequence/SnapshotHash、测试环境入口修订、起止时间及 Passed/Failed。报告声明为人工登记，附件摘要用于追溯，不能证明报告真实性。

类型至少含接口功能、集成、契约兼容性；项目连接规则规定必需类型，默认三类均需。契约兼容性引用现有比较引擎的实际能力和人工风险评审，Unknown/Unsupported 不显示无风险。

测试验收单关联确切制品和证据集合。申请人与验收人不同；验收人须具备 release.test.accept、来源环境 read_write 和 API 资料可见性。不把 TEST Release.Ready、节点 ACK 或旧审批当测试验收通过。证据/验收记录追加，不原地改 Passed；可追加撤销事实，撤销后停止尚未执行的晋级。

首期一次验收即可满足连接，不建设多级测试审批引擎。证据未齐、来源发布失败/回滚、当前来源环境已被其他配置替换时不能新建晋级；先重新测试当前目标内容。生产审批前后均重新确认验收有效性。

### 4.3 目标环境映射计划

映射绑定本制品和目标环境，使用逻辑路由键到目标 Cluster、类型化策略参数、目标应用授权与凭证选择的显式关系。选择现有目标资源或创建本制品的目标 Route/策略绑定工作草稿；涉及创建资源同时检查既有 route/policy 写权限。不得修改共享策略或覆盖不属于本次制品的 Route。需要目标全局策略变化时先完成独立受审变更。

Route 不存在时按制品冻结行为生成新目标 Route；存在时核对归属、方法路径和版本，创建或更新该 Route 的草稿。准备时事务内校验目标基线与修订，写入目标工作资源并记录准备日志，生成目标冻结候选；该操作不下发 Snapshot。生产直接发布受新模式约束，不能借准备草稿绕过晋级。

目标凭证采用显式选择集合扩展候选构建：候选不得自动包含目标应用的全部凭证。只允许选用已经存在且有效的目标配置凭证；若应用跨环境共享，页面显示共享事实并要求目标审批确认。禁止自动使用来源候选中的凭证集合。保留基线其他路由所需应用和授权，不为新增 API 秘密删除既有授权；共享应用凭证集合变动须列为全环境影响。不能用此次晋级声称实现了完整环境凭证隔离。

预检包含映射完整性、目标范围、资源修订、与当前 PROD 运行配置的契约/路由差异、策略安全参数、应用授权/凭证有效性、路由冲突、上游地址允许策略、节点在线/schema、目标入口已配置和回滚快照可用性。首期上游健康由人工验证证据记录；不新增任意 URL 健康探测器。

## 5. 数据模型和约束

| 模型 | 关键内容 |
| --- | --- |
| project_delivery_policies | project_id 唯一、source_environment_id、target_environment_id、mode、required_test_types、verification_validity_minutes、revision；有效期默认 1440 分钟，配置范围 1–10080 |
| release_artifacts | 来源 Release、Scope、canonical 内容、artifact_hash、source_snapshot_hash、创建人/时间；内容不可更新 |
| release_verifications | artifact_id、release_id、环境/配置/序列/入口上下文、phase=SourceTest/Production、类型、人工登记标识、结果、报告摘要及附件 ID、操作者/时间、expires_at |
| release_test_acceptances | artifact_id、冻结证据 ID 集合与摘要、申请人/验收人、状态、修订、意见与时间；撤销另存事件 |
| release_promotions | artifact_id、来源/目标环境、来源/目标 Release、状态、baseline、mapping_revision、candidate_hash、target_access_address_revision、冻结连接规则、revision、申请人/时间 |
| release_promotion_mappings | promotion_id、逻辑资源键、目标资源引用/参数及修订；编辑仅限 Draft |
| release_promotion_events | promotion_id、阶段、前后状态、原因码、操作者、时间、关联发布/验证 ID；追加记录 |

release_records 添加可空 artifact_id、promotion_id、source_release_id，旧记录保持 NULL；promotion_id 唯一，使一次晋级只产生一个正式目标 Release。失败重试仍使用既有 RecoveryOf 链，关联回原晋级，不生成未审批的新内容。所有关联采用真实外键和 Scope 校验，不依靠前端传入组织项目名称。

已有 approval_tasks 复用目标 Release 两级审批，不新增独立的生产晋级审批表或重复审批状态机。测试验收属于不同业务事实，单独持久化。

每项目首期只配置一条来源到目标连接。mode=`Legacy` 或 `PromotionRequired`；旧项目迁移为 Legacy，并明确显示尚未启用生产晋级门禁。管理员以 project.write+目标 environment.write 和 read_write Scope 保存连接并显式启用 PromotionRequired；启用须已配置来源/目标、生产审批模板及目标公开入口，且目标没有进行中的旧发布。启用后正常生产 publish 必须有有效 Promotion，不放行新建直发。历史回滚/恢复走既有权限和审批路径。首期没有紧急直发豁免，不把回滚变成任意新配置发布。

## 6. 状态、并发与失败

Promotion 状态为 Draft → WaitingApproval → Ready → Deploying → Verifying → Completed。分支为 Rejected、Cancelled、Invalidated、DeploymentFailed、VerificationFailed、RolledBack。审批状态由目标 Release 投影，不能由 UI 单独修改。

Draft 阶段可编辑映射并执行预检；提交时冻结制品、来源验收、目标候选及其摘要、资源修订、环境基线、入口修订和连接规则，创建并提交目标 Release。晋级创建、目标资源准备、制品唯一关联、Release 和事件在同一业务事务内协调。

使用 If-Match、Idempotency-Key、现有治理锁及目标环境锁。需要同时锁两个环境时按环境 UUID 排序，统一锁顺序避免死锁。锁内验证权限、来源事实、验收有效期、审批规则、目标基线与资源。不得在锁内等待文件扫描或网络验证。重复命令键+相同正文返回原结果，键+不同正文拒绝。

审批后，制品、目标工作资源、连接规则或入口修订变化使候选失效，执行拒绝；不自动更新冻结内容沿用旧批准。旧申请 Cancelled/Invalidated 后以新晋级重新准备、预检和审批。现有普通环境发布规则保持；新增严查只适用于晋级关联候选。

发布启动与 Worker 构建都重新检查验收、当前权限、目标修订与基线，处理排队期间变化。已开始下发后不承诺全节点瞬时一致。部分 ACK 失败/超时记 DeploymentFailed，详情显示实际节点版本，不用失败状态暗示无节点生效；人工按现有恢复或审批回滚执行。

全节点 ACK 后进入 Verifying，原 Release 仍为 Succeeded。生产验证必须绑定本次目标 Release、配置版本、DeploymentSequence、SnapshotHash 和入口上下文。默认必需入口连通、认证授权、关键业务调用三类人工登记 Passed；验证人与申请人、实际发布执行人均须不同。缺项、过期或权限不足不能 Completed。

VerificationFailed 不自动回滚；提供创建目标环境历史回滚申请入口，完成既有审批和 ACK 后记 RolledBack。入口地址本身变化不会被 Snapshot 回滚恢复。Completed 后环境发生普通后续部署仅将旧交付标为历史，不更写旧验证；若入口变化，则历史详情显示其证据不适用于当前入口。正在 Verifying 时入口或实际运行配置变化使本次验证失效，需要按当前实际版本另行取得完整验证，不沿用旧证据。

## 7. 接口、权限与审计

新增接口均位于 `/api/v1`，按实际 Scope 鉴权、分页、no-store；来源/目标任一无权时拒绝，不返回跨范围详情。

| 接口族 | 职责 |
| --- | --- |
| GET/PUT `/projects/{id}/delivery-policy` | 查看/配置项目连接；PUT 为 ETag 和幂等命令 |
| POST `/releases/{id}/artifacts`、GET `/release-artifacts[/{id}]` | 从成功来源发布生成、查询制品 |
| POST `/release-artifacts/{id}/verifications` | 登记来源测试证据及受控附件引用 |
| POST `/release-artifacts/{id}/acceptance-requests`、POST `/test-acceptances/{id}/accept|reject|revoke` | 独立测试验收与撤销 |
| POST/GET `/release-promotions[/{id}]`、PUT `/release-promotions/{id}/mappings` | 创建/查询晋级、编辑草稿映射 |
| POST `/release-promotions/{id}/precheck|submit|cancel` | 预检、冻结提交、取消未执行晋级 |
| POST `/release-promotions/{id}/production-verifications` | 登记发布后验证；满足条件后事务内完成交付 |

生产批准/拒绝/执行/恢复/回滚沿用目标 Release 现有接口，不新增平行命令。读权限 release.read+两环境 Scope；artifact 生成及晋级建立需 release.create 和所选 API/version/schema 可见性；目标资源准备额外要求对应写权限。新增 release.test.record、release.test.accept、release.verify；分别控制登记、验收、生产验证，默认不自动赋给已有普通角色。平台管理角色按现有权限目录机制增加，但仍遵守自批及独立人员限制。

制品含 API 契约和策略行为，详情按所引用资源可见性投影；部分无权时不把“受限”当零风险。报告上传采用平台管理附件，首期只允许 PDF/纯文本，单文件不超过 10 MiB，以下载附件方式返回，不执行 HTML、脚本或主动访问报告链接；附件须同 Scope，使用随机存储标识与摘要，读取重新鉴权。报告文件与业务回执一起纳入交付备份，业务事务只引用已成功落盘的不可变附件。

状态竞争返回 409、修订变化 412、权限拒绝 403/无权对象 404、业务验证拒绝 422。已过期的证据显示原因及重新验证入口。审计只存安全摘要和资源 ID，不保存密钥、凭证哈希、原始请求认证头或报告全文。

## 8. UI 主流程

来源成功发布详情提供“生成发布制品”；制品详情登记测试证据、发起测试验收，验收通过后进入晋级向导。

向导步骤：选择已验收制品 → 查看固定目标环境 → 映射目标资源与凭证 → 准备目标候选并预检 → 查看差异及入口 → 提交独立生产审批。目标详情保留跳转来源制品/发布/测试报告/验收人及返回上下文。

审批页明确显示制品摘要、来源验收、目标资源差异、共享凭证影响及入口配置；操作资格复用现有 ApprovalEligibilityService。发布详情分别显示审批、下发、节点确认和业务验证，避免“节点确认完成”被误读为交付完成。

交付总览从真实持久事实查询当前环境运行配置、待审批/待验证、失败晋级和来源追溯；服务端先授权再计数分页。沿用统一控制台风格，支持 1440/1280、键盘、冲突草稿保护、撤权清空与迟到请求隔离。

## 9. 验收和升级

| 层次 | 必须取得的证据 |
| --- | --- |
| 制品规则 | 稳定 canonical Hash、不含环境秘密、字段类型拆分、行为变化需新制品、未知策略阻断 |
| 测试验收 | 未齐/失败/过期/撤销证据阻断，实际来源版本绑定，独立验收，无自批 |
| 映射/候选 | 目标 Route 不存在、已有 Route 更新、Scope 拒绝、基线未选 API 保留、显式凭证与共享影响、修改后失效 |
| 并发事务 | 幂等、锁顺序、重启恢复、重复提交只一个目标 Release、旧批准不能执行新候选、排队期间撤权/修订改变 |
| 真实闭环 | 独立 TEST/PROD Gateway、不同上游，测试验收→生产审批→目标 Snapshot→真实响应→全 ACK→业务验证；无测试地址/凭证被自动复制 |
| 失败/回滚 | 部分 ACK/超时、业务验证失败、人工审批回滚与新序列、当前节点事实可见、入口变化不伪装已恢复 |
| 治理/Console | Legacy 兼容与显式启用、PromotionRequired 直发阻断、旧审批/回滚保留、多环境追溯、权限/附件下载、导航与可访问性 |
| 原本机实例 | 固定源构建、旧数据/SSO/文件保留、冷备与恢复、迁移兼容检查、双网关/观测回归、实际页面截图 |

两份规格的设计审阅不代表允许跳过实施计划审阅。实施计划分别固定开发隔离方式、任务、验证入口、数据迁移和原本机升级步骤。现有非洁净工作目录需要保留，不自动提交或覆盖用户修改。

升级采用先隔离验收、再检查原实例状态与备份的流程；不能用旧库直接覆盖升级后新增晋级/验证数据。使用真实企业 DNS/TLS/LB、企业上游、生产性能或 HA 时另行取得目标环境验收证据。Linux 本机演示通过不能替代生产验收。

## 10. 自查与后续

规格明确了共享 API/version 与环境 Route 的关系、制品不等于 Snapshot、目标 Route 准备、现有凭证未完全环境隔离、生产审批复用、ACK/验证分层、并发失效、旧模式迁移、人工报告证据范围和失败后的目标环境回滚。

第二期按独立规格开放可配置阶段顺序、多级门禁及自动报告接入；第三期设计流量灰度、自动指标验证、自动回滚与 CI/CD 集成。这些未来能力不计入本期完成条件。
