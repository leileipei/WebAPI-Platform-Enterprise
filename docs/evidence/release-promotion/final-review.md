# 发布晋级整分支独立审查

审查日期：2026-10-09。结论：**With fixes；合入及安装前应修复 2 项 Important。Critical 0，Important 2，Minor 0。**

## 范围与证据

- BASE：`f73d4e486ec2632356c3b5578f1586302af34ae9`。
- HEAD：由 `git rev-parse HEAD` 确认的 `1d4be835a53b073abdf1a4454588dcf27226d905`。
- 按最终计划、权威规格和 progress.md 中所有 Ruling 审查 B 整分支；不重复已另行审查的 A 地址设计。阅读了权限、迁移、制品、附件、验收、映射、预检、正式发布、审批、Worker、ACK、恢复、生产验证、Console、备份和验收工具的修改及相关调用方/测试。
- 审查前后产品工作树保持不变；仅按委派要求写入本报告。没有启动/安装服务、修改测试或触碰原4192实例。
- 本次实际执行：`fa0c583d3177b5f86a467db1da166f5ad5a2d077/b11-runtime` 证据校验返回 `passed=true, errors=[]`；证据验证、交付前端状态及备份单元测试共 **53/53，通过，零跳过**。
- Domain753 / Integration965 / Gateway142 / Console245 / runtime146 / nativebackup2 是已提交验收记录中的结果，本审查没有再次启动这些完整服务套件。对测试代码与固定源证据作了核对，不将上述全量数字描述成本次重跑结果。

## 已落实的关键设计

正式晋级复用既有 Release 和两级审批，未增加平行发布引擎。新权限有显式默认角色排除；候选显式选择目标凭证并保留其他 API 的基线运行授权；Worker 的执行校验处在实际生成快照前。恢复记录保持 `PromotionId=NULL`，依靠有循环/跨环境检查的恢复链追溯。ACK 完成与业务交付完成分开，生产验证使用运行版本、部署序列、入口和人员资格共同绑定的上下文。报告存储、权限重检、备份归属及不可降级覆盖新事实的说明均有相应实现。

## Issues

### Critical

未发现有明确代码依据的 Critical。

### Important 1：来源验证可以把部署之前的测试结果绑定到当前制品

- **位置**：[ReleaseVerificationService.cs:31](</Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.worktrees/api-delivery-promotion/src/WebApi.Infrastructure/Delivery/ReleaseVerificationService.cs:31>)，关联同文件第45–52、64–66行；[TestAcceptanceService.cs:120](</Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.worktrees/api-delivery-promotion/src/WebApi.Infrastructure/Delivery/TestAcceptanceService.cs:120>)。绝对路径均指向本次受审 worktree。
- **触发条件**：当前 TEST 发布 R 已成功，生成制品 A。对 A 登记三类 Passed，`FinishedAt` 填在 R 创建之前，例如发布前一小时；时间仍在默认1440分钟有效期内，开始时间不晚于结束时间。随后正常申请，并由有资格的另一人验收。
- **代码依据**：`Validate` 仅检查类型、结果、起止顺序、未来时间及长度。登记代码核对“此刻”的来源部署，然后直接将 R 的 ReleaseId、快照、序列和入口写入记录，没有检查提交的测试时间是否发生在该部署之后。`RequireEvidenceAsync` 只检查有效期、最新证据以及已被服务器填入的当前上下文，没有补上部署时间下界。因此上述明显早于 R 创建、根本不可能测试 R 的时间仍能满足来源验收门禁。生产端在 `ProductionVerificationService.cs:30` 已有 `verification_predates_deployment` 处理，来源端缺失。
- **影响**：操作人员误选旧报告/旧时间，也能形成“当前制品已完成测试”的有效验收，并继续生产晋级。人工登记不能证明报告真伪，但不应接受与平台已知部署时间明显矛盾的证据。这直接影响“不能靠旧测试结果批准改过内容”的目标。
- **建议**：在持锁取得实际来源部署后校验测试结束时间不早于该部署的有效测试起点；按当前“成功发布后验证”的流程，可与生产端一致采用 `Release.CompletedAt`。验收/执行门禁也需拒绝已存储的此类时间矛盾证据，避免只修新增登记后旧记录继续有效。保留历史事实，不原地改写测试时间。
- **应补测试**：来源发布完成前（尤其创建前）且仍未过期的三类 Passed 不能登记为当前有效测试/不能通过验收；完成后的时间可通过；历史回滚/恢复应以本次实际来源部署时间核对，不能使用原快照早年的测试时间。现有 `ReleaseVerificationTests` 覆盖未来时间和倒置时间，但没有此下界；`DeliveryScenario.RecordAsync` 默认回填过去1分钟，修复时应同步调整为真实部署后的时间。
- **复现级别**：静态确定的调用链缺陷；本审查没有为此新建服务或修改夹具。与本次53项Node测试、既有真实闭环成功证据并不矛盾，现有用例未覆盖该负向条件。

