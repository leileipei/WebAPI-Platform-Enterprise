# 第4包账号安全与凭证治理收尾设计规格

日期：2026-10-15。状态：**完整设计方向已获认可；本书面规格待用户审阅，产品实施尚未开始。**

源码基准 `226dfe450865cd322d81eb5747bec791a71f7519`；当前 main 为 `a30ce5bf8291539eac2640ea02839b403ccd68c8`，产品源码一致。原 4192 已安装 226dfe4，当前维护入口 Ready。第2包保持暂停。

## 1. 已批准意图和交付范围

用户要求按既定工作包继续收尾，先选首次改密 A，再选 Credential 统计 A，并认可整体设计方向。

- 首次改密 A：升级迁移保留现有本地账号登录行为，新建本地账号及全新安装管理员首次登录必须改密；SSO 密码由 IdP 管理。
- 统计 A：记录最近使用及按环境的认证与 API 授权通过次数；上游错误、后续限流或缓存命中仍计一次；内部重试不重计；错误、过期或无权限请求不计。
- 补齐原第34页本地密码重置、第20页轮换与最后有效凭证风险提示、第18/19页摘要、监控及审计入口。保持原审批发布、范围权限及工作/运行配置区别。
- 以原40页形成逐项实现和验收台账，不将“存在页面入口”作为整页交付证明。

本规格不授权源码提交、推送、合并或4192安装，也不授权性能矩阵。写入新设计和需求索引不覆盖原本地改动。原阶段的已完成发布、导入、Schema、JWT/重试/缓存和通知功能继续沿用，不重建。

## 2. 固定源码事实和原需求来源

原 Word 为 `WebAPI_Platform_Enterprise_V2_产品原型与数据架构设计.docx`。其4.1明确首次强制改密，4.19要求Overview/调用KPI/审计，4.20要求Last Used/轮换/最后有效凭证风险，4.34要求显式重置本地密码。

[40页原文索引](../../evidence/work-package4/2026-10-15-readiness/original-40-page-requirements.json)包含454条组件、操作、状态与权限核对单元及原文段落、文档Hash。454不是已验收数量，也不包括全系统业务章节的每一约束；本包验收还须纳入Word第6.4凭证轮换流程及相关全局安全规则。

当前UserRecord无首次改密字段；认证接口只有csrf/login/logout/me；用户更新契约不含密码重置。Credential已有LastUsedAt列和DTO，Gateway认证成功后没有使用上报，凭证表未显示Last Used或轮换。运行快照已有Credential ID，可沿用2.0–2.3格式。

`docs/console-coverage.md`含历史“后续”“4192未安装”描述，仅作为线索。已有契约补全、通知审批及第3包的新证据可以支持其实际覆盖范围，不可把旧证据改标当前源码或当作本包验收。

## 3. 账号数据和迁移

UserRecord增加`must_change_password boolean NOT NULL DEFAULT false`与`password_changed_at timestamptz NULL`。旧账号全部保留原password_hash、security_stamp、status、角色及范围；不从CreatedAt、旧Hash格式或未知登录历史推断“首次”。旧时间为空显示“历史未记录”。

本地账号新建和真正首次bootstrap设置must_change_password=true；initialized=true的原运行目录不重新bootstrap。SSO用户始终false且PasswordHash=null，禁止本地重置。

IdentityDto增加`authSource`及`mustChangePassword`，UserDto增加标记供用户管理显示。普通会话字段及旧工具的现有身份字段保持。登录成功返回待改密身份时permissions/scopes为空，不返回完整授权菜单；业务角色和范围记录不被清空。

PasswordChangedAt、SecurityStamp更新属于已授权改密事务。自动迁移不得把现有4192管理员标为待改密。后续只有显式密码重置可改变已有用户密码及标记。

## 4. 改密会话与服务端边界

本地密码校验继续原登录限速和失败/成功审计。待改密账号使用最长10分钟、非持久且不滑动续期的受限Cookie；若系统SessionTtl更短则取较短值。普通本地及SSO会话TTL保持既有设置。

只有`/api/v1/auth/csrf`、`/auth/me`、`/auth/logout`和新的`POST /auth/change-password`可由受限会话使用。每次业务请求都读取当前用户状态、改密标记和SecurityStamp，后端统一返回403 `password_change_required`；不得仅依赖前端跳转或Cookie旧标记。匿名登录/SSO入口和节点Bearer内部接口保持其独立原协议，不给受限用户额外管理能力。

