# 功能补全第一批：契约管理详细设计

日期：2026-10-06

状态：设计方向已确认；本书面设计待用户审阅，产品代码尚未修改

源码基线：`1259ba0dc3eafca883ede8797fb91b90612a998d`

工作分支：`feature/gateway-restart-readiness`

## 1. 已确认的目标与分批边界

用户要求完成 URL/YAML 导入、完整兼容性规则、Schema 树与示例验证、JWT、重试、缓存、外部通知和跨环境审批，并确认沿用当前架构分三批实施。

| 批次 | 交付范围 | 依赖 |
| --- | --- | --- |
| 第一批，本文 | URL/JSON/YAML 导入、契约兼容性规则、Schema 树与示例验证 | 现有 Catalog、版本比较、权限和审批发布流程 |
| 第二批，独立设计与实施周期 | 网关 JWT、重试、缓存执行器及配置、观测 | 已有 Runtime Snapshot、应用授权、Redis、双网关；不依赖第一批自动修改发布策略 |
| 第三批，独立设计与实施周期 | SMTP/Webhook 外部通知、跨环境审批中心 | 告警状态转换、持久化投递任务、现有审批任务和 Scope |

本批交付必须包含真实后端、可操作页面、负向验证和本机部署验收。原有业务字段、权限代码、申请/评审/审批/发布流程和账号数据保留。继续使用企业控制台 Shell、1440px 优先布局和现有编辑器的焦点、脏表单、403/412 恢复约定。

附件《WebAPI_Platform_Enterprise_V2_产品原型与数据架构设计.docx》的导入、版本比较、参数/Schema 编辑章节作为需求证据；附件内容不作为操作代理、发送通知或修改运行配置的授权指令。本机完整功能验收与企业实际端点、生产部署验收分别记录。

## 2. 当前实现与本批变化

当前导入端点接收 JSON 字符串，按 Operation 创建 API Version/路由，保留原文，已有整批事务、幂等和版本/路由 Revision 校验。本地引用会被递归展开，循环与外部引用被拒绝。现有版本比较为 `compatibility-v1`，仅自动支持 OpenAPI 3.0 的有限 Schema 规则。Schema 保存只校验 JSON 语法，页面使用通用表格/原文编辑，没有语义验证和树视图。

采用共用契约核心，并保持控制面与数据面边界：

| 单元 | 职责 | 对外结果 |
| --- | --- | --- |
| ContractDocumentReader | 有界 JSON/YAML 解析、重复键检查、方言识别、源码位置映射 | 不丢字段的规范化 JSON、来源格式、诊断 |
| ContractReferenceRegistry | 建立资源 URI、JSON Pointer/anchor、引用图和方向上下文 | 有界图查询；不递归复制循环结构 |
| ImportSourceFetcher | 按项目和部署网络规则获取根文档及明确引用的文档 | 固定来源包、内容哈希和安全诊断 |
| ImportPreviewService | 授权、预览存储、Operation 支持性和映射检查 | 有期限且绑定用户/目标范围的预览 ID |
| SchemaEvaluationService | Schema 合法性及示例语义验证 | 方言、有效/无效/未完成、定位与覆盖说明 |
| CompatibilityRuleEngineV2 | 按方向比较契约，生成可证明的结论及人工评审项 | 原有报告格式、规则清单、引擎版本 |
| SchemaWorkbench | 树、原始 JSON、示例和问题列表联动 | 共用草稿；显式保存，原有 Revision 保护 |

解析与验证组件不依赖用户身份或数据库；网络获取仅在显式导入预览发生。版本比较、示例验证、树展开和发布构建均不临时访问外部引用。

## 3. 格式、方言和资源预算

### 3.1 接受的输入

