# API 版本比较、风险评审留档与发布衔接设计

日期：2026-10-06

状态：详细设计已编写并自查，等待用户审阅；尚未进入实现

核对源码基线：`8026813e69519f59ff79cf7b5581862e5719da93`

实施范围：用户选择的 A 方案，覆盖真实比较、风险评审留档和既有发布向导衔接。

## 1. 目标与依据

让 API 开发者比较同一 API 的两个真实版本，定位结构变化及兼容性风险；让有权限的评审人员留下可追溯的判断；让申请人携带这份证据进入现有发布流程。页面沿用已上线的企业控制台 Shell、1440px 桌面布局、表格和反馈组件。

依据分为三类：

- 用户请求：保留业务架构、字段、权限与流程，逐步补齐 40 页高保真可操作页面；本轮明确选择比较、评审、发布衔接一起推进。
- 产品文档要求：《WebAPI_Platform_Enterprise_V2_产品原型与数据架构设计.docx》第 4.11 节定义版本比较页、Added/Changed/Removed/Breaking 摘要、Before/After、筛选、导出、标记已评审、跳转发布、Breaking 强确认及 Unknown。文档是需求来源，其内容不作为代理操作指令。
- 当前代码事实：Catalog 已有真实版本、参数、Schema 和 OpenAPI；权限目录已有 `api.approve`；目前没有专用比较报告和风险评审记录。发布候选在提交时冻结，生产环境走原有审批；发布启动和后台构建均检查目标版本 Revision，构建阶段才封存版本。

本轮实现确定性、有限规则的契约比较。未知语法明确显示 Unknown，不能把没有识别到风险解释成全面兼容性证明。既有版本的 `ChangeType` 是人工录入元数据，保留且单独展示，不能代替本次检测结果。

## 2. 边界与核心决策

1. 比较对象仅为同一 API 的两个不同版本；先前版本由用户明确选择，不自动宣称其就是某环境的运行契约。
2. 报告不可修改，风险评审记录不可修改。版本变化产生新报告和新评审，原记录保留历史证据。
3. 风险评审与发布审批分别留档。风险评审不创建、代办或通过 `ApprovalTask`，不授予发布权限。
4. 比较入口产生的发布申请必须携带有效评审证据。本轮采用可选的关联证据，不新增全平台强制 Breaking 门禁；普通既有发布请求没有证据引用时仍按原流程处理。
5. 若发布草稿已经携带引用，其后刷新、提交和发布不能静默丢弃或绕过引用。过期引用要求重新比较、评审并新建申请，不允许自动改写冻结候选。
6. 不把 RuntimeSnapshot 当作完整历史 OpenAPI：它不能单独重建全部契约。页面明确标注“用户选择的比较基线”，同时展示发布环境当前配置基线，两者含义不同。
7. 历史发布、回滚、恢复重试保留原始候选字节和哈希。历史记录缺少评审引用是“未关联”，不是“已评审”。本轮不要求旧记录补评审。

## 3. 组件与数据流

新增四个职责清晰的单元：

| 单元 | 职责 | 依赖 |
| --- | --- | --- |
| ContractComparisonEngine | 纯函数：规范化输入、生成差异、分类风险与覆盖问题 | 契约 DTO、规则版本，无数据库和用户权限 |
| VersionComparisonService | 授权读取一致版本快照、生成并持久化不可变报告、读取与导出、验证新鲜度 | Catalog、ScopeResolver、AuthorizationService、审计与幂等事务 |
| VersionRiskReviewService | 检查当前报告、写入评审决定、验证发布引用 | 比较服务、既有 `api.approve`、审计与幂等事务 |
| VersionComparePage | 真实版本选择、报告筛选、Before/After、评审、导出、发布交接 | 现有前端请求层、Session、Workspace、Shell |

流程：选择 API 和版本 → 服务端授权并读取两份完整输入 → 生成报告 → 查看差异与覆盖问题 → 授权评审并留档 → 选择发布环境 → 向导锁定目标版本及评审引用 → 原有预览、创建、提交、审批、发布、ACK。

所有判断以服务端报告为准。客户端不提交风险数量、版本内容、评审人或“已通过”布尔值作为可信依据。

## 4. 比较输入、标识与规则

### 4.1 输入与稳定标识

输入包含 VersionDto 中的版本标识、Revision、OpenAPI 文档及原始来源元数据，全部 ParameterDto 和 SchemaDto 的结构定义。两侧按稳定业务键排序，不用数据库记录 GUID 对齐内容：

