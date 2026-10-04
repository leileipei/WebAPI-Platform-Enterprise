# 实施过程记录

以下为按时间追加的原始实施记录。早期pending与阻塞均属历史状态；当前完成状态以 verification.json 和 final-review.md 为准。完整裁定另见 ../../decisions.md。

# SDD ledger — plan: docs/superpowers/plans/2026-10-04-core-loop-implementation.md
用户已批准设计、实施计划及Native执行（回复1）；所有15任务连续执行，不重复询问方向。
Ruling: 新建enterprise独立Git仓库及feature/core-loop分支，保留prototype原目录 — 原目录无Git，批准的计划已明确独立工程 — 若错误隔离成本为调整仓库布局，不影响原型。
Ruling: 计划标题由中文“任务N”改为“Task N”以兼容技能任务提取脚本 — 任务内容和用户范围不变 — 若错误仅影响任务提取。
Pre-flight: 1→2/3/4：DbContext和源表32张；API Version无环境字段，Route关联环境，一致。
Pre-flight: 2→3/4/5/6/8/11：ActorContext/ScopeRef/ResourceRef授权；使用实际资源解析，不信任客户端Scope，一致。
Pre-flight: 3→4/5/6/8：审计事务executor；幂等executor必须加入同一事务，不另开事务覆盖，一致。
Pre-flight: 4/5→6/7：版本及关联资源、凭证窗口；冻结发布资源不引用最新可变对象，一致。
Pre-flight: 6→8/11：审批与状态机；重试协调记录需新sequence而非恢复失败记录，一致。
Pre-flight: 7→8/9/10：RuntimeSnapshot/Envelope exact bytes；JSONB不能重序列化取Hash，一致。
Pre-flight: 8→9/10/11：PG期望事实/Redis缓存/冻结实例/序号；需保障事务指针与Outbox同提交，一致。
Pre-flight: 9→10/11/14：ACK与真实节点身份；ACK仅应用后发送，不使用模拟事件，一致。
Pre-flight: 10→11/15：LKG/单generation请求/回滚递增sequence；YARP激活失败不得成功ACK，一致。
Pre-flight: 2–11→12–15：DTO与浏览器API、CSRF/ETag/状态反馈；未接接口页面不得模拟成功，一致。
Task 1: in progress. BASE 1e055ce。目标：容器Release编译及32张核心源表+补充表真实PostgreSQL迁移/约束测试。
Task 1: Ruling: 尚无行为的Gateway/Worker/console项目延后至其拥有的任务建立，不放运行时占位服务 — 测试基础工程仅建Domain/Contracts/Infrastructure和Integration — 若错误需提前补项目配置，不影响接口。
Task 1 RED tests written: FreshDatabaseMigrates(缺核心表), DuplicateApiVersionRejected(缺唯一约束), SnapshotRawBytesRoundTrip(缺原字节存储), MigratorRunsTwiceWithoutDataLoss(缺幂等迁移)。Context仅空签名，待真实RED后写迁移。
Task 1 RED observed: 4/4 tests failed; FreshDatabaseMigrates expected32 actual0，其他3缺核心关系。日志task-1-red.log，真实PG可用及NuGet restore成功。
Task 1: Ruling: EF映射从已批准源字段机械生成，约束后缀另入同迁移 — 避免手抄32表遗漏且保留逐实体类型 — 风险为未标注字段可空性，已在模型文档与测试中明确。
Task 1: 版本/归属RED已观察：版本唯一1失败3通过，跨组织和Scope环境归属2失败，方法路径冲突1失败（Expected exception but none）。依赖冲突定位Relational10.0.4与Design10.0.12，补显式同版本引用，不忽略警告。约束改为集中Fluent映射进入模型快照，消除SQL后缀与EF模型漂移。
Task 1: complete (commits 1e055ce..b8a329d, tests: ./scripts/check.sh integration → Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 1 s - WebApi.Integration.Tests.dll (net10.0))
Task 1: 独立Migrator在本项目数据库执行两次成功；幂等SQL在新隔离数据库执行两次通过；SDK锁定Restore与Release build 0warning0error。
Task 2: in progress BASE b8a329d。RED真实Kestrel HTTP端点404，4失败；环境配置错误已纠正后才计业务RED。组织Scope沿源文档access_mode使用read/read_write；平台初始化管理权限可创建首组织，业务权限仍必须有实际Scope。
Task 2: complete (commits b8a329d..fb6af55, tests: ./scripts/check.sh integration → Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14, Duration: 2 s - WebApi.Integration.Tests.dll (net10.0))
Task 3: in progress。RED真实HTTP6失败（治理端点404），随后实现。Ruling: 首期跨组织用户注册表 / 停用 / 整体角色和Scope分配由平台管理员管理，组织资源及组织自定义角色仍按Scope授权 — 全局用户可能跨多个组织，单一组织不能停用其他组织账号 — 若需要组织成员治理，应新增限定组织的成员关系命令，当前避免越权。Ruling: 关键写入使用统一PG事务授权锁，将实际授权检查和角色/Scope变更串行化 — 保证事务提交时授权不陈旧 — 首期控制面写吞吐受限，后续可按授权版本细化锁。
Task 3: 源角色字典RED为空字典1失败，GREEN九源角色显式幂等种子；Scope并集不扩宽组织、进行中发布项目/环境停用均测试通过。完整24通过，locked Restore/Release build零警告错误。
Task 3: complete (commits fb6af55..b74f1f8, tests: ./scripts/check.sh integration → Passed!  - Failed:     0, Passed:    24, Skipped:     0, Total:    24, Duration: 4 s - WebApi.Integration.Tests.dll (net10.0))
Task 4: in progress BASE b74f1f8。RED10真实HTTP测试失败（端点404）。Ruling: 简化首期路由语法为整段命名参数及末尾命名通配符，同形参数和大小写等价规范化 — 与ASP.NET匹配保持一致且避免未验证约束语法 — 若需内联约束/可选参数将另补验证；静态始终优先于参数和通配符，priority在各类型内生效。Ruling: 每条新路由持久化API Key认证绑定，显式匿名需额外policy.write — 满足基础认证绑定并默认安全 — 后续策略扩展须复用绑定，不重复生成认证事实。实际运行版本只在所有在线启用节点一致时返回公共版本，其他情况null，不把desired pointer伪称实际运行。
Task 4: 结构空子项额外RED预期422实际500（2失败），统一RequestValidationFilter修复；全部36测试通过。新增API Key绑定Policy及基础timeout字段，无环境字段增加到Version。
Task 4: complete (commits b74f1f8..2d254ca, tests: ./scripts/check.sh integration → Passed!  - Failed:     0, Passed:    36, Skipped:     0, Total:    36, Duration: 8 s - WebApi.Integration.Tests.dll (net10.0))
Task 5: in progress BASE 2d254ca。RED8真实HTTP测试失败（导入与凭证端点404）。Ruling: 每个导入Operation对应独立Version，已有Version仅可对应一条当前Operation；已有Route覆盖需显式ID及revision — 参数/Schema表没有operation_id，避免把不同Operation参数混在同一个版本 — 完整多Operation同版本建模另行扩展，不谎称支持。已有Route认证绑定保留；新Route默认ApiKey。幂等导入按任务5原计划在任务6公共执行器完成后接入并回归。
Task 5: 7/8最初业务GREEN；Secret测试暴露IPAddress默认JSON序列化访问IPv4.ScopeId异常。追加真实Audit HTTP RED后以AuditDto显式字符串修复，保持存储inet类型。测试审计脱敏仅序列化安全文本列，避免测试直接序列化实体。应用DTO标WorkingConfiguration，不一概冒称Pending或已生效，实际版本另列。
Task 5: Path Item本地引用RED422随后解析支持，新增SHA256精确摘要及正/负验证断言；完整46通过，locked Restore/Release build零警告错误。AuditDTO IP字符串修复包含独立真实HTTP回归。
Task 5: complete (commits 2d254ca..54e56b4, tests: ./scripts/check.sh integration → Passed!  - Failed:     0, Passed:    46, Skipped:     0, Total:    46, Duration: 7 s - WebApi.Integration.Tests.dll (net10.0))
Task 6: in progress BASE54e56b4。RED7真实HTTP失败，审批端点404和导入幂等缺失。Ruling: Production flow固定两级顺序1/2，每级required_count允许1到5，任何人最多占一个审批席位 — 保留源字段并落实独立性 — 默认每级1人，较高人数需要更多独立账号。Ruling: CandidateBytes内部含凭证摘要，ReleaseDTO仅投影安全视图而不返回hash — 详情不暴露凭证hash约束同样适用于发布预览 — 后续Snapshot公共接口也须脱敏。
Task 6: 7业务测试GREEN；审批模板接口RED404后可配置并验证冻结策略不变；完整54通过。Draft记录服务端完整resource revisions，Submit检查完整集合。注意任务8：如果Version内容在Submit后变更，发布必须处理同一VersionID首次发布不可变性，不可覆盖用户新编辑；建议明确冲突而非悄悄回写。后续待补Source Username email格式创建（当前Governance.Validate按Code限制，源用户示例允许email用户名）及公共审计按trace/resource筛选，留给真实UI联调覆盖。
Task 6: complete (commits 54e56b4..46870f4, tests: ./scripts/check.sh integration → Passed!  - Failed:     0, Passed:    54, Skipped:     0, Total:    54, Duration: 9 s - WebApi.Integration.Tests.dll (net10.0))
Task 7: in progress BASE46870f4。RED14领域测试NotImplemented；GREEN15（另加共享Cluster隔离）。Ruling: RuntimeCluster用native SourceId +实际配置内容派生运行ID — 选中API更新共享Cluster时，未选择API必须保留旧后端配置 — 协议含SourceId供治理关联；未来Policy扩展应同样避免共享配置附带影响。Ruling: 空路由的有效Snapshot允许用于撤除最后路由，仍需有效版本/协议 — 不能让禁用最后Route永远无法发布 — 无LKG首启仍不Ready，空有效版本返回404。
Task 7: complete (commits 46870f4..12eb354, tests: ./scripts/check.sh domain → Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 39 ms - WebApi.Domain.Tests.dll (net10.0))
Task 8: taken BASE12eb354, brief read. 尚未写Task8测试或业务实现。实施要点：需补PublishRequestedBy/PublishTraceId持久字段及迁移，Worker重新授权实际Publish发起人（不能误用RequestedBy申请人）；Ready→Building入队时不移动pointer，Worker编译后同事务保存Snapshot/targets/pointer/sequence/outbox/audit。Version在Submit后改内容不能回写覆盖，应409/Failed明确冲突；Source用户名允许email及audit筛选UI联调待补。授权撤销在Building队列时必须阻止pointer移动；提交后Outbox重发属于已提交事实，需明确ledger Ruling授权提交语义，不能凭之后撤权悄悄回退事实。Runtime请求单generation建议Task10用YARP IHttpForwarder +已批准简化路由语法直接匹配以避免异步Provider切换导致auth混用，实际Task10brief为准。
Task 8: Ruling: 授权线性化点为Worker锁内复核及PG事务提交，提交后的Outbox重试完成已授权事实 — Building撤权不得移动pointer，提交后不能偷偷撤销目标或破坏PG与Redis一致性 — 如需撤回已提交目标必须明确发起新的经授权协调记录。Ruling: 全部启用节点都必须在线且至少2个，构建冻结所有实例 — 缺节点不降低ACK目标 — 较大部署中需要先显式停用已退出节点才能发布。Ruling: Submit后Version内容revision改变则发布409或构建Failed，不覆盖草稿 — 首次发布版本需要与审批内容一致且不可变 — 必须重新提交审批。新增PublishRequestedBy/PublishTraceId迁移明确记录实际发布人；申请人与发布人分离。
Task 8: 初始3 RED缺发布组件，首轮GREEN3；补充真实Redis崩溃/租约重试、旧Outbox、2^53以上seq、不同发布人撤权、启动缓存补偿、离线集合、Version冲突共9通过；完整PG63通过。锁定restore + 全解决方案Release build 0warning/0error；增量迁移及幂等SQL双执行回归通过。节点为隔离DB准备集合，未冒称真实网关ACK。
Task 8: complete (commits 12eb354..8ebe216, tests: ./scripts/check.sh integration → Passed!  - Failed:     0, Passed:    63, Skipped:     0, Total:    63, Duration: 13 s - WebApi.Integration.Tests.dll (net10.0))
Task 9: Ruling: 节点注册以显式Nodes:Enrollments环境/名称/SecretFile绑定，不提供通用注册秘密；摘要唯一且库内停用不能被配置重新注册绕过 — 无默认节点身份、不能用同密钥跨环境注册 — 部署前需配置每节点独立文件，密钥轮换需显式运维流程。Ruling: 进程重启生成新instanceId，旧冻结发布目标保持不变，不把新实例填入旧ACK席位 — 遵守冻结实例身份 — 发布中重启需新恢复协调记录。Ruling: Publish:AckTimeoutSeconds由Worker显式配置，DeadlineAt在发布PG事实中持久化；API入口自身检查截止时间 — 未引入源后续system_settings模块 — 配置变更只影响后续构建，不延长已有发布。心跳不充当ACK；负ACK或超时保留真实已应用状态，不自动恢复。
Task 9: 初始RED缺NodeContracts及端点；13真实HTTP测试GREEN，覆盖独立注册、两ACK成功一次、错误实例/hash/seq、停用旧请求、跨环境、重启冻结、负ACK、超时接口准入、部分失败封存、并发ACK及心跳不可捏造版本。完整PG76通过，全方案Release build零警告错误。DuplicateAckAfterWorkerRestartIsHarmless本阶段新DI scope/无内存状态验证；真实进程重启在Task15补齐。
Task 9: complete (commits 8ebe216..5d6c766, tests: ./scripts/check.sh integration → Passed!  - Failed:     0, Passed:    76, Skipped:     0, Total:    76, Duration: 18 s - WebApi.Integration.Tests.dll (net10.0))
Task 10: taken BASE5d6c766，brief已读。采用真实MapReverseProxy + IProxyConfigProvider，按YARP实际匹配Route metadata acquire同generation认证；不采用先前建议的手写路由/IHttpForwarder捷径。切换短暂阻止新请求进入路由选择（准入门释放于取得generation lease之后，不等待后端旧请求），generation级Route/Cluster ID避免旧集群引用被原位替换；旧lease完成后回收。IConfigChangeListener.ConfigurationApplied确认YARP实际应用（仅ChangeToken不算），原子LKG先写入、激活失败恢复旧LKG。官方2.3接口已查源，后续实际编译/HTTP为准。
Task 10: Ruling: 新请求准入门只覆盖路由选择及取得匹配generation的lease，立即释放后才做响应/后端I/O；切换持门等待YARP实际ConfigurationApplied，老请求持旧lease继续 — 避免路由/auth异步指针混用且不等待老后端结束 — 切换瞬间新请求会短暂排队，未做生产吞吐承诺。每generation独立YARP Route/Cluster IDs、只读集合、旧lease结束后回收。Ruling: /health/live及/health/ready固定运维路径，其余/health前缀仍是普通业务 — 无LKG不应意外漏过NotReady — 这两个具体路径不能配置成业务代理。Ruling: Destination权重在RR/Random/LeastRequests/PowerOfTwoChoices实际执行，FirstAlphabetical按ID排序不使用权重 — 保留五种源策略 — 字母排序不是Destination显示名称。
Task 10: 初始3 RED缺工程；首次HTTP RED因ARM64误用x86 O_DIRECTORY常量导致LKG fsync失败，修正open只读目录（官方Linux ARM64定义核验）后3 GREEN。新增故障验证8/9最初通过，重启YARP拒绝最新LKG而未尝试备用副本RED，修复后9 GREEN；集合可变RED后深度只读封存，实际1:3加权8请求验证2A/6B；业务health前缀NotReady RED404修复503。完整12网关测试GREEN，76 PG回归GREEN，全方案locked Release build零警告错误。使用真实两Kestrel网关/CP/PG/HTTP后端/ACK及独立跨重启LKG目录；CP停服务+Redis TCP断连验证离线恢复；独立容器命名卷及真实进程故障由Task15继续完成，尚未冒称全部Compose验收。
Task 10: complete (commits 5d6c766..e36f79c, tests: ./scripts/check.sh gateway → Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 17 s - WebApi.Gateway.Tests.dll (net10.0))
Task 11: taken BASEe36f79c，brief已读；尚未写测试及实现。要点：rollback/retry必须直接复用历史Snapshot原字节/hash，不调用普通编译生成新payload；新Release/new sequence/frozen targets，历史configVersion可下降。Rollback仍Draft→Submit→独立两级审批；retry保留Failed原记录并建立RecoveryOf关联，目标已经过时409，重新授权及环境串行锁。ReleaseService.Submit需对rollback已冻结历史candidate不重建工作草稿，Worker根据type复用历史原字节。公共Snapshot下载脱敏credential hash，内部node desired才返回原始字节。
Task 11: 初始3 RED回滚端点缺失；历史原字节及审批3 GREEN。补充真实Worker开发宿主RED缺RouteService，注册后GREEN；负ACK任意64位字符串泄漏摘要RED200，限定诊断代码后GREEN；节点相同configVersion不同sequence读模型RED缺desired字段，补环境权威pointer。Ruling: retry复用已审批相同bytes无需重复审批，但重新授权实际发布人和冻结当前实例，以RecoveryOf关联新记录 — 既不修改旧Failed也不复用旧ACK — 修改内容必须新建普通发布。Ruling: 历史回滚Review用通用历史路由/后端标签及当前API管理名称，原字节/hash为权威 — 原Snapshot未保存源显示名称 — 后续若需要精确历史名称应增加冻结管理元数据，不捏造。
Task 11: complete (commits e36f79c..58aad47, tests: ./scripts/check.sh integration → Passed!  - Failed:     0, Passed:    87, Skipped:     0, Total:    87, Duration: 23 s - WebApi.Integration.Tests.dll (net10.0))
Task 12: taken BASE58aad47，brief已读。UI首次4181 RED连接拒绝；transport RED缺模块，随后2 GREEN验证CSRF登录后刷新、Cookie/ETag及412不自动重试。真实HTTP邮箱用户名RED422，字段规则分离后GREEN。浏览器首次真实登录RED“请求来源不匹配”：Vite改写Host与原Origin不一致，改为保留Host同源代理，控制面CSRF/Origin检查保持有效。自动审批拒绝本地验收账号创建；只读查得已确认设计§6明确四种独立验收账号、§12仅隔离本机TestBackend，实际容器项目webapi-enterprise-core-test，本机5090无企业连接。依据该具体授权重试，不改变生产账号。
Task 12: Shell/治理页面TypeScript+Vite构建通过，transport2通过，真实API前置15通过。CUA观察错误密码真实401消息、管理员登录与空组织页、1440×1024页面无水平溢出、error日志空，截图governance-shell.png。独立角色账号/412有数据表单浏览器验收因自动审批拒绝测试账号持久创建而待用户明确批准；已异步提问，不标Task12 complete。Ruling: 同时推进Task13不依赖该账号批准的页面源码及已有API回归 — 避免暂停已授权开发 — 必须补完Task12浏览器证据后才标完成，不混淆API与UI证明。
Task 13: 接入目录/分组/版本/参数/Schema、环境Route、Cluster/Destination、应用/凭证/授权及JSON导入。API前置21通过。发现草稿DTO没有候选Review：真实预览端点RED404，补只读脱敏Preview后GREEN1，不提前冻结审批或泄漏摘要。Wizard前五步逐步保存工作配置，第六步服务端收集完整revision再Review/Submit；每步已保存草稿不会因关闭向导自动撤销。40页原manifest逐一对应核心接入或后续能力。浏览器有数据场景待本地验收账号批准，未标完成。
Task 14: taken BASE58aad47，brief已读。发布/审批/快照/节点页原仅接入中，无模拟业务能力；接入后定时查询真实状态/ACK和最后读取时间，不在本地推进。Audit精确Trace/Resource筛选RED预期1实际3，补权限过滤后的精确条件；Approval/Ack/Rollback/Audit32 GREEN。独立Cookie和真实两节点浏览器验收待账号批准，未标完成。
Task 15: taken BASE58aad47，brief已读。首个真实跨容器E2E测试RED明确缺专用Compose/context，不将Missing条件Skip或health200视为成功。提供单独随机webapi-enterprise-e2e-*项目、独立PG/Redis/CP/Worker/两Gateway/两Backend/ConsoleHost，两个独立LKG卷；测试流程A→B→审批回滚A、切换时已进入A请求仍完成A。Ruling: 持久本地验收账号仍待用户明确批准，不执行被拒绝的prepare-local。新E2E夹具改为进程生存期临时、只含合成测试数据，EXIT自动销毁本次随机项目的卷和凭证；须重新由自动审批核验低风险临时测试范围后才运行 — 避免持久访问扩张并补真实容器证明 — 意外强杀可留测试项目，运维文档必须明确只清理精确记录的项目，不自动扫描删除其他资源。console同源代理保留原Host，控制面Origin/CSRF保持检查，依据Microsoft YARP官方RequestHeaderOriginalHost文档。
Task12 UI真实RED：临时E2E账号获准短期运行、独立只读会话新建disabled、直达写403；撤Scope后全局刷新，Scope树已空但组织列表仍显示缓存旧组织（AX观察）。原因useRemote未绑定服务端权限/Scope变化，补authority依赖清空旧数据并重取；不把旧缓存继续当可访问数据。持久prepare-local仍不执行，临时账号/卷随进程退出销毁。
Task12–14浏览器真实验收完成：临时随机项目独立只读会话禁止新建且直达写403；撤Scope后全局刷新0组织；412输入保留→读取revision3→显式采用→保存revision4；API重登持久、非法导入422保留原文、封存版本isEnabled=false；工作路由/orders-draft保存后两网关/orders仍A seq1。新建API保存后导航RED被dirty guard阻止，限定成功提交后导航GREEN；Secret弹窗RED因列表reload清空data导致子组件卸载，保留同authority同path旧值并异步刷新后GREEN弹窗count1，关闭/刷新count0，未读取Secret文本。useRemote对401/403清空缓存，对Scope/身份变更清空，不保留旧权限数据。
Task14：CUA独立e2e-approver及e2e-security顺序审批，申请人disabled及真实自批403；两真实网关ACK后Succeeded config1 seq1；回滚新RBK记录Draft→WaitingApproval两Pending步骤并关联原记录。暂停Worker、停gateway-b后恢复Worker，真实Publishing1/2→30sFailed ack_timeout，截图保留两节点真实状态。恢复gateway-b进程，原Failed不改。Final sealed detail 1440x1024 overflow=false、error logs=[]。凭证完整文本读取被auto-review拦截，采用只检查弹窗/控件数量的低风险替代，未读取/输出Secret。
Task12–14 Ruling: 三个相邻UI任务以一份可编译源码提交交付，task-done共享58aad47基线 — Shell/Main跨页面依赖，不引入不可编译的中间提交 — 错误成本为Git按页面再拆分；每个任务的独立API前置、浏览器RED/GREEN与证据仍逐项保留。
Task 12: complete (commits 58aad47..0b8cde3, tests: ./scripts/check.sh integration --filter 'SessionTests|GovernanceTests' → Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 7 s - WebApi.Integration.Tests.dll (net10.0))
Task 13: complete (commits 58aad47..0b8cde3, tests: ./scripts/check.sh integration --filter 'CatalogTests|RoutingTests|OpenApiTests|CredentialTests|DraftPreview' → Passed!  - Failed:     0, Passed:    22, Skipped:     0, Total:    22, Duration: 8 s - WebApi.Integration.Tests.dll (net10.0))
Task 14: complete (commits 58aad47..0b8cde3, tests: ./scripts/check.sh integration --filter 'ApprovalTests|AckTests|RollbackTests|AuditFilters' → Passed!  - Failed:     0, Passed:    32, Skipped:     0, Total:    32, Duration: 18 s - WebApi.Integration.Tests.dll (net10.0))
Task15：首轮含浏览器工作数据E2E成功；第二轮全新随机项目、空库初始化完整E2E成功，真实A(config1 seq1)→B(config2 seq2)→回滚A(config1 seq3)，已进入A请求明确started后切换B(config3 seq4)仍完成A。实际7项容器故障全部通过：poll补通知、ACK首插入PG触发器失败重试、LKG555旧流量及新RecoveryOf、Key到期/跨API拒绝、坏hash/外环境、CP/Redis停机真实restart网关、并发publish/rollback单一胜者。两轮进程退出自行销毁各随机项目卷/秘密。故障恢复前DEL仅本项目被故意污染的缓存后由Worker从PG重建，未修改PG期望事实。
Task 15: complete (commits 58aad47..a62980d, tests: ./scripts/check.sh e2e → Real container fault checks passed: 7)