- 根契约支持 OpenAPI 3.0.x 和 3.1.x，接受 UTF-8 JSON、YAML 文件或粘贴文本；URL 入口支持同样内容。其他主/次版本明确拒绝，不使用 `StartsWith("3.")` 作为支持性检查。
- 3.0 使用该版本的 Schema Object 语义；3.1 使用 OAS 3.1/JSON Schema 2020-12 方言。显式 `$schema` 只接受已登记的这两类语义，不支持的自定义方言列为诊断。独立维护 Schema 可显式选择方言；旧记录优先继承关联版本的 OpenAPI 方言，无文档的旧记录按历史 3.0 语义显示并允许用户在未封存草稿中明确设置。
- JSON/YAML 保留未知字段和扩展字段，不用 DTO 反序列化再序列化去除它们。JSON 重复属性和 YAML 重复 mapping key 拒绝，YAML 只允许字符串 mapping key 和 JSON 兼容值。根契约检查 OpenAPI 必需结构、Operation 与 Response 等对象形状；不能仅能解析成 JSON 就显示契约合法。
- YAML 采用有界事件/节点处理，不做 CLR 类型构造。支持单文档、常用锚点/别名及 merge mapping；重复键或合并结果冲突有明确诊断。拒绝自定义对象标签、多文档流和循环别名；展开后按同一节点/字节预算检查。布尔、数字、null 使用确定的 JSON 兼容标量规则，日期及其他普通标量作为字符串，不做浮点精度有损转换。
- 原始来源保存为文本，`SourceFormat` 为 `json` 或 `yaml`；`OpenapiDocument` 始终为规范化 JSON。版本原文保存路径也要兼容 YAML，不能继续对 YAML 来源执行 `JsonFields.Validate`。

解析依赖选用 YamlDotNet；语义校验采用 JsonSchema.Net，通过方言适配器和受控资源注册表使用。实施计划负责选择与现有 .NET 10 兼容的固定版本、核对许可证并更新锁文件，禁止未经批准运行后动态下载元 Schema 或远程引用。

### 3.2 默认预算与超限处理

| 项目 | 默认上限 |
| --- | --- |
| 根文档原始/规范化字节 | 各 2 MiB；规范化后同样检查 |
| 一份来源包 | 根及引用文档共 16 份，各 2 MiB，合计 4 MiB，规范化包也限 4 MiB |
| 解析结构深度/总节点 | 64 / 50,000，含别名展开及引用处理 |
| 导入 Operation 数 | 1–1,000，沿用现有整批事务规则 |
| URL 网络获取 | 整次预览网络阶段 15 秒；每次连接上限 5 秒；无自动重定向 |
| 预览有效期与配额 | 20 分钟；每用户每项目最多 5 份有效预览；每项目最多 100 份 |
| 示例请求 | Schema 及已存来源包总计不超过 4 MiB；示例 256 KiB；500 条诊断；执行上限 5 秒 |
| 版本比较 | 单侧全部输入 4 MiB、双侧 8 MiB、50,000 节点、64 深度、5,000 发现、10 秒执行预算 |
| Schema 树 | 分批显示；单次展开不超过 500 个节点，引用循环显示引用入口 |

预算由部署配置集中定义，运营可收紧；项目配置只能在部署上限以内收紧。超限、取消或超时不产生“校验通过/兼容”结论，也不能从不完整结果创建正常评审。不能先无界解析再做大小校验。前端大小预检用于反馈，服务端为准。

## 4. URL 获取、项目来源配置与固定预览

### 4.1 网络边界

每项目维护导入来源规则：允许的 origin、路径前缀、是否允许登记的内网 CIDR、较小资源预算、Revision。默认没有 URL 允许项；文本/文件导入仍可使用。来源规则读取使用 `project.read`，修改使用项目范围 `project.write`，采用既有 `If-Match`、审计和幂等模式，不新增或放宽角色权限。规则表独立于现有只允许 system scope 的系统设置表。

允许项必须是精确 scheme/host/port，路径按 URI 规范化后的段边界匹配；不支持通配 host 或模糊字符串前缀。公网默认只允许 HTTPS；HTTP/内网必须同时获得部署级允许和项目级登记。本机验收只登记独立契约源测试服务，不开放平台管理端、数据库、Redis 或任意 localhost 端口。

认证与目标项目/环境/集群检查先于 DNS/网络获取。禁止 URL userinfo、query 和根 URL fragment，拒绝非 HTTP(S) 协议。暂不提供来源 URL 认证凭据输入；需要身份验证的来源可先导出文件导入，后续凭据接入要走 SecretReference 而非密码拼接 URL。

DNS 解析后检查全部候选地址并固定连接目标，TLS 保留原 host/SNI 和证书验证；禁用代理环境变量和自动重定向。内网允许项仍受部署限制；元数据、保留/特殊地址及管理服务明确拒绝。IPv4/IPv6、映射地址、混合解析结果、DNS rebinding、压缩后超限及慢速响应均有负向测试。响应只按正文内容和声明格式解析，不执行脚本，不信任 MIME 类型作为唯一判断。