所有业务授权入口（含直接服务调用的AuthorizationService、集合权限查询和导出）同样防止待改密用户读取或写入业务。用户在普通会话期间被重置后，旧SecurityStamp会使原Cookie立即按既有会话校验失效，不依赖浏览器刷新。

`POST /auth/change-password`只操作当前认证用户，不接受目标UserId。请求最大32KiB，含currentPassword、newPassword、confirmPassword；按原始字符串校验，不自动trim或Unicode改写。要求当前用户Active/local，确认字段一致，新密码符合当前PasswordMinLength/PasswordComplexity且不同于旧密码，长度最大1024。

校验与写入在原治理事务锁和用户行锁内串行化，重新读取标记/Hash/Stamp。成功写Hash、清除标记、更新PasswordChangedAt/UpdatedAt/Revision、更新SecurityStamp，并在同一事务写脱敏审计；提交成功后废止旧Cookie并签发普通新Cookie，再返回现有完整Identity。审计失败则回滚，不能已改密码但回报失败。并发请求只允许一个旧Stamp成功。

改密使用独立purpose的原HMAC/Redis限速实现，阈值复用可配置的登录IP/账号预算；不共享登录计数键。Redis保护故障返回503、限速429含Retry-After，旧密码错误采用明确的已认证改密错误，不暴露其他账号。密码、确认值、Hash及Stamp不进入日志、审计Before/After、Outbox或持久重放响应；审计记录动作、目标、自身/管理员、修订、结果和TraceId。

改密不缓存任何可再次签发会话的成功响应。需要Idempotency-Key以统一请求校验，但会话/Stamp重新校验优先于重放；网络丢失后使用新密码重新登录，不能用旧Cookie重放成功获取新会话。回滚后可安全重试，密码成功写入不因Cookie传输失败回滚。

## 5. 管理员显式重置

新增`POST /users/{id}/password-reset`，沿用平台级user.write、数据范围、If-Match和幂等保护。管理员提供符合当前策略的临时密码，接口仅返回用户修订及标记，不回显密码。禁止SSO、停用用户和管理员重置自己的密码；自助改密走上一节。支持重置唯一Active本地管理员的密码，但不能移除其角色/范围或停用账号，该账号仍能本地登录并完成改密恢复管理能力。

事务内复验目标、当前管理员非待改密状态、ETag和唯一本地管理员约束。成功更新Hash/Stamp/标记/Revision，所有旧会话失效，写审计`user.password.reset`。密码请求最大16KiB；审计字段白名单明确增加MustChangePassword与PasswordChangedAt，仍排除PasswordHash、SecurityStamp及所有密码输入。不得绕过LocalAdministratorGuard去同步改角色或状态。

幂等规范化输入只保存目标ID、预期修订及临时密码的目的域HMAC指纹（使用原私有login-protection-hmac秘密，包含动作/Actor/目标/修订），不得把原密码或其未加密SHA指纹交给持久化重放。复用原IdempotentCommandExecutor对这一非秘密规范化输入查重，不新增通用密码账本。幂等记录只保存成功状态及资源修订，无临时密码/密码Hash/Stamp；每次请求先重新授权，ETag过期不回放旧成功，禁止把目标ID或密码差异折叠为另一人的重置结果。实际主机的bootstrap-password等维护文件不由Web接口更新；重置提示明确要求管理员自行维护私有运维凭证。原4192账号不执行此动作作为测试。

## 6. 统计语义

统计名称为“凭证认证通过次数”，不是后端成功数、计费账单或HTTP尝试数。记录点位于ApiKey模式完成Secret/状态/有效期与原API授权校验之后、next之前，使用该请求冻结的RuntimeGeneration/RuntimeCredential ID；即使随后配额拒绝、超时或上游错误仍计一次，缓存命中计一次，代理重试不会再次经过记录点。

CORS预检、IP/CORS在认证前拒绝、无效/过期/已发布撤销凭证、API无授权、JWT/匿名模式均不计Credential使用。JWT请求即使夹带X-API-Key也不计。路径/JSON改写不改变授权归属。

LastUsedAt是节点观察到的认证通过时间，迟到报告以GREATEST单调更新。统计写入不改变Credential或Application的Revision/UpdatedAt、工作/冻结配置、权限窗口、快照Hash或发布状态。历史工作已撤销但旧已发布快照仍允许的实际使用须记录，不能用当前工作状态过滤历史事实。