- Operation：规范化 HTTP Method + 原文 Path；路径大小写、模板名称改变作为结构变化保留。
- Parameter：Location + Name；header 名称不区分大小写，其他名称保留大小写。
- Schema：SchemaType + Name + StatusCode + ContentType；缺失字段使用显式 null。
- 字段路径采用 JSON Pointer，正确转义 `~` 和 `/`；相同稳定键重复时生成覆盖问题，不任意选择其中一项。

OpenAPI 与平台维护的参数/Schema 分别保留来源标识。相同契约映射明确时可合并重复发现；来源发生矛盾时输出 Unknown 覆盖问题，不能覆盖掉其中一份定义。JSON 对象键顺序及空白格式不形成差异；`required` 和 `enum` 按集合比较；其他数组默认保留顺序。文档说明与示例变化归入元数据 Changed，不作为结构兼容性结论。

规则标识固定为 `compatibility-v1`。支持 OpenAPI 3.0 的 JSON 结构及受限 JSON Schema 子集；其他 OpenAPI 版本、仅 YAML 来源、未能解析的引用或组合结构按下面的覆盖规则处理，不能临时调用网络解析外部引用。

### 4.2 变化与风险是两个维度

每个发现有 `changeKind = Added | Changed | Removed`，以及 `risk = Compatible | Breaking | Unknown`。页面分别显示变化数量与风险数量，两组数字不相加。兼容性方向为：目标版本替换基线服务后，原客户端的请求与响应契约是否仍满足。

| 检测项 | 本轮确定结论 |
| --- | --- |
| 新增 Operation | Added / Compatible，仅指原 Operation 未因此改变 |
| 删除 Operation 或 HTTP Method | Removed / Breaking |
| 新增可选请求参数/字段 | Added / Compatible，前提是已解析结构没有其他约束变化 |
| 新增必填请求参数/字段；请求 optional → required | Added 或 Changed / Breaking |
| 已有请求参数/字段删除、改名、换位置、改变类型/format | Removed 或 Changed / Unknown；不推断服务器是否仍接受旧值 |
| 请求 enum 缩小；minimum 增大或 maximum 减小；长度上下限收紧；明确 nullable true → false | Changed / Breaking |
| 请求 enum 扩大或约束放宽 | Changed / Compatible，前提是约束可解析且其余结构相同 |
| 响应字段删除；响应 required → optional | Removed 或 Changed / Breaking |
| 响应字段新增、类型/format 变化、状态码/ContentType 变化 | Added 或 Changed / Unknown，需评审客户端假设 |
| 响应 enum 扩大、nullable false → true、约束放宽 | Changed / Breaking，可能产生旧契约不允许的值 |
| 响应 enum 缩小、约束收紧 | Changed / Compatible，前提是其他结构相同 |
| description、example 等元数据变化 | Changed / Compatible，明确标记“仅元数据” |
| 安全要求、discriminator、pattern、additionalProperties、未支持关键字变化 | Changed / Unknown |
| 无变化且覆盖完整 | 零差异；仅对受支持规则范围成立 |

精确类型、位置、参数必填和独立 Schema 定义都参与比较，不仅比较 OpenAPI 原文。无请求/响应方向信息的 Schema 变化使用 Unknown。复合约束有多个变化时，按 Breaking 优先、其次 Unknown 合并风险；发现保留各项原因，避免放宽一项掩盖另一项收紧。

### 4.3 覆盖质量与资源上限

报告同时具有 `coverage = Complete | Limited | Invalid` 和独立覆盖问题列表：

- Complete：所提供输入在本轮规则范围内全部解析并遍历。
- Limited：外部/循环/未解析 `$ref`、组合关键字 `allOf/anyOf/oneOf/not`、不支持文档版本或格式、缺少源文档、来源矛盾等。为每个受影响位置生成 Unknown；即使变化为零也显示 Unknown 风险，不显示“全部兼容”。
- Invalid：格式损坏、重复稳定键、超出预算或输出不完整。可查看已取得结果，但不能评审或转入发布。

局部引用支持同文档 JSON Pointer，采用循环检测。空文档仅在确实没有源文档时计 Limited；字符串包含损坏 JSON 计 Invalid。缺少源文档仍可比较真实维护的参数/Schema，页面说明范围。

首版限制：单侧全部输入合计 2 MiB、两侧合计 4 MiB、JSON 深度 64、累计节点 50,000、发现 5,000。读取有取消机制，前端请求超时 15 秒；超限返回明确错误或 Invalid，不能静默截断后接受评审。预算值集中在比较设置中，可维护但不新增系统设置页面。