### 4.2 预览与提交

新增会话式导入端点，原有 JSON `import-preview`/`import-commit` 请求结构保留：

1. 前端选择文本/文件/URL、目标环境和集群，提交 `POST /api/v1/openapi/import-previews`。输入包含现有三个目标 ID，以及互斥的 sourceText/sourceUrl 和格式提示。
2. 服务端授权后解析根文档，在同一网络边界内收集明确引用的外部资源，保存不可变来源包。返回 PreviewId、SourceHash、BundleHash、Dialect、ExpiresAt、来源规则 Revision、Operation 清单、警告和原文行列诊断。
3. 前端勾选 Operation，逐项映射新 API、已有 API 新版本或现有草稿；能查看“支持/不可导入”的具体原因。预览结果显示来源格式、摘要和有效期。
4. 确认时调用 `POST /api/v1/openapi/import-previews/{id}/commit`，只提交 ExpectedBundleHash 和沿用现有 `ImportTarget` 的映射列表。提交不重新获取 URL，不接受客户端替换预览内容。
5. 提交重查用户、Scope、来源规则 Revision、集群、草稿/路由 Revision、目标归属和冲突；规则变化或预览过期要求重新预览。用户无权查看其他用户预览，统一按不可见资源处理。
6. 沿用 `AuditedCommandExecutor`、`IdempotentCommandExecutor` 和整批原子性。预览进入 Committed 状态与业务写入同一事务；授权与请求哈希校验后，相同幂等请求优先重放保存的结果，预览内容清理后仍可重放。不同请求或不同幂等键复用已提交预览返回冲突，不重复导入。保留绑定用户/Scope/目标 ID 的提交元数据至幂等回执到期，用于重放前重新检查当前目标权限；草稿 Revision、到期及状态检查只在真正新写入时执行，不能使一次已成功提交的同键重试产生第二次写入。

文本、文件、URL、目标范围或集群改变均使前端预览失效。网络/解析/冲突失败保留用户输入与映射。取消仅取消当前操作，不丢掉草稿。导入仍只写工作定义和默认 API Key 路由，不自动授权、审批或发布。

旧端点继续接受原有 JSON 请求，并可通过共用读取器接受 YAML 文本；旧端点没有 URL 参数，不进行网络获取。对于没有 base URI 和附带文档的文本外部引用，明确要求使用会话式多文件导入或 URL 预览，不猜测网络地址。

## 5. 引用和契约来源数据

### 5.1 引用图

支持同文档 JSON Pointer、本地 anchor、3.1 `$id` 基址、外部相对/绝对资源和递归 `$ref`。引用解析采用 URI/指针索引及访问对缓存，保留循环图，不把递归类型无限展开。3.0 Reference Object 与 3.1 Schema `$ref` 邻接关键字分别按方言处理。

文件入口可附带有相对文件名的 JSON/YAML 文档集合，不接收 ZIP、不读本机绝对路径、不允许 `..` 越界或重复逻辑名称。URL 来源包只能获取允许规则覆盖的明确引用，每份资源获取一次；任何引用失败时相关 Operation 显示不可导入，不能静默移除引用后变成成功。

保留 `components` 以及受支持参数、请求体、响应、header、security scheme 的引用上下文。多 examples 保留并提供选择；parameter.content、范围响应码/default、组合 Schema 支持准确表示。未纳入运行转换的 style/explode/encoding 等信息保留且明确提示：契约导入不会自动创建网关转换策略。callbacks/webhooks 等非当前入站路由模型的内容保留原文并单独提示，不凭它们创建普通路由。

### 5.2 增量表与版本元数据

| 数据 | 主要内容与生命周期 |
| --- | --- |
| `project_import_source_policies` | ProjectId、规则 JSON、Revision、UpdatedBy/At；无行表示 URL 未启用；精确项目 Scope |
| `api_import_previews` | Id、Organization/Project/Environment/ClusterId、ActorId、SourcePolicyRevision、原文/规范化来源包、哈希、格式/方言、CreatedAt/ExpiresAt、状态、ImportId 和已提交目标 ID；过期清理内容，提交元数据保留至幂等回执到期，限制配额 |
| `api_version_contract_sources` | ApiVersionId、来源包 JSON 和哈希、逻辑 URI/来源格式清单、方言、来源来源规则版本；版本删除时级联，已封存版本不可改 |