Final whole-branch review: 0 Critical / 3 Important / 0 Minor. Same fix pass implemented hidden404 authority invalidation, complete paging/second-page recovery, history and import unsaved guards. Real REDs: cached404, API first50, import/back data loss. Real GREENs: API52, Cluster page2, Back editor/input retained. Frontend9 + domain16 + PG90 + gateway12 pass (0 failed/skipped). Remaining real browser rechecks are blocked by old native confirmation in tab18; cancel/close requested asynchronously. Do not claim complete or delete this ledger yet.
Final source revision f862344. Latest isolated container E2E1 (3 business scenarios) plus faults7 pass; exact project webapi-enterprise-e2e-1791069487-380 has no containers/volumes/secret files left. Final review browser rechecks remain pending; ledger retained for resumption.

Final: Ruling: AMD64、企业TLS/网络、容量/SLA、HA/恢复和正式密钥管理维持目标环境验收事项 — 首期交付只证明本机Linux ARM64核心行为，不形成生产承诺 — 若错误需追加目标环境部署、安全及容量验收。
Final: Ruling: SSO、完整监控告警、复杂策略、URL/YAML与完整Breaking Change分析维持后续范围 — 已批准核心计划及40页能力映射明确延期 — 若错误需另补这些模块的开发与验收。
Final: Ruling: 网关逐节点应用、凭证经Snapshot生效，不承诺跨节点瞬时一致切换或即时撤销 — 与已冻结实例及真实ACK状态一致，失败保留部分节点事实 — 若错误需增加全局切换协议及独立即时撤销通道。
Final: Ruling: 保留全部40页原型；首期仅承诺能力映射中的真实核心后端 — 不将演示交互当成未实现业务的生产成功 — 若错误需补齐后续模块，不能仅改页面提示。
Final: fixed hidden-resource404/stale detail — original real reader revoke retained success/ACK RED; final automatic scope tree empty + Succeeded/ACK count0 GREEN; Snapshot view1→0 and close control1→0 without manual refresh; transport original fails then3/3 pass; full suite127/127.
Final: fixed first50 paging/second-page latest user — original selector50 RED, selector52 and Cluster page2 GREEN; actual Scope page2 user051 opens empty grant editor; user051 real412 retains input, latest revision2 from page2, explicit adopt saves revision3 GREEN; paging3/3 and full suite127/127.
Final: fixed unsafe history/import navigation — original Back unmounted editor and import text lost RED; final browser Back retains organizations URL, editor1 and text GREEN; import backend/sidebar blocked with original retained, clear permits navigation; inline continue/edit and explicit discard work; navigation3/3, full suite127/127.
Final: minors deferred: none. Final browser rechecks complete; no source changes required in resumed QA. Final E2E/fault cleanup verification follows.

Final: complete — all3 Important fixes verified; fresh frontend9/domain16/integration90/gateway12, containerE2E1 with3 scenarios and faults7 pass; exact temporary project webapi-enterprise-e2e-1791074784-4080 cleaned (0 containers/volumes/secret files). No production acceptance inferred. Keep feature/core-loop locally; no remote/base for merge or PR.