已有LastUsedAt不被清空；不得从无凭证维度的旧Loki/Prometheus样本反推。JWT与Anonymous继续现有应用监控。所有时间用UTC；服务器对超出两分钟的未来时间拒绝并标记ClockSkew，过去24小时内的真实补报可以写入其原观察小时。

## 7. Gateway有界采集和落盘

部署配置命名空间`CredentialUsage`，不作为可直接通过系统设置UI修改的业务参数。默认Enabled=true，FlushIntervalSeconds=15（5–60）、CheckpointIntervalMs=1000（100–5000）、MaxInMemoryKeys=10000（100–20000）、MaxPendingBytes=64MiB（8–128MiB）、MaxBatchEntries=1000（1–1000）、MaxBatchBytes=256KiB（16–256KiB）、MaxPendingAgeHours=24（1–24），启动时严格校验。

有界键包含环境、Credential、ApiId、匹配的冻结授权窗口(validFrom/expiresAt)、源发布ID/ConfigVersion/DeploymentSequence/Hash和UTC小时。记录原认证时实际匹配窗口，重叠窗口沿用原有效窗口判定选择，不增加新的授权条件。认证线程仅TryRecord并增加本地计数，不做网络、数据库或fsync；容量/磁盘故障不改变API认证结果，产生明确统计损失状态和指标。

独立后台任务把活动增量写到现有owner的LKG卷下`credential-usage/`；路径固定，不接受HTTP提供文件名。采用同一私有目录的临时文件、fsync、原子rename和父目录fsync；当前累计、已封批次、CollectorId、nextSequence、持久水位及未结gap一起原子保存。单节点只有一个写者。启动严格核对环境、节点名及文件身份，文件损坏保留诊断、不覆盖，然后登记gap并新建采集epoch。

最后一次已fsync到崩溃之间的易失尾部可能丢失，因此不承诺exactly-once全量调用账本。正常停止先封批/落盘；非正常退出重启会将上次durableThrough至新实例启动之间标为可能缺失。gap单独最多1MiB持久控制记录不受普通数据队列满影响，按小时/原因合并；控制预算耗尽时把受影响小时合并为粗粒度Unknown并记录GapSummaryLimit，不静默丢掉缺报标志。控制记录仍落盘失败则采集源为Unavailable，绝不报Complete。

封批时分割不超过1000项、256KiB、最多2个源快照；每项Count为1至1000000的整数，包含firstObservedAt/lastObservedAt。没有调用也以items=[]发送覆盖水位；每15秒提供连续采集区间，使完整无流量窗口可被证明，而不是依赖数据批次存在。单次发送超时3秒、最多一个在途请求，原请求流不等待。失败指数退避1至30秒带抖动；不循环重试阻塞应用停机。超24小时或容量不足的待报数据不静默覆盖：保留有界失败摘要，登记受影响时间gap，释放最旧数据再继续；不是把损失变成0次。

持久CollectorId不随Gateway重启重置，Sequence从1起单调递增。单批核心载荷封存后不变，成功ACK才移除文件。24小时内原批次可重传，同时间窗口多批均为增量；恢复后不得重封已经封存的同一增量。

## 8. 内部统计协议1.0与实例补报

保持现有register/heartbeat/desired/ack签名，新增独立内部入口，不向旧NodeHeartbeat强加字段：

- `POST /internal/v1/nodes/{nodeId}/credential-usage/collectors`：当前实例认证并绑定CollectorId，返回服务端nextExpectedSequence和已接受水位。
- `POST /internal/v1/nodes/{nodeId}/credential-usage/batches`：提交严格1.0批次，包含当前senderInstanceId、原producerInstanceId、collectorId、sequence、封批时间、已持续采集区间/gap及items。

沿用节点Bearer秘密与NodeIdentityService，校验Enabled和绑定环境。发送者必须是当前已注册实例；历史生产者只能来自该node/collector已登记实例，或持久registered事件证明属于同节点的实例。新实例注册后可上传其已落盘旧生产者批次，但不能声称另一个节点/环境/未登记实例的事实。离线LKG新实例在CP恢复后先完成现有注册和collector绑定，再补报其有界24小时内数据；未能证明身份的区间保持Partial。