来源包作为单个有界聚合保存，无需每个引用建立业务资源。原文可能含敏感业务示例，只有既有版本/Schema 读取权限才能读取；审计和公共验收材料只记录哈希、条数、错误码及无凭据的来源标识，不输出完整原文或示例值。

旧版本不回填/重写来源，也不重算旧 SchemaHash。无来源包的旧 JSON 版本以原有文档为唯一注册资源。导入、版本编辑、Schema 编辑均在同一事务内更新草稿 Revision 和相关来源元数据；不能存在 `OpenapiDocument` 已变而来源包仍伪装匹配的情况。直接替换根文档会使旧外部来源包失效，要求重新导入或提供完整来源包。

独立 Schema/参数定义仍是维护来源，与原 OpenAPI 分别保留，不自动覆盖原始契约。新增的树编辑只改变所选维护定义。比较继续报告两者矛盾；手工修改不能静默覆盖导入依据。未导入而直接维护的 Schema 方言及根 URI由版本契约元数据记录。

## 6. 兼容性规则与“完整”的验收定义

本批“完整”指下表契约层和 Schema 规则族全部进入规则目录、遍历、分类及验收测试，前后端能展示结论与覆盖问题。它不表示任意 Schema 表达式都可自动证明包含关系。示例通过也不能证明两个 Schema 兼容。

### 6.1 统一判断方向

以目标契约替换基线服务且原客户端契约继续成立为目标：请求要求“基线允许的输入集合包含于目标允许集合”；响应要求“目标可能输出集合包含于基线允许集合”。联合约束按完整节点判断，不把单个属性变化当成完整证明。

结论有三种：有充分包含证明为 Compatible；有能够由校验器复核的反例或明确契约删除证据为 Breaking；其余为 Unknown。可以保留多条局部发现，但父节点未知/冲突不能被子节点 Compatible 遮盖。生成反例只作充分证据，找不到反例不作兼容证明。元数据差异单独标注。

| 规则族 | 纳入的范围和明确策略 |
| --- | --- |
| Operation 与路径 | Method+Path 稳定键；新增/删除；模板命名、路由歧义及 Path Item 引用变化有独立发现 |
| 参数 | 位置、名称、header 大小写、必填、schema/content、style/explode/allowReserved；计算默认值后比较序列化语义，未证明的转换 Unknown |
| 请求体 | 必填性、媒体类型、Schema、encoding；新增必填 Breaking，媒体类型删除根据接受集合判断 |
| 响应 | 明确/default/范围状态码优先级、媒体类型、header/必填性、Schema；覆盖关系而非只比较字符串键 |
| 安全要求 | security 的继承及空数组匿名、OR 分支/AND scheme、OAuth scope 集合；可证明更严格的请求要求为 Breaking，认证实现假设无法判断为 Unknown |
| 类型/空值 | primitive、integer/number、3.1 联合 type/boolean schema、3.0 nullable；采用真实方言语义及类型集合 |
| 枚举与常量 | 深层 JSON enum、3.1 const、集合包含，包含数字精度/对象规范化；不在合法方言中的关键字有诊断 |
| 数字 | minimum/maximum、exclusive 边界、multipleOf；精确有限数值运算，超出可证明范围 Unknown，禁止浮点误判 |
| 字符串 | min/maxLength、pattern、已登记 format；正则完全相同可证明相同，变化需反例或人工评审；匹配有执行预算 |
| 对象 | properties、required、additionalProperties、patternProperties、propertyNames、min/maxProperties；按开放/封闭对象真实语义处理字段删除和新增，不只看 properties 名单 |
| 数组 | items、prefixItems、min/maxItems、uniqueItems、contains/min/maxContains；按方言识别元组与普通数组，纳入组合约束 |
| 组合与条件 | allOf、anyOf、oneOf、not、if/then/else、dependentRequired/dependentSchemas；解析和验证完整支持，有限可证明情况自动判断，不能证明包含关系的变更 Unknown |
| 评估后的属性/元素 | unevaluatedProperties/unevaluatedItems；处理真实 annotation 上下文，不能降级成 additionalProperties/items；复杂变更 Unknown |
| 引用 | 本地/外部 `$ref`、anchor、`$id`、递归与 `$dynamicRef`/`$dynamicAnchor`；比较固定包，动态上下文变化无法证明时 Unknown；损坏引用 Invalid |
| 请求/响应注解 | readOnly/writeOnly 结合方向和 required；discriminator/mapping 独立诊断；format assertion 模式变化纳入指纹 |
| 文档、扩展和内容注解 | title/description/examples/XML/链接、x-扩展、contentEncoding/contentMediaType/contentSchema；保存与定位完整，纯已知注解为元数据，自定义行为语义未声明时列覆盖问题 |