### Important 2：纯 JWT 目标应用被强制要求具备 API Key

- **位置**：[PromotionCredentialSelection.cs:19](</Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.worktrees/api-delivery-promotion/src/WebApi.Infrastructure/Delivery/PromotionCredentialSelection.cs:19>)。
- **触发条件**：来源制品采用 JWT；生产目标已有合法 JWT 策略、应用映射和该 API 的有效目标授权，应用只使用外部签发的 JWT，因此没有平台 `ApplicationCredential`。显式映射该应用，提交 `CredentialIds=[]` 及合法 `AuthorizationIds`。
- **代码依据**：第19行无条件要求每个应用 `CredentialIds.Count >= 1`，在读取认证模式之前即返回422。完全不选该应用也不能绕过，因为第24行要求认证路由有目标授权覆盖，`ReleaseCandidateBuilder.cs:95` 还要求 JWT 映射应用出现在候选。现有普通发布则允许 `Credentials=[]`，`SnapshotCompiler.cs:64` 正常编译空凭证应用；`JwtApplicationBindingTests.cs:93` 明确断言这一已有能力。B2亦明确支持 JWT 策略的环境字段映射。
- **影响**：现有合法的纯 JWT 接入无法走晋级流程；用户只能额外创建一个业务上不需要的 API Key，才能通过与 JWT 无关的校验。首期规格没有排除纯 JWT，也不应将“显式选择已有凭证”解释为强制创建新凭证。
- **建议**：按冻结路由的实际认证需求核对显式凭证集合。允许合法 JWT 应用显式选择空 API Key 集合，继续强制 JWT 应用映射、目标授权、有效状态和 Scope 校验；对 ApiKey 路由仍要求有效目标凭证，对共享基线已有凭证仍必须保留，不能简单取消全部凭证门禁。
- **应补测试**：纯 JWT 制品＋目标有效应用授权＋零 API Key 可保存映射、预检并编译；缺 JWT 映射/授权仍阻断；ApiKey 路由零凭证仍阻断；JWT 与 ApiKey 混合以及共享应用保留基线凭证的回归继续通过。
- **复现级别**：输入 `CredentialIds=[]` 必然命中第19行，静态依据明确；没有依赖猜测的运行环境条件。

### Minor

未列出风格或低价值建议作为缺陷。

## 五项 Review Focus 结果

| 重点 | 结果与核对依据 |
| --- | --- |
| 1. 内置角色自动补权限 | **通过指定场景审查。** `PermissionCatalog.cs:24–25` 显式排除三个新权限；PlatformAdmin继续按目录补全。`DeliveryPersistenceTests.NewPermissionsOnlyDefaultToPlatformAdminAndExplicitGrantsSurviveReseed` 检查重复Seed、普通内置角色0默认授权、管理员3授权，以及显式人工授权不被重置。 |
| 2. 共享应用基线与TEST凭证隔离 | **通过指定共享基线场景审查，另有上述JWT兼容问题。** `PromotionCredentialSelection.SelectAsync` 从已校验基线保留未选 API 的应用、授权及凭证，对共享选中应用要求包含全部保留凭证且字节事实不变，并明确确认影响。候选取显式目标凭证，不拷贝来源集合。`PromotionCredentialTests` 覆盖遗漏凭证、未确认共享、保留其他路由/授权及跨环境共享；真实proof有来源凭证到目标401。 |
| 3. 撤销与Worker领取竞争 | **通过指定场景审查。** 所有业务命令和Worker先取共同治理锁；验收撤销再锁排序后的来源、当前和历史关联目标。尚在Building会被取消；Worker构建处重新执行 `RequireExecutionAsync`，之后才写快照/Outbox。`PromotionConcurrencyTests` 覆盖提交/撤销竞争；`PromotionExecutionTests.QueuedBuildRechecksAfterFreshServiceScope` 覆盖排队后撤销、过期、凭证、入口、身份变化；B11真实停Worker后撤销及撤发布权限/改连接再启Worker均有证据。 |
| 4. 恢复Release唯一关联 | **通过指定场景审查。** 正式关联有唯一索引和关联约束，retry/rollback不复制PromotionId；`RecoveryOf/RollbackOf`追溯检查100层上限、循环和跨环境。恢复沿用精确已存在Snapshot和新序列；晚到旧ACK校验当前环境序列。`PartialReceiptsAndFailurePersistAndRetryUsesSameSnapshot` 及 `FailedRollbackRetryStaysRollbackRatherThanVerifyingOriginalDelivery` 覆盖关联和回滚意图。 |
| 5. 旧生产验证上下文 | **通过指定场景审查。** `ProductionVerificationService.RecordAsync` 要求ExpectedContextHash与实际运行上下文一致；`RuntimeContextAsync`核对正式晋级Snapshot/当前Release、全部节点序列；新入口重新收齐三类事实。`PreviouslyOpenedEntryContextCannotSilentlyBindOldEvidenceToNewEntry`、`ChangedActualRuntimeCannotCompleteOriginalDelivery`、最后验证/回滚竞争等用例覆盖旧表单和版本变更。 |