请求最大256KiB、JSON深度8；未知/重复字段、空GUID、非1.0协议、重复条目键、非法小时/计数/时钟均拒绝。senderInstanceId不参与核心内容Hash，允许恢复后的当前发送实例转送原生产者的同一封存内容；其余规范化字段及items参与Hash，项目/组织不由客户端声明。

CP验证每个源发布的环境、ConfigVersion、DeploymentSequence、Snapshot Hash及Credential/App关联，凭证确在该不可变快照里可用且有原API授权。item包含ApiId、原匹配的validFrom/expiresAt及firstObservedAt/lastObservedAt；该窗口须确在冻结快照内，且最早/最晚观察均在凭证与此授权窗口交集内，hourStart须与观察小时一致。不能仅凭“该凭证现在存在”，也不能把不同授权窗口合成跨越未授权时段的一项。每个batch最多2个历史快照，上报处理并发默认2（1–4）、最大处理预算10秒；不得逐条数据库往返或全表读取管理列表。

Node/实例在事务内重新校验并使用短时行锁防止并发退役。Collector游标FOR UPDATE，事务原子完成去重、小时增量、LastUsedAt、来源水位及Batch回执。相同key+相同核心Hash重传返回AlreadyApplied，不重复计数；相同key不同Hash为409；新序列必须等于expected，乱序返回409和expected，不跳序。各节点/collector互不共享序列。

24小时有效重放期内保留回执Hash；序列小于或等于持久水位且回执已过期的请求返回410 Retired，不再计数、不声称Hash相同。若恢复了较旧LKG导致local nextSequence落后服务端已分配水位，先隔离冲突材料、登记回退区间gap并建立新CollectorId，不能覆盖旧游标或把新调用误当旧批次重放。去重游标不因清理统计桶被删除。

## 9. 持久化、完整性和保留

增加以下派生表，不删除业务表或改冻结候选。具体EF迁移命名及锁定模型在计划阶段固定，表职责不能合并为无界JSON账本。

| 表 | 主键/唯一键及职责 |
| --- | --- |
| credential_usage_collectors | nodeId+collectorId；acceptedSequence、内容身份、协议、最新水位和关闭状态 |
| credential_usage_collector_instances | nodeId+collectorId+instanceId；来源登记和起止，支持当前实例上传旧生产者批次 |
| credential_usage_batches | nodeId+collectorId+sequence；核心Hash、处理时间、确认结果；24小时重传去重 |
| credential_usage_hourly | environmentId+credentialId+hourStart；Count bigint、LastUsedAt；稀疏非零小时桶 |
| credential_usage_coverage | environmentId+nodeId+hourStart；完整采集区间、gap、持久/已上报水位、源状态 |

对于不同源快照/API的同凭证小时项，CP验权后汇总到同一环境桶，不重复计数外部请求。数据库Count加法须检查bigint溢出，溢出则原子拒绝、登记受控源故障，不能回绕或截断。所有范围查询先授权，SQL读取量随页/时间范围有明确边界；本包不对暂停性能包作验收声明。

默认Hourly/Coverage保留30天（可部署配置1–90），BatchHash默认25小时；其部署保留期必须至少比Gateway最大补报期长一小时（可配置2–168小时，组合非法启动失败）。清理每次最多1000行、有界事务；不删除未超过补报窗口的来源实例或活跃Collector游标。关闭collector的轻量序列墓碑保留到节点记录显式删除，已删除节点拒绝认证后按FK受控删除；保留LastUsedAt，不把清理当统计归零。

各环境完整性按请求窗口内应在岗的节点群体判断。新能力注册、在岗/退役、Collector区间和gap形成明确来源记录；没有旧群体/旧协议记录的历史区间为Unknown/Partial，不能仅按当前在线节点推断过去完整。旧节点无1.0来源或任一节点延迟/未闭合/重启丢失区间时整窗Partial。

范围请求用UTC整点小时边界，最多30天且不超过实际RetentionDays；非整点、越保留期或过长返回422并说明可选区间，不静默截断。默认界面为最近24个完整UTC小时；当前小时单列“进行中/截至最近上报”，永不标为完整。

状态：Available表示窗口完整并有计数；NoData表示窗口完整且0次；Partial返回已采集次数及missingNodes/gaps/freshness，明确这是下界；Unavailable/Unknown返回null计数并给出来源原因。没有完整覆盖的历史空值只能写“尚无可用采集”，不显示“从未使用”。