规则目录为每条记录 RuleId、方言、输入方向、自动证明条件、Unknown 条件、测试用例 ID。验收必须显示逐规则覆盖，不用测试总数代替规则覆盖率。3.0 非法使用 3.1 专属约束须报错，不“升级解释”。跨方言版本比较先做明确语义适配；无法无损适配的节点 Unknown。

报告沿用 Added/Changed/Removed 与 Compatible/Breaking/Unknown 两个维度。Coverage 沿用 Complete/Limited/Invalid：Complete 仅表示目录内所有输入已被有效处理，不等于全 Compatible；Limited 包含无法自动证明、未登记语义或来源缺失；Invalid 包含损坏契约、失效来源哈希及超预算/中断等不完整结果。Unknown 和 CoverageIssues 必须计入摘要和评审门禁，不得出现“0 风险但有未处理约束”。

### 6.2 引擎升级与历史证据

新引擎固定标识 `compatibility-v2`，输入指纹包括解析/验证适配版本、方言、固定来源包哈希、维护定义及其 Revision 和 format 模式。报告仍不可变；历史 v1 报告、评审、发布候选的原始字节及哈希不得重写。

新建评审与带证据的新发布申请必须使用当前 v2 指纹。升级后旧未提交的证据草稿显示过期，并要求重新比较/评审后新建申请；不能静默删除引用。已经提交的冻结候选及历史回滚/恢复继续按既有冻结证据规则校验，不能仅因当前引擎版本改变而阻塞已批准发布或重算候选；同时继续执行已有的真实资源 Revision/权限/前置条件检查。旧未关联证据的常规发布路径保留原流程。本批不新增全平台“Unknown 必须拒绝”的业务规则：Limited/Unknown 沿用明确 AcceptedRisk 强确认，Invalid 禁止评审。

源码核查发现，当前 `ValidateReferencesAsync` 会通过当前引擎版本判断历史评审是否新鲜；直接替换引擎常量会使升级前的冻结申请失效。因此明确拆分“新证据要求当前版本”和“冻结证据按已登记历史版本复核”两个校验入口。保留 v1 原输入形状及指纹算法，以同一输入和现有资源一致性验证 v1 冻结证据；新增来源元数据不能混入 v1 序列化。历史引擎版本只允许已登记且有回归测试的 v1/v2，不接受客户端任意指定版本。冻结证据仍检查报告/输入/评审/候选字节哈希、真实资源 Revision、当前授权与目标归属；不以“历史”绕过这些检查。PublishCoordinator 的启动和 Worker 构建两处均使用冻结校验入口。

## 7. Schema 树、编辑与示例校验

### 7.1 页面与保存

参数/Schema 页提供“结构树 / 原始 JSON / 示例验证”联动区域，响应按状态码和 ContentType 切换；保留现有参数表、用途、名称、说明、示例和所有 IDs。树支持属性、数组、组合分支、引用入口、必填、类型、约束和请求/响应标记；JSON Pointer 正确转义，重复名称不能跨父节点误定位。

树与原文共用一个草稿模型，结构编辑以 Pointer 补丁操作，保留未修改字段、关键字和引用；不把全部 Schema 转成只支持数个字段的表单。常用属性的新增/删除/改名同步维护父级 required，但跨文档引用不自动全局改写。存在其他引用的删除/改名在服务端返回受影响位置，要求明确处理后才能保存。

原文有语法错误时保留文本，结构编辑暂停并显示行列位置；不能自动恢复成上一份 JSON 后保存。树只展开有界部分，引用循环显示目标入口，不无界展开。已封存版本、只读角色可以查看树与验证结果，不能写入草稿；只读结构与元数据加载错误时不能显示保存入口。