## Ruling逐项权衡

下表数字为 `progress.md` 的 Ruling 行号；同一行中的多个决策分别说明。接受某项取舍不表示免除上面两项实现缺陷。

| Ruling位置 | 审查判断 |
| --- | --- |
| 15 | 接受策略端点提前到B1，以便实际ETag/幂等验证；最终接口没有重复分叉。 |
| 16 | 接受事件单一归属Promotion或Acceptance及XOR约束；验收撤销不必伪造晋级。 |
| 18 | 接受组织级应用ProjectId可空；应用组织/项目适用性由映射服务补充核对，未把可空字段强制迁移成非空。 |
| 24 | 接受纯模板DTO放Contracts、算法放Domain，避免循环引用。 |
| 25、28 | 接受冻结实际认证模式；接受服务器派生逻辑路由键并拒绝不符输入，避免默默改路由身份。 |
| 29、30 | 接受保留业务Schema/描述/示例，不声称通用内容脱敏；接受来源恢复/回滚从原publish候选校验精确快照，同时绑定本次成功运行发布。 |
| 41 | 接受artifact/promotion归属参数替代任意Scope输入，服务端解析及事务内重检范围。 |
| 42、43 | 接受报告卷提前纳入备份；接受有界原始正文、并发上限、UTF8/HTML签名检查。PDF仅格式头检查的能力表述准确，未把它当内容安全扫描。 |
| 48 | 接受独立连接确认报告元数据提交后再清理本次文件；数据库不可确认时保留可能已提交文件比误删证据更合适。 |
| 60、61、62 | 接受共同治理锁及排序/历史目标锁；只撤销尚未下发记录，不暗示撤回已应用节点；执行前重检原验收人资格合理。 |
| 68、69 | 接受通过现有目标策略ID/修订提取环境字段、生成独立策略副本；接受既有Route严格同API/规范化路径/方法匹配，防止覆盖其他业务。 |
| 70、71 | 接受未选业务精确保留及共享确认；接受准备原语必须在调用者事务中使用、HTTP仅给ID。其凭证保护必须与JWT空凭证合法性兼容，见Important 2。 |
| 74、75 | 接受保留旧私有策略供历史追溯；接受B5使用编译器验证持久夹具，真实目标部署另由B11验证，未混淆证据级别。 |
| 77、81 | 接受只暴露跨环境共享布尔值、以额外授权修订冻结影响；接受晋级阻断Retired API，避免隐式移除测试制品。 |
| 85 | 接受未知/不可见artifact保持404，不通过幂等键反向探测其他Scope。 |
| 86、87 | 接受上游健康是显式人工确认而非自动URL探测；契约风险引用真实持久比较及人工接受，Unknown不自动视为无风险。 |
| 88、89 | 接受抽取同事务冻结/审批席位原语；接受B6先落直发门禁、B7完成Worker二次校验的提交顺序，最终分支两部分齐全。 |
| 92、95 | 接受生产无基线时阻断并先通过Legacy建立；接受来源过期后独立审批者仍可拒绝，避免无效申请无法终止。 |
| 101、102 | 接受历史恢复继续使用原权限/精确快照，不重新要求当前来源验收；ACK/timeout共同治理锁在排序环境锁前，避免反向加锁。 |
| 105、110 | 接受重试回滚仍投影RolledBack；成功发布幂等重放原回执且不再次执行，并重新检查当前命令权限/来源可见性。 |
| 112 | 接受复用原持久Worker循环和审批钩子，在协调器加门禁并补两个宿主依赖，无平行Worker。 |
| 118 | 接受仅生产新增必需ExpectedContextHash及上下文GET、来源契约保持兼容；此兼容决定不应被解释为允许来源测试时间早于来源发布，见Important 1。 |
| 125、130 | 接受部署后生产验证使用冻结有效期政策及当前验证者权限，不强制来源一直运行不变；Completed保留历史事实、回滚执行单独展示，成功后RolledBack。 |
| 138、141 | 接受来源分页/服务器资格接口，必要时在SQL里过滤策略可见性再计数；预算和默认/上限齐全，没有用受限数据推断零风险。 |
| 144 | 接受先固定实现提交再拍浏览器证据；不将中途失败证据算成功。 |
| 158、159 | 接受额外目标映射选项端点服务环境委派，返回必要的安全摘要；总览按两环境和来源资料授权查询，Restricted/null与Partial有区分。 |
| 162、165 | 接受审批行动资格在分页SQL前加入来源资料/策略权限；新建晋级按钮依服务器当前验收和实际读写Scope，后端仍重复验证。 |
| 166、169 | 接受将共享凭证影响存入预检JSON且按app.read暴露；映射资料缺权限时整体隐藏并停用编辑，避免覆盖隐藏字段。 |
| 170、171 | 接受1000条选项/凭证/授权预算及truncated阻止保存；恢复追溯复用来源权限并返回Restricted空摘要，目标读权限不扩大来源访问。 |
| 176 | 接受保留各固定提交的失败浏览器证明并重建，不改写失败为成功。 |
| 187（备份） | 接受固定两个生产节点服务、三个附加卷和显式元数据；恢复只进入新UUID及自己的标签，未开放任意卷清单。 |
| 187（清理） | 接受准确补入通知夹具卷白名单，仍检查UUID、归属及已知名称。 |
| 189 | 接受私有10.249.x.0/24显式可选网络；未修剪他人网络，重叠由Docker拒绝。 |
| 193 | 接受e2e/faults/browser均指向完整同一隔离场景，保持部署/人员上下文一致；文档明确这是别名，未声称三个不同场景。 |