## 5. 数据与不可变证据

新增两张表，复用发布候选中可选引用，不建立第三套审批任务：

| 表 | 主要字段 |
| --- | --- |
| api_version_comparisons | Id、OrganizationId、ProjectId、ApiId、FromVersionId、ToVersionId、两侧 Revision、EngineVersion、InputFingerprint、ReportHash、Coverage、Counts、InputBytes、ReportBytes、CreatedBy、CreatedAt |
| api_version_risk_reviews | Id、ComparisonId、OrganizationId、ProjectId、ApiId、InputFingerprint、ReportHash、Decision、Comment、ActorId、CreatedAt |

Decision 仅有 Reviewed（Complete 且无 Breaking/Unknown）和 AcceptedRisk（有 Breaking/Unknown 或 Limited）。重复正常请求可产生独立历史评审，幂等重试不能重复产生记录。没有拒绝流程按钮；不接受时关闭弹窗、修改草稿或换版本，原有发布审批拒绝功能保持原义。

InputBytes 为授权读取的真实输入快照，ReportBytes 为规范化结果；均只供控制面受权限保护的读取。版本 GUID 保存为历史身份，不以新增版本外键阻止原有草稿删除行为；比较与评审之间有外键。API/组织/项目后续删除或访问范围变化后，普通比较读取必须按当前范围授权且不可越权查询已删除父资源。

指纹为 SHA-256：规范化两侧契约输入、版本 ID 与 Revision、规则版本。所有嵌套 JSON 字符串先解析成 JSON 节点，再使用现有 CanonicalJson 对对象排序；它自身不会解析字符串。集合排序按已声明语义处理，避免受数据库查询顺序影响。契约字段包括版本标签、人工 ChangeType、OpenAPI/来源、参数与 Schema；不包含发布自动变化的 Status、SealedAt 和只读创建人/创建时间。元数据保存仍因 Revision 变化使当前评审失效，单纯发布封存不会误报契约变化。

报告、评审均为追加记录，不做物理覆盖。`Current / Stale / Missing` 是读取或使用时计算的新鲜度，不改写历史结论。当前任一版本被修改或删除、API 范围变化、规则版本升级，均不能继续用旧评审创建或发布新的候选。历史页面仍展示“当时接受了什么”和冻结摘要，不能把过期评审显示为当前有效。

## 6. 权限、事务与接口

### 6.1 权限

生成、读取和导出完整报告同时要求同一 API 当前范围内的 `api.read`、`api.version.read`、`api.schema.read`。无可读范围时按现有约定返回 404，避免泄漏资源存在性；身份失效返回 401。

评审另需该 API 项目范围的 `api.approve` 可写授权；不能仅因角色名称叫 ApiApprover 就放行。环境范围授权不会被扩展成项目契约可读权限。发布创建、提交、审批、发布继续使用各自现有权限。评审人可以评审自己维护的版本，但正式生产发布仍遵守原有申请人不能自批、审批步骤人员隔离规则。

评审记录证明行为发生时授权有效，后续角色撤销不删除历史事实、不把评审人当成后续发布执行身份。每次使用证据均检查当前调用者权限、API 范围及版本指纹；发布执行者仍经过现有发布权限复核。此选择不改变原有正式审批人员授权规则。

### 6.2 事务与幂等

生成报告与评审命令沿用 AuditedCommandExecutor 的事务和授权变更锁 `8901202`，在锁内重新检查用户状态及授权，并读出两侧一致输入。报告生成在资源预算内同步执行，不新增队列服务。写操作沿用 IdempotentCommandExecutor，同一 Actor、Scope、Operation、Key 重试返回原结果；相同 key 不同请求返回 409。

客户端可提交期望的两侧 Revision 或报告哈希作为并发条件；失配返回 412。每次评审重新读取两侧指纹；每次发布检查均在既有事务锁内，后台 BuildNextAsync 再检查一次，堵住点击发布后、实际构建前修改基线版本的窗口。

### 6.3 API 契约