显式保存继续使用现有批量参数/Schema PUT、版本 `If-Match` 和幂等规则。结构合法性在服务端检查；新保存的损坏/非法 Schema 或失效引用被拒绝。历史非法定义仍可读并显示诊断，不迁移时删改。示例不匹配不会阻止保存草稿，但显示明确问题，并保持已有发布业务规则；本批不擅自增加示例必过的发布门禁。

### 7.2 只读校验端点

新增 `POST /api/v1/versions/{id}/schema-validation`：ExpectedVersionRevision、SchemaId 或 ParameterId、可选 DraftSchemaJson、ExampleJson、Direction、FormatMode。必须关联服务端查到且属于该版本的定义；新增草稿允许明确的 Draft 标记和有界 Schema 输入。继承版本方言及来源包，客户端不能指定网络地址或替换外部包。

权限使用版本范围 `api.schema.read`。读取权限允许在内存验证临时 Schema/示例，不保存版本、Schema 或示例。先权限校验再取源包，版本已变化返回 412，客户端保留输入并提示刷新。

结果为 Valid/Invalid/Incomplete，返回 Dialect、EvaluatedVersionRevision、SchemaHash、ExampleHash、FormatMode、CoverageIssues 和 Issues。问题包括 instancePointer、schemaPointer、keyword、code、message、可得的原始行列及方向；返回语义理由，不回显示例敏感值。缺少引用/预算耗尽/不支持方言返回 Incomplete，不能显示通过。

FormatMode 默认为 Annotation，保留 JSON Schema 默认语义；可切换 Strict 以校验已登记 uuid/date/time/date-time/email/hostname/ipv4/ipv6/uri 等标准格式。未登记格式在 Strict 下列覆盖问题，不伪称通过。模式不写入业务配置，页面和结果明确显示，比较按固定的规则配置产生可复现结果。

校验支持 JSON 示例；非 JSON 媒体类型展示“当前示例验证仅覆盖 JSON”，不假装验证 XML/二进制。OpenAPI 多 examples 的引用和值均列入选项，externalValue 不自动网络获取，需导入时明确取得来源或显示未验证。

## 8. API 兼容、错误和运维

新增来源规则：`GET/PUT /api/v1/projects/{id}/import-source-policy`，GET 无记录返回禁用规则与可用于首次创建的 Revision 0，PUT 需要 `If-Match` 并按旧 Revision 原子更新。导入页仅有授权用户显示来源配置入口，使用既有 Editor；普通开发人员可看允许范围而不能修改。

会话预览另提供 `GET/DELETE /api/v1/openapi/import-previews/{id}`，限创建人当前有效 Scope；DELETE 撤销待提交预览，并在清理时释放内容。清理工作在现有 Worker 中有界执行，删除已过期/已提交满 20 分钟的内容；已提交行的最小授权/目标元数据待幂等回执到期后清除，已清理内容不可通过 GET 读取。幂等回执按原有保留机制管理。配额在数据库事务内约束，并发不能突破配额。

错误按现有 ApiException 结构返回：413 大小超限；422 格式/方言/引用/禁止来源；409 路由冲突/已提交/预览过期或政策变化；412 定义 Revision 不匹配；429 预览配额；502/504 获取失败/超时。未授权资源按现有 403/404 不泄露约定处理。日志仅包含可公开错误码、Scope、TraceId、哈希及字节数；不写原文、访问凭据或示例。

迁移仅新增表和 nullable 来源元数据，不重写历史表数据。迁移在隔离 PostgreSQL 克隆验证；添加后旧源码仍能启动读取旧数据。若已产生 YAML/来源包新数据，完整软件回退需要保留支持新格式的兼容构建或隔离恢复冷备到新实例，不能声称旧版本对新数据功能等价。回退程序不得把旧冷备覆盖到仍有新业务数据的原实例。

4192 升级继续固定提交构建、冷备与克隆恢复验证。验收发布新契约只在隔离克隆；原环境只安装软件，不自动创建新来源允许项或发布测试 API。原有文档改动、冻结验收材料、账号/SSO Secret、维护工具和历史交付包继续保护。

## 9. 验收矩阵