## 10. 管理查询和权限

新增`GET /applications/{id}/credential-usage`，带environmentId可选、UTC from/to、page/pageSize；默认50、最大100。回包含Credential身份摘要、窗口认证次数、窗口内LastObservedAt、可见环境最近使用、来源状态、coverageStart/completeThrough、实际窗口和采样含义。Count使用规范十进制字符串，避免JS超过2^53精度损失；新统计是无采样事件汇总，但可能有gap，不能以OTel采样率解释丢失。

首先要求app.read及应用范围；每个环境再次要求app.read范围，并从可信关联解析组织/项目。显式不可见环境返回403/404；汇总只纳入当前可见环境且说明“可见环境合计”，不泄漏其他环境是否有流量。Credential详情中最近使用按可见环境投影；保留的全局实体LastUsedAt不能直接作为受限视图来源。

监控KPI复用metric.read和现有Prometheus按applicationId过滤；日志/Trace/审计分别继续独立权限，缺少权限时隐藏入口，不把原app.read扩成全部监控或审计权限。应用摘要和凭证次数使用app.read，不新增秘密读权限。没有在管理列表增加每条Credential的独立SQL往返。

## 11. 应用、凭证和轮换页面

应用列表显示“当前可见有效授权API数（跨环境去重）”与“当前可用/总凭证数”；准确标注工作配置。可用凭证为Active且validFrom<=now<expiresAt。应用详情保留原三个Tab，并补Overview、Metrics和Audit入口；现有编辑Owner/停用流程保持。

Overview显示应用状态/Owner、可见授权和凭证摘要、最近观察使用、运行环境/版本及按独立权限可见的KPI。Metrics复用当前应用过滤和源状态。Audit复用可信资源过滤（应用及其凭证/授权事件），不在浏览器全量查询后筛选；无法从旧审计定位创建者/撤销者时显示历史未记录，不猜CreatedBy/RevokedBy。

凭证AK默认仅展示后四位及掩码，不把Secret混入列表/URL/下载/审计。一次性API Key继续只在创建成功的内存Modal展示，关窗后不提供再次读取。表格增加Last Used、认证次数、统计窗口与状态；当前小时与完整历史分开，权限不足或历史缺报显示原因。

轮换向导步骤：选原凭证→填写新凭证有效期及重叠窗口→创建并一次性保存新Key→选择需使用的环境并发起已有发布→等待原审批规则及真实当前双节点确认→提示消费方迁移→观察旧/新凭证采集→显式撤销旧工作凭证→再次已有发布和节点确认。没有自动发布、自动撤销、自动延长授权或消费方已迁移的伪造状态。

轮换期间无关API/App继续保留在发布候选，沿用原并发修订/共享凭证影响和跨环境发布规则。只有目标环境的已发布新Key以及其有效窗口才算线上替代；仅创建未发布不能视为替代。缺报/Partial或当前小时延迟不能证明旧Key无人使用；仍允许有权限管理员在充分提示后显式作业务撤销决定。

最后有效凭证影响预览同时计算当前工作状态和每个可见环境已发布状态，不把工作变更当已生效。对当前无替代、窗口形成断档、旧Key正在使用、未发布新Key给出明确风险。保存/撤销需If-Match、影响摘要Hash及资源修订复验，风险变化返回409要求重新看确认；确认允许用户主动关闭全部访问，不创建额外企业审批引擎。当前Snapshot实际影响仍只在现有审批发布后生效。

## 12. 默认安全和兼容部署

旧账号迁移默认false，原角色/范围/Hash/Stamp不变；新功能测试使用独立owner和新账号，不重置原admin或本机维护文件。旧维护工具/长期夹具若首次创建管理员，必须显式完成改密后才能管理；受限会话不是自动豁免脚本入口。新版夹具自己保管新测试密码，长期升级不得覆写原秘密。

新CP读取旧2.0–2.3快照；新Gateway消费相同快照，用新增独立1.0来源协议采集。新CP配旧Gateway来源为不完整；新Gateway配旧CP遇新增端点404/不支持时继续API/LKG与有界落盘，不把统计故障变成业务失败，最大24小时后依上述gap处理。统计协商先单独进行，不让旧heartbeat未知字段阻断原注册。