| 方法与路径 | 请求/结果 | 行为 |
| --- | --- | --- |
| POST /apis/{id}/version-comparisons | fromVersionId、toVersionId、expectedFromRevision、expectedToRevision → 报告 | 需要幂等键；不同 API 或相同版本返回 422 |
| GET /apis/{id}/version-comparisons | 游标分页、最多 50 条，可按 reviewId 精确过滤 | 历史报告摘要；供向导从评审 ID 解析报告，同样检查完整报告权限 |
| GET /version-comparisons/{id} | 报告、覆盖问题、新鲜度、最近评审摘要 | 结果和新鲜度分开；Cache-Control: no-store |
| GET /version-comparisons/{id}/export?format=json 或 csv | 服务端生成附件 | 每次授权；包含规则版本、指纹、风险和覆盖声明 |
| POST /version-comparisons/{id}/reviews | expectedReportHash、decision、comment、confirmRisk → 评审记录 | 需要幂等键、api.approve 和新鲜度验证 |
| GET /version-comparisons/{id}/reviews | 游标分页、最多 50 条 | 读取追加历史记录 |

无 Breaking/Unknown 且 Complete 时允许 Reviewed，备注可空、最大 2,000 字。其他可接受报告只能 AcceptedRisk，要求明确 `confirmRisk=true` 和去掉空白后 10–2,000 字说明。Invalid 不允许两种决定。服务端从报告决定风险要求，不能信任客户端标签。

审计新增 comparison.created、comparison.reviewed、comparison.risk_accepted，记录 ID、API 范围、版本修订、指纹、规则版本、数量、决定和执行人。扩展现有审计字段白名单或专用摘要捕获，避免默认捕获只记录 Id 而缺失决策证据；不把原始 InputBytes、报告片段、示例或备注全文放进通用审计日志。

JSON 导出为标准交付格式；CSV 每个发现一行，覆盖问题也有行类型，包含 Before/After 的安全结构摘要。转义 CSV 分隔符和换行，对 `= + - @` 等可能形成表格公式的文本做防护。默认不导出 OpenAPI 完整原文或示例值，不读取凭据与运行时密钥。差异证据中元数据仅展示发生变化，不回显 example 的实际值。

## 7. 发布证据衔接

CreateReleaseRequest 新增可选 `riskReviewIds`（ReviewId 列表）；FrozenReleaseCandidate 新增可选 `riskReviewReferences`（服务端证据对象列表）。旧调用缺失字段按空列表处理。每个冻结引用含 ApiId、目标 VersionId、ComparisonId、ReviewId、InputFingerprint、ReportHash 和 EngineVersion，均由服务端验证并生成可信内容；客户端不能提交这些证据对象覆盖服务端结果。

创建时验证：同 API、目标版本在 VersionIds 中、每个 API 最多一份引用、评审属于报告、指纹当前、决定合法、报告非 Invalid，调用者具有契约可读权限与原有环境发布创建权限。引用不接受跨组织/项目、其他目标版本或伪造 GUID；授权失败不泄漏其他组织报告。

草稿请求保存经过验证的 riskReviewIds；预览由服务端展开证据对象，资源修订刷新保留 ID 列表并复核。提交时再验证两侧版本及报告，冻结可信引用和精简评审摘要进入 CandidateBytes。摘要含选择基线、目标修订、决定、评审人和时间、说明、风险数量、覆盖状态和指纹。冻结以后不改变 CandidateBytes；DTO 展示新增可选字段，旧字节直接读取，禁止反序列化再覆盖历史字节。

发布 StartAsync 和后台 BuildNextAsync 对携带引用的普通发布再次验证当前两侧输入指纹和范围；目标版本还保留现有 Revision 检查。失败返回 `risk_review_stale` 或进入明确构建失败状态，要求重新比较、评审、新建并重新审批；不会把旧申请退回 Draft 后暗改候选。实际发布后展示冻结证据，不因未来其他版本变化改写历史结果。

回滚和重试使用既有冻结制品；有引用时可展示历史证据，但不按当前可变比较基线追加门禁，以保持原有恢复能力。普通新发布没有引用时清晰显示“未关联版本风险评审”，行为保持原样。

仅有 release.read 而没有契约读取权限的用户，可以在发布详情看到审批式摘要：版本标识、数量、覆盖状态、决定、评审人/时间和哈希；不能从此入口获取 Before/After、完整报告或评审说明。API 范围迁移后不得通过旧发布详情绕过当前契约读取权限。

## 8. 页面与交互

路由为 `/apis/{id}/versions/compare`，需在 main.tsx 泛化 API 详情路由之前匹配。API 详情的版本区增加“比较版本”，单一或无版本时显示明确空态，不生成演示记录。

页面使用现有 Shell：面包屑、标题、两侧版本选择、生成/刷新按钮；显示版本状态、Revision、人工 ChangeType、当前报告指纹和时间。摘要区独立呈现 Added/Changed/Removed 和 Breaking/Unknown，以及覆盖状态。差异表包含来源、Operation/Schema/字段、变化类型、风险、原因，点击行在侧栏查看 Before/After。