| 编号 | 必须通过的证据 |
| --- | --- |
| C01 | 同一 OpenAPI 的 JSON/YAML 导入结果语义相同；原文/格式保留；文件、文本、URL 三入口可操作 |
| C02 | 重复键、非法 tag、多文档、别名爆炸、Unicode、数字精度、超限和取消均确定拒绝，失败保留输入 |
| C03 | URL 获取对允许项成功；未授权用户零网络请求；重定向、混合 DNS、重绑定、IPv6、特殊地址、正文超限和慢响应拒绝 |
| C04 | URL 在预览后改变：提交导入仍为预览包；哈希篡改/来源政策变化/过期/其他用户不可提交 |
| C05 | 本地、附带文件、外部、相对、anchor、递归引用正确；缺失/非法引用有位置；递归树不会挂起 |
| C06 | Operation 勾选、目标映射、3.0/3.1、parameter.content、多 examples、范围/default 响应；不支持路由模型的内容有提示 |
| C07 | 真实数据库整批原子性、重复提交幂等、并发版本/路由 412、归属/授权撤销、配额竞争、清理后幂等重放 |
| C08 | 每个兼容性规则族都有请求/响应方向、放宽/收紧、无变化、组合交互和 Unknown 用例；规则 ID 与测试一一可追踪 |
| C09 | Broken/Compatible 的正向证据和反例经同一校验器复核；没有反例不得自动判兼容；新增可选字段遇封闭对象能识别风险 |
| C10 | JSON Schema 官方测试集固定提交离线运行，2020-12 各已登记词汇、引用及动态引用覆盖；OAS 3.0 方言有独立适配测试 |
| C11 | 树/原文双向同步、特殊 Pointer、组合分支、循环、未知字段保留、元数据编辑、412/403/脏表单和封存只读 |
| C12 | 示例成功/错误/未完成；必填、枚举、组合、条件、格式开关、readOnly/writeOnly 方向、多 examples；错误定位且无样本值泄漏 |
| C13 | v1 历史证据字节及哈希保持；v2 新证据指纹变化有效；旧未提交证据过期，已冻结候选的发布/回滚/恢复流程不因引擎更名失效 |
| C14 | 1440 与 1280 页面导航、导入→Schema→比较→风险评审→现有发布向导关键交互；真实 403/412 和键盘焦点 |
| C15 | 固定提交构建、后端/前端检查、隔离克隆迁移/端到端验证、备份恢复、4192 服务健康；原账号、数据与历史包保护证据 |

测试执行失败、未执行或覆盖受限须如实列出，不把旧批次测试结果当作本批证据。代码完成后请求独立审查，修复实际问题，再提交合并与安装；仅在本批全部要求通过后标记第一批完成，后两批继续独立设计与实施。

## 10. 设计依据

- 原产品设计文档和本仓库 Catalog/Comparison/SchemaEditor 源码，核对基线见文首。
- [OpenAPI 3.0.3 Schema Object](https://spec.openapis.org/oas/v3.0.3.html#schema-object)：方言适配依据。
- [OpenAPI 3.1.1 Schema Object](https://spec.openapis.org/oas/v3.1.1.html#schema-object)：2020-12、引用、注解语义依据。
- [JSON Schema 2020-12 Validation](https://json-schema.org/draft/2020-12/json-schema-validation)：实例校验与 format 模式依据。
- [JSON Schema 官方测试集](https://github.com/json-schema-org/JSON-Schema-Test-Suite)：固定提交、离线注册远程测试资源和逐用例验收依据；标准实例测试不能代替平台兼容性包含判断测试。
- [OWASP SSRF Prevention](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html)：获取边界、允许名单、DNS 和重定向控制依据。
- [YamlDotNet 官方仓库](https://github.com/aaubry/YamlDotNet) 与 [JsonSchema.Net 官方文档](https://docs.json-everything.net/schema/basics/)：候选解析/校验依赖，实施前固定版本及锁文件。

## 11. 自查与下一阶段

已检查范围拆分、格式与历史证据兼容、权限/网络边界、只读校验与写入差别、预算和错误处理、默认 format 模式、来源包生命周期及验收编号。本设计无待定占位；产品代码、数据库、4192 和业务配置未改动。

本文件获用户审阅确认后进入实施计划编写；实施计划需要列出精确任务顺序、改动文件、固定依赖与验证命令，并由用户审阅和选择执行方式。之后进入第一批编码与验收。