冷备增加各原LKG卷下的usage目录及新派生表，本来LKG卷归档不能漏这些材料。恢复另一个owner必须重绑定collector身份/来源水位，禁止用克隆队列报告到原Control Plane；当前owner完整恢复需校验DB/LKG游标一致。异常优先兼容修复前进，旧软件回退需另验：不得忽略新的MustChangePassword安全语义，让旧二进制绕过待改密限制。

## 13. 40页需求关闭规则与已知矛盾

454条索引逐项增加固定实现路径/符号/提交、功能测试ID、实际场景/QA材料、验证版本、状态和保留原因。状态使用已关闭、部分、缺失、待复验、经确认延期；所有已关闭项须有与声明范围一致的验收事实，历史旧图不能代替新逻辑运行。

本包核心改动覆盖1、18、19、20、34，以及认证/发布/节点/观测/审计/设置交叉安全回归。其他页仍需逐项对照并登记已有固定证据，新增未覆盖需求不能随意宣称已完成；超出已批准方向的功能先形成差异供用户确认，不自行扩架构。

原4.18“停用立即影响数据面”与已批准的“工作配置经Snapshot审批发布生效”存在解释差异，本包沿用后者，在页面明确待发布事实，原要求保留为业务决策项，不暗中加入管理数据库即时鉴权。用户曾暂缓六步向导第5步整合Cluster/Destination，此项继续暂缓。日志/Trace/外部通知、企业IdP/TLS/HA、容量及CI签名等分别关联已交付范围或第5包/独立环境，不据本包一并关账。

## 14. 必须验收的场景

| 编号 | 正向、反向与恢复门禁 |
| --- | --- |
| P4-01 | 空库新管理员受限→改密→普通管理；旧库迁移现有用户不强制、密码/角色/范围保持；SSO不设本地密码 |
| P4-02 | 直接业务HTTP/集合查询/导出绕过均拒绝；csrf/me/logout/change允许；强弱密码、并发改密、审计/Redis故障、Cookie丢失恢复、旧Stamp拒绝 |
| P4-03 | 显式用户重置权限/ETag/幂等/SSO拒绝/唯一管理员、重置后旧会话失效和新临时登录改密；全链路实际秘密扫描 |
| P4-04 | 有效ApiKey+授权计一次；401/403/JWT/匿名/预检不计；上游503、缓存Hit、重试3次仍只计一次；旧快照合法迟到使用不被工作状态丢弃 |
| P4-05 | 双网关批量累计、重复相同Hash、Hash冲突、乱序、退役/未知实例、跨组织项目环境/凭证/快照伪造全部按契约处理 |
| P4-06 | 正常/崩溃重启、CP断网、ACK丢失、落盘错误、容量/24h过期、LKG游标回退、冷备恢复；已持久不重计、易失/gap不伪完整 |
| P4-07 | 时钟偏差、BIGINT溢出、小时边界、清理与补报并发、30天窗口、权限过滤和受限LastUsed不泄漏；统计不改业务修订或Snapshot字节 |
| P4-08 | 双Key重叠→独立审批发布→消费方测试→观察→显式撤销→第二次发布；双节点确认、最后有效窗口、影响412/409和无关业务保留 |
| P4-09 | 应用数量/Overview/Metrics/Audit与权限、Partial/Unavailable、AK脱敏/一次性Secret、1440px键盘/焦点/离开保护 |
| P4-10 | 40页454单元逐项证据/关闭或保留原因；来源新旧身份分开，原4192数据、密码、main本地文件及第2包暂停保护 |

执行真实PostgreSQL、原Redis保护及隔离双Gateway场景；基于固定Git构建完成回归、1440px实际操作及独立审查。测试日志和先前失败保留，不能把候选绿测换成固定发布身份。本包不启动正式性能矩阵，不用假执行器证明实际上报/重启/采集完整性。

## 15. 规格自检与下一阶段

规格固定了改密覆盖、受限接口、统计记录点、小时窗口、30天保留、24小时补报、批次去重/游标、重启尾部gap、角色范围及原Snapshot兼容；设计中的收集状态不能用于业务鉴权。原文矛盾及未证实的40页条目明确保留。

用户审阅本规格后才编写实施计划。实施计划须分解认证、来源协议/存储、查询/页面、轮换和逐页收尾，列出精确迁移/源码/测试及保护边界，选择当前会话执行方式。新固定提交、合并推送及4192部署不包含在这次设计选择中，需要对应具体审阅包。