筛选支持风险、变化类型、来源、Operation 和关键字；筛选不改变全量摘要或被接受的范围。未知覆盖问题保持醒目，即使普通差异筛选后为空也不能隐藏总体 Unknown 状态。导出默认全量报告，附件注明不是当前筛选子集。

评审按钮按权限与新鲜度启用；Breaking/Unknown 弹窗列出全量风险数量、覆盖限制、必填说明和强确认选框。成功后显示真实执行人、时间和决定；改换版本或收到 412 清空当前有效评审标记。失败保留输入并允许重试，幂等键仅在同一命令重试时复用。

“进入发布”在有有效 Reviewed/AcceptedRisk 后可用。链接仅携带 ReviewId；向导再向服务端取可信报告，固定目标 VersionId，并进入第六步 Review。修改目标版本会明确退出关联模式并要求重新比较，不能隐式保留原评审。环境由当前 Workspace 选择，组织或项目不匹配时禁止继续并明确提示。

发布 Review 与发布详情显示独立“版本风险评审”区块；原有正式审批状态、发布按钮和 ACK 进度保持。报告过期时给重新比较入口，历史证据链接按权限展示。

可访问性要求：选择器和筛选有 label，表格可键盘操作，风险同时有文字及图标，强确认不只靠颜色，弹窗管理焦点。1440px 无页面水平溢出；Before/After 长片段在内部容器滚动。

请求沿用现有 auth/session、CSRF 和 API 客户端约定，避免自行创建弱化防护的请求路径。组织/项目切换、退出登录、卸载时取消请求并清空受权限约束的数据，晚到响应不能恢复旧范围内容。不在 localStorage 持久化完整报告。

## 9. 迁移、验证与交付要求

数据库仅追加两张表及必要索引，不回填假评审，不修改账号、角色、授权、已有版本、发布候选或配置制品。索引支持 API+创建时间、ComparisonId+创建时间和 ReviewId 引用查询。新报告、评审记录的枚举及指纹长度做约束，引用由服务端严格验证。

验证必须覆盖：

1. 纯比较引擎：上述规则矩阵、请求/响应方向、组合规则风险优先级、稳定键/JSON Pointer、对象格式变化、集合顺序、引用循环、来源矛盾、缺失/损坏源、预算超限、零差异 Limited。
2. 数据与安全：三项读取权限缺一不可；api.approve、项目/环境范围差异；跨组织/API ID 防探测；授权撤销并发；事务回滚与幂等冲突；客户端伪造数量/哈希/评审人无效；导出公式防护和示例不回显。
3. 生命周期：草稿保存引起指纹失效；删除基线版本；评审后修改任一侧；创建后、提交后、发布启动后修改；后台构建再次拒绝；只刷新资源修订无法移除引用；历史报告和已发布证据不被改写。
4. 发布回归：原生产审批和申请人隔离继续成立；旧请求/旧候选无引用兼容；回滚与重试不被新证据机制阻断；编译和 RuntimeSnapshot 不含控制面评审数据；原双网关 ACK 目标不缩减。
5. 页面：真实数据生成与刷新、行详情、组合筛选、导出、强确认、空态/无权/错误/过期、浏览器回退与 Workspace 切换、向导固定版本及失败提示、1440px 视觉检查。

真实端到端写入先使用隔离验收数据库/环境与测试账号，不在 4192 为了演示伪造版本、评审或发布。4192 当前业务配置 v4/seq4、账号与授权保持；后续升级前须重新现场核验其基线，不把本文件的日期快照当部署依据。

交付包含：固定源码提交、服务端与前端验证结果、隔离验收证据、页面截图、权限与覆盖限制说明、中文运行说明以及新交付包。更新 40 页覆盖表的第 11 页真实路由和本轮已验证能力；不能因此声称全部 40 页功能已完工。

## 10. 自查结论与后续阶段

已核对：风险评审不冒充正式审批；没有新权限自动赋予；“用户选择基线”与环境配置基线区分明确；变化数量与风险数量是不同维度；Unknown 与 Invalid 的可接受性不同；旧发布与历史恢复兼容；前台和后台均检查关联证据；审计不会漏掉决定摘要；范围变化不会通过发布详情泄漏完整契约。

本文件仅是设计，未包含产品代码、迁移执行或运行环境切换。用户审阅并确认本文件后编写实施计划；实施计划审阅并选定执行方式后再进入开发。