## 迁移与还原工具

迁移增加交付表、可空Release关联及范围约束，旧项目缺连接行由服务返回Legacy；已检查保留组织级应用空ProjectId和旧发布字节的迁移测试。没有发现需要以旧数据库覆盖新交付事实的升级路径。报告只挂ControlPlane，支持检测来自固定部署定义；旧备份没有报告卷时保留兼容分支。新增生产夹具备份列举固定卷/服务，停机失败的finally会恢复原服务；恢复先初始化原生私有卷，再初始化交付卷。本次备份单元测试和固定证据校验均通过。

## Declined to judge

- **原4192安装后的兼容性/运行状态**：本分支尚未安装，委派已说明安装在审查后；不把未来步骤标为产品缺陷，也不据本审查宣称本机安装成功。后续仍需记录实际固定镜像、迁移、原账户/SSO、双网关、通知/监控及报告读取。
- **企业生产DNS/TLS/LB、真实企业上游、性能与HA**：现有证据是自有隔离环境，不能据此判断企业生产验收；proof明确productionAcceptance=false。
- **人工报告内容真实性、自动测试引擎、任意Stage编排**：首期明确采用人工登记，未实现也未声称这些能力；此处不另加自动化引擎要求。平台可知的时间一致性仍在本次审查范围，已列Important 1。
- **旧二进制对已经产生新晋级事实后的无条件降级**：计划明确要求独立验证并优先前进修复；本审查只判断迁移及隔离恢复代码，没有执行降级试验，也不批准覆盖升级后新事实。
- **18张截图的重新逐张视觉验收**：本次验证其摘要和已提交标记，未另行重复整套人工视觉验收；既有检查记录不等同于本审查再看了一遍截图。

## Assessment

**Ready to merge：With fixes。** 五项指定高风险主链有实质实现和测试支撑，固定隔离证据自洽；但来源测试时间绑定和纯JWT映射两项会分别影响验收可信度与已支持场景的正常交付，应修复并补对应回归后再进行后续安装与最终Coverage封档。
