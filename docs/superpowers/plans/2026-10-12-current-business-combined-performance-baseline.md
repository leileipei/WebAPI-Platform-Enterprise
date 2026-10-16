# 当前业务组合性能对照实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. 用户已选择当前会话逐项实现、测试，最后独立审查；不重新选择执行方式。步骤用 `- [ ]` 跟踪，勾选须有实际证据。

**Goal:** 在当前业务上交付精确限定的反事实基线和严格共享夹具，完成业务等价与固定版本前置验证，为原性能矩阵准备审阅包。

**Architecture:** 候选产品保持 C760；独立 B_current 只改变七种查找和批准的管理读取路径。双方使用 H_align 的同一份模板数据，克隆后验证完整结构与权限，再应用恰好四个索引的实验角色；完整 DTO 预检先于 Gateway 计时。

**Tech Stack:** 项目现有 .NET 10／EF Core／Npgsql、Node.js、Docker Compose、固定 PostgreSQL 17.11；锁文件和固定镜像不变，不新增依赖。

**Spec:** [已批准设计](../specs/2026-10-12-current-business-combined-performance-baseline-design.md)，SHA256 `98ff99ab88bf504d0e9f9d32fb288b15e168997867aaa69e1bbf6b91f659bbe0`。用户“确认”批准书面设计，允许本计划准备；本计划仍待审阅。产品源码及原生测试尚未实施。

## Global Constraints

- 当前业务共同底座 C760：`760493da5a5bc0f0674e7e6398260c8a5ac4a6c0`，src tree `1aa393776ac219f43c7a9de48a63632dce1c82bf`；父夹具 H417：`417f67cfc0b833093f17de104ebf4c882388f7f9`。
- 13 个已有产品文件及 1 个新文件的白名单以设计第 4.1 节为准；其他 src 字节必须相同，候选产品源码保持 C760。
- 保留现有主工作区 649 个文件、Git 状态、HEAD，保留原隔离源码、失败证据、账号和数据。不在主工作区实现，不 reset／stash／clean，不向 main 导入试验代码。
- 全部字段、权限、状态码、分页排序、审批发布和历史快照语义保持；发布和用户角色的关联批量装配为共同逻辑，不归因于旧 N+1 改善。
- 双方同一 H_align 完整文件指纹、数据与 Snapshot 字节；不能由角色标签代替实际结构、源码和数据库证明。
- S=100 路由／100 应用／200 凭证；M=1,000／1,000／2,000；L=10,000／5,000／10,000，每应用 8 个授权窗口。Snapshot 上限仍为 16,777,216 bytes。
- 原矩阵 144 对／288 窗口；每窗预热 10 秒／测量 20 秒，重复 3，并发 1／32／128，A/B 与 B/A 顺序不变。
- 两 Gateway 和 ControlPlane 各 2 CPU／2 GiB，PostgreSQL 2 CPU／3 GiB，backend 1 CPU／256 MiB，Redis 0.5 CPU／256 MiB，client 2 CPU／1 GiB；server GC、70% heap hard limit、池 32。
- 数据库 max_connections=100／shared_buffers=512MB／work_mem=16MB／maintenance_work_mem=256MB／parallel workers per gather=0／UTC；固定 PostgreSQL 17.11 镜像及现有 SDK／Node／Redis 镜像摘要沿用。
- 原 SQL 预算、路由轻量键 O(N) 例外、引用批处理特殊预算、小档 >10% 退化调查及稳态内存增加 >25% 评估不变。Release ≤12、告警规则 ≤10、告警事件 ≤6，不因新业务自动上调。
- 只有四个批准索引可在独立工作库中改变；迁移历史、模型、其他索引及原始 ModelMatched 不改写。组合收益不能拆为单项贡献。
- 本计划批准后可实施和验证任务 1–10；不自动新增提交。任务 11 需要精确双提交审阅包获准，任务 12 的正式矩阵还需运行授权。推送、main 合并、4192／生产部署不在本计划执行范围。

## Review Focus

1. RF1：两侧结构都错误却摘要相同，必须通过当前目标模型类型／非空／主键检查后才可冻结模板；任务 1、2 验证。
2. RF2：模板连接、未知设置／角色、ACL 或 owner 异常，必须阻断相应操作，失败只保留及清理本 owner；任务 2 验证。
3. RF3：第三个索引删除失败，整个四索引事务回滚；同名不同定义及额外缺失不能被 role 验证接受；任务 3 验证。
4. RF4：基线重复取数据库时间、改变关系数组排序或把功能失败误归为预算失败，必须拒绝；任务 6、7、10 验证。
5. RF5：仅凭 Indexes 属性／baseline 标签误报 scan，或同 SHA 双构建标签冲突，必须验证实际容器并复用同身份镜像；任务 8、9 验证。

## 文件与接口约定

路径均相对各任务的独立仓库。文档／证据宿主为现有 gateway-query-performance 工作树；代码只写新 checkout：`H_work=.runtime/current-business-shared-harness`、`B_work=.runtime/current-business-scan-baseline`，两者相对宿主工作树，必须不存在才创建。两者起点 H417；实现阶段使用 using-git-worktrees 技能选择隔离方式。原生工具不能在含 H417 的仓库创建时，从现有 `.runtime/explain-cte-validation-repo` 进行本地 `git clone --no-hardlinks`，核对完整提交后创建隔离分支；不 fetch 到 main，不修改原 checkout。

- H_work：共享夹具及其测试；src tree 保持 C760。B_work：14 个批准产品路径及基线功能／成本测试。共享测试在资格快照中覆盖到双方，不能把基线专用类型测试复制进候选。
- `LegacyContract`、`DatabaseInspection` 使用现有 C# 记录及其 JSON 字段；`TemplateInspection={contract,inspection,metadata}`。metadata 沿用探针的 `{database,settings,effective,objectPermissions,migrationRows}`，不含数据库 OID 或工作库名称作为逻辑相等项。
- `readState(): Promise<TemplateInspection>` 总是检查当前 `webapi_performance`。所有 helper 接收现有 `docker(args, options)` 和 owner，执行前核对真实镜像、双标签与固定数据库。
- `TemplateFixtureReceipt={schemaVersion:1,owner,key,sourceProduct,postgresImage,contractSha256,metadataSha256,privateContractSha256}`；私有文件由 owner/key 推导，0700 目录／0600 文件，禁止符号链接或外部路径。
- `ExperimentRoleProof={role,modelMatched,experimentRoleVerified,missingApprovedIndexes,contractSha256,metadataSha256,indexesSha256,complete}`，role 仅 baseline／candidate。
- 文档 [预算断言登记](../../evidence/gateway-query-performance/current-business-combined-baseline-plan/candidate-budget-assertions.json) 固定 19 处源位置、31 个片段及测试方法／参数限制。它用于分类，不用于跳过测试；无法唯一归因的失败直接阻断。
- 每任务保存 RED、GREEN、命令、退出码、实际计数、源码／夹具摘要、owner 与清理回执。未固定的诊断一律 immutable=false／formalQualified=false；不继承旧回归数量。

---

### Task 1：只读当前目标结构契约及隔离准备

**Files:** H_work 修改 `tools/WebApi.PerformanceHost/MigrationProbe.cs`、`Program.cs`、`tests/WebApi.Integration.Tests/PerformanceMigrationTests.cs`。

**Interfaces:** 产出 `Task<LegacyContract> MigrationProbe.CaptureCurrentContractAsync(WebApiDbContext db,CancellationToken ct=default)`；CLI `migration-current-contract --config ... --output ...`，沿用固定 Host=postgres／DB=webapi_performance／Username=webapi 限制。

- [ ] 建立 H_work／B_work，核对父 H417、原工作区保护、两个 src tree 与批准 spec／预算登记哈希，记录隔离方式。已有路径或身份不符即停止，不复用未知内容。
- [ ] 写 `CurrentContractChecksFullTargetSchemaWithoutMigration`、`CurrentContractRejectsTargetDrift`、`CurrentContractAllowsApprovedIndexAbsence`：正常全库返回全部表；分别改 varchar 列类型、非空、新表字段、主键时抛 InvalidDataException；缺四索引仍可捕获结构，原 ModelMatched=false；前后迁移历史和数据摘要相同。
- [ ] RED：`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~PerformanceMigrationTests'`，新方法／入口不存在或错误结构被接受必须造成失败，保存日志。
- [ ] 实现只读捕获，复用 `ReadLegacyAsync(db,null,true,ct)` 的全目标结构校验；不得调用 MigrateAsync。Program 加入精确命令并保持旧入口行为。
- [ ] GREEN：重跑全部 PerformanceMigrationTests，新增负向类型／PK和原兼容／纯索引／故障测试均通过；新 native owner 清理归零。保存精确三文件补丁，暂不提交。

### Task 2：可复用独立模板与配置权限恢复

**Files:** H_work 新增 `scripts/performance/template-fixture.mjs`、`template-fixture.test.mjs`。

**Interfaces:** 消费 Task 1 的 readState；产出 `captureTemplateFixture({owner,postgresId,key,sourceProduct,readState,docker}): Promise<TemplateFixtureReceipt>`、`restoreTemplateFixture({owner,postgresId,fixture,readState,docker}): Promise<{restored:true,state:TemplateInspection,receipt:TemplateFixtureReceipt}>`。

- [ ] 写测试 `rejectsUntrustedInputsBeforeDatabaseMutation`：错标签／镜像、symlink、非0600、契约哈希变化、非法 key、未知设置／角色均拒绝，docker mutation calls=0；`cloneRestoresNondefaultConfigurationAndAcl`：直接克隆遗漏后，UTC／16MB、本库角色设置、撤销 PUBLIC TEMPORARY 和 grant option 严格恢复；`connectedTemplateBlocksClone` 不杀会话（RF1、RF2）。
- [ ] RED：`node --test scripts/performance/template-fixture.test.mjs`；接口缺失或任一异常执行了 drop/create 必须失败。
- [ ] 实现经校验的 `ordinary-100` 等 key 到模板名 `webapi_performance_t_<scene>_<scale>` 映射；停止 owner 应用且无会话，先当前模型验证，再保存私有契约、重命名并关闭模板连接。恢复前校验全部输入，克隆后恢复固定设置及 ACL，再严格比较类型／主键／行／历史及 ProductVersion／全索引／配置／对象权限。未知角色或设置不执行猜测恢复。
- [ ] GREEN：单元测试通过后，用 C760 产品和未固定 H_work 做新 owner S100 两次真实克隆，加入实际连接、非默认配置／权限和数据漂移拒绝演练；标注 mutable diagnostic。私有契约篡改在 drop 前拒绝，冻结库实际数据漂移在克隆复验时、任何索引／应用操作前拒绝，保留失败，不宣称在线恢复。
- [ ] 独立比对两个契约与实际秘密扫描，清理仅本 owner；补丁只含该两文件，暂不提交。

### Task 3：四索引事务和独立实验角色门禁

**Files:** H_work 新增 `scripts/performance/index-experiment.mjs`、`index-experiment.test.mjs`。

**Interfaces:** 消费 Task 2 的严格恢复 state；产出 `prepareIndexExperiment({owner,postgresId,role,reference,readState,docker}): Promise<ExperimentRoleProof>`、纯函数 `verifyExperimentRole({role,reference,current}): ExperimentRoleProof`。reference/current 为 TemplateInspection；reference 必须为完整候选状态。

- [ ] 写 `baselineRemovesExactlyFourIndexesAtomically`：恰好四个批准名称缺失，其余定义／唯一性／有效性及数据和历史不变；candidate 不发 DROP；`rejectsExtraOrCorruptIndexChanges`：额外缺失、同名变定义、unique／constraint、invalid／not ready 拒绝；`thirdDropFailureRollsBackAllFour` 在真实事务第三条 DDL 前注入错误，四索引都仍存在（RF3）。
- [ ] RED：`node --test scripts/performance/index-experiment.test.mjs`，缺函数、部分删除被接受或模型结果被改写均失败。
- [ ] 实现固定四个名称／表的精确事务 DDL，不接受调用者 SQL。角色验证比较完整目标结构、数据、真实迁移、配置／权限及所有其他索引；保留 inspection.modelMatched，另算 experimentRoleVerified。不使用“排除任何索引后相等”的通用例外。
- [ ] GREEN：单元及新 S100 owner 的 baseline／candidate 原生演练通过；普通 S 当前 326→322 仅作实际样本，故障回滚和 role=false 都有实证；ANALYZE 在计时外，原 pure-index／compatible 测试不降级。
- [ ] 保存四索引源定义摘要、事务回执及清理，暂不提交。

### Task 4：同代线性 map 和完整拒绝语义

**Files:** B_work 修改 `src/WebApi.Gateway/Configuration/RuntimeLookupIndex.cs`；新增 `RuntimeLinearLookupMap.cs`（同目录）和 `tests/WebApi.Gateway.Tests/RuntimeLinearLookupMapTests.cs`。原 RuntimeLookupIndexTests 不改。

**Interfaces:** `public sealed class RuntimeLinearLookupMap<TKey,TValue>:IReadOnlyDictionary<TKey,TValue> where TKey:notnull`，构造器 `(Func<TKey,(bool Found,TValue Value)> find,Func<IEnumerable<KeyValuePair<TKey,TValue>>> enumerate,Func<int> count)`；七个 Indexes 属性及 `Build` 签名不变。

- [ ] 写 `LookupVisitsOnlyUntilMatchAndNeverCaches`：first／middle／last 计数枚举器分别访问1／N÷2+1／N，每次重复仍扫描；不存在键返回 false／索引器抛 KeyNotFoundException；键和值枚举、Count、Ordinal和组合键命名空间正确。
- [ ] 写 `ValidationRejectsEveryCurrentFault` 覆盖设计3.2全部重复／引用／认证／JWT错误；`WindowsRemainImmutableAndUnfiltered` 保留未来／过期／重叠窗口，现有 IList.Clear 必须抛 NotSupportedException；`AuthenticationBuildsValueOnlyForMatchedRoute` 确认不对全部扫描路由解析值。
- [ ] RED：`./scripts/check-contracts.sh gateway --filter 'FullyQualifiedName~RuntimeLinearLookupMapTests'`；缺 map 或仍保留索引／缓存导致计数不符。
- [ ] 实现 map callbacks 只引用 frozen snapshot 与原 authentication 配置；临时验证集合不捕获。PermissionWindows 使用私有只读筛选 view，同时实现 IReadOnlyList／只读 IList 契约，不创建全代数组。其他 src，尤其 middleware／store，完全不改。
- [ ] GREEN：上述测试和全部原 Gateway 功能／安全／租约／热更新／回滚测试通过；审查所有 callback 捕获和字段，记录构建临时分配及无持久七 map 的源码证据。仅两产品路径及新增测试补丁，暂不提交。

### Task 5：核心及范围列表的精确反事实

**Files:** B_work 修改 `Catalog/CatalogService.cs`、`Applications/ApplicationService.cs`、`Policies/PolicyService.cs`、`Routing/ClusterService.cs`、`Routing/RouteService.cs`（均在 `src/WebApi.Infrastructure/`）；新增 `tests/WebApi.Integration.Tests/CounterfactualCoreListTests.cs`。

**Interfaces:** 原 ListAsync／DTO 签名不变；Consume 当前 Scopes、EffectiveAsync 和 Pagination。新测试 fixture 在自身文件，使用现有 ApiFixture／SqlRequestProbe，不改变原预算测试。

- [ ] 写 `CorePagesRetainFullContracts`：151 主记录、页大小1／50／100、第一页／中间／末页／越界／负值钳制，明确 Total、ID、完整 DTO；Cluster 同名 Destination 按 Name／ID；Route 多语言／Unicode 等价路径按 CurrentCulture／ID。
- [ ] 写 `ScopedCounterfactualUsesCurrentAuthority`：302／80／0 应用可见量、共享策略302／151／75、环境不能获得父项目读权限、无功能／Scope、停用／跨组织、撤权下一请求，以及422条件；`CostIsUnboundedBeforePaging` 确认全量主表／每候选权限／逐路由 Effective，不能仅因 budget 失败证明反事实成立。
- [ ] RED：`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~CounterfactualCoreListTests'`；原优化实现应不满足反向成本断言，保存失败。
- [ ] 按设计表逐方法修改；数据库排序在取全量前执行，授权过滤先于内存分页。Cluster 本页关联保持完整次序；Route 分页前全部 Effective，含空页成本。CollectionAuthorizationQuery、其他详情／命令字节不改。
- [ ] GREEN：新功能与成本测试通过，原 Core／Scoped 全测试执行保留TRX；只能按登记片段分类成本失败，所有字段／权限／排序失败都阻断。暂不提交。

### Task 6：发布、用户与 Gateway 管理读取

**Files:** B_work 修改 `Releases/ReleaseService.cs`、`Governance/GovernanceService.cs`、`Gateway/GatewayReadService.cs`；新增 `tests/WebApi.Integration.Tests/CounterfactualGovernanceListTests.cs`。

**Interfaces:** 所有现有列表签名不变；仍一次调用 `ReleaseListReader.ReadAsync(page,actor,ct)`，仍同一 UserRole 页内批量查询。

- [ ] 写 `ReleaseAndUserRelationshipsRemainByteIdentical`：多 Target、ACK、Role 数组和当前发布6类新增字段，读／写环境权限的内部地址遮罩、风险评论三权限、流水线创建者审批限制及交付轨迹；不得因新查询自行重排关系数组（RF4）。
- [ ] 写 `GatewayMetadataAndCapRemainUnchanged`：10,003事件的 Total=10,000及末页IDs、越界空页；Snapshot最新 deploymentSequence而非CreatedAt、size元数据，未加载PayloadBytes；同时验证重复最新序列按原方式失败、稳定心跳状态、ETag及撤权。`FullPrimaryReadAndPerSnapshotAssociationsAreMeasured` 确认反向成本及用户关联仍仅0／1次。
- [ ] RED：`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~CounterfactualGovernanceListTests'`，当前有界主表不满足成本检查。
- [ ] 只改批准的列表主表读取和 Snapshot 逐版本关联，复用当前谓词、Dto 和异常行为；ReleaseListReader、eligibility、delivery guard 与全部写路径不改。
- [ ] GREEN：新测试、原完整 ReleaseListQueryTests 必须全通过；Gov 原测试的成本失败仅允许指定行／方法，payload、角色关联和字段失败不免责。暂不提交。

### Task 7：告警单次时钟与策略引用逐来源读取

**Files:** B_work 修改 `Alerts/AlertRuleService.cs`、`Alerts/AlertEventService.cs`、`Alerts/AlertListReader.cs`、`Policies/PolicyReferenceReader.cs`；新增 `tests/WebApi.Integration.Tests/CounterfactualAlertReferenceTests.cs`。

**Interfaces:** 原 ReadRulesAsync／ReadEventsAsync／ReadVisibleAsync 不变；单次非空页 DatabaseTimeAsync调用1次、空页0次；当前 PlanBatches helper 和其原测试保持不变，B读取路径对每个合法 descriptor 调用现有单元素批方法。

- [ ] 写 `AlertClockIsSingleForAllPageItems`，在151记录、size1／50／100及空页统计Clock=1／0；`KnownUnknownAndResolvedContractsRemainCorrect` 覆盖未来／过期slot、全部活跃环境、旧逻辑状态、退役目标、Transition OccurredAt／ID、Unknown 值遮罩（RF4）。
- [ ] 写 `ReferenceReadsEachVisiblePayloadOnce`：Working／Frozen／Runtime完整Total、独立权限、历史schema/source ID语义、重复名次序、实际二进制字节一致；损坏／缺失可见载荷409、隐藏引用继续保护写、合法8–16MiB及多字节对象可读；N来源=N次载荷查询，无负大小放行。
- [ ] RED：`./scripts/check-contracts.sh integration --filter 'FullyQualifiedName~CounterfactualAlertReferenceTests'`，原批量查询不满足逐来源／逐项成本。
- [ ] 告警 reader 先获取一次 now 再逐页项读取原关联并复用原 DTO 计算；不逐项调用已有带时钟的整个读方法。引用只改变读取批的粒度并保持全部合并／排序、异常和写保护；当前 PlanBatches 仍正常返回200／1，不为构造基线修改其通用契约。
- [ ] GREEN：新增测试及原功能断言通过；Clock、BinaryColumnBytes、PlanBatches 或保护语义失败均阻断，只有登记的 SQL／行数成本可分类。保存四产品路径补丁，暂不提交。

### Task 8：实际容器微基准与完整性门禁

**Files:** H_work 修改 `tools/WebApi.PerformanceHost/LookupMicroProbe.cs`、`tests/WebApi.Integration.Tests/PerformanceProbeTests.cs`、`scripts/performance/completion.mjs`、`completion.test.mjs`；B_work 新增 `tests/WebApi.Integration.Tests/CounterfactualMicroProbeTests.cs`。

**Interfaces:** Measure 签名不变；LookupMeasurement增加尾参数 `string Backend="legacy-array"`；Backend仅 `runtime-frozen-dictionary-v1`／`runtime-linear-map-v1`／`legacy-array`。新增 `LookupMicroProbe.ClassifyContainers(IReadOnlyList<object> containers):string`，输入七个实际值；原旧模式 legacy 保持，组合模式只接受前两种且符合源角色。

- [ ] 写 `BackendFollowsSevenActualContainers`、`MixedAndUnknownBackendsReject`、`BaselineLabelCannotTurnFrozenContainerIntoScan`；共享代码不得直接引用候选不存在的线性类型；基线真实程序集 Measure 输出21项、scan、真实Backend、每项iterations=hits=10,000（RF5）。
- [ ] 写完成门禁故障：去掉目标结构、模板配置、role证明、backend或完整窗口任一项，combined complete=false；legacy complete逻辑原样。不得只凭虚构元数据关闭门禁。
- [ ] RED：Node completion及定向 PerformanceProbeTests，当前按属性存在标 indexed 的行为必须在真实B接口测试失败。
- [ ] 实现实际类型／键值接口的反射识别和编译getter；保留实际对象调用与原legacy扫描路径。组合门禁额外绑定来源差异、模板／角色和backend证明，不减少原verifyBaselineCompletion要求；GenerationProbe真实生命周期保持不改。
- [ ] GREEN：七操作×三位置、真实两种产品接口测试、未知／混合拒绝和原资源完整性测试通过；保留源码审阅和Task4计数证明，暂不提交。

### Task 9：组合模式、同身份镜像及预检前置

**Files:** H_work 修改 `sources.mjs`、`comparison-runner.mjs`、`comparison.test.mjs`、`performance.test.mjs`；新增 `current-business-parity.test.mjs`（均 `scripts/performance/`）；修改 `tools/WebApi.PerformanceHost/PerformanceManagementDataset.cs` 和共享 `PerformanceProbeTests.cs`，只稳定历史节点夹具时间。

**Interfaces:** parseArguments新增 `experiment`；精确值 `current-business-combined-v1`，只有固定三身份的 compare／validate diagnostic=compare合法。`runComparison`消费任务2、3；`afterManagementPreflight`及原DTO比较接口保持。现有 `--sql-audit` 只对新组合模式的固定身份 `validate --diagnostic compare --preflight management` 允许收集；compare正式模式及所有旧compare诊断仍拒绝SQL审计。

- [ ] 写非法／缺身份／其他mode／重复flag拒绝；`sameImmutableIdentityReusesSingleBuild` 确认同source/harness/fingerprint只build一次、两image ID相同；`distinctIdentityStillBuildsTwice`；真实相同SHA管理预检只产诊断（RF5）。只有validate＋management预检可接受双方同一当前产品，标记diagnosticPurpose=same-source-protocol-check；它不满足反事实产品差异／scan门禁，也不能进入Gateway或正式资格。正式组合对照仍要求真实B_current差异及scan证据。
- [ ] 写 `SourceAuditIsPrivateAndNeverQualification`：上述唯一诊断组合可先收集SQL而无分类清单，私有审计路径不得与公开output重叠或为symlink；正式／旧模式／缺固定身份一律拒绝。审计输出 sourceAuditComplete 只说明采集完整，fullBaselineComplete／fullComparisonComplete／formalQualified永远false，measureGateway calls=0，未归类原计数不隐藏。
- [ ] 写 `AnyManagementParityFailurePreventsGateway`：数据／权限／字节／Total／嵌套字段或库角色失败时measureGateway calls=0；成功才进入下一门禁；场景与档位不能丢失，scope/reference必须有非空实际数据，失败私有载荷保留。
- [ ] 写 `HistoricalNodeHeartbeatsStayOutsideExpiryBoundary`：普通夹具所有 history-node 的 LastHeartbeatAt=spec.WindowStart.AddHours(-1)，双源DTO保持原始同一时间和 Offline=true；原功能 fixture 中的在线／离线边界仍按当前真实120秒规则验证。长基线读取不得因两侧跨越过期边界制造字节差异，不修改产品Dto或关闭真实时钟。
- [ ] RED：`node --test scripts/performance/comparison.test.mjs scripts/performance/performance.test.mjs scripts/performance/current-business-parity.test.mjs`；当前无新模式或仍重复同SHA构建时失败。
- [ ] 组合分支每场景用候选完整schema seed→capture模板→每变体restore→prepare角色→ANALYZE→启动当前变体→完整DTO比较；Gateway控制库也照此处理。旧dump／compatible流程不改。SourceIdentity、overlay、CLI固定driver、pair时间顺序和资源采样保持；同完整身份复用真实build结果，记录reusedFrom，不能仅按src tree复用不同SourceRevision。新私有SQL审计分支只在明确诊断模式工作，记录两产品SHA和实际共用H指纹，在无分类清单时保留原始计数且不运行资格判断；生成清单后必须重新做分类完整的固定预检。
- [ ] GREEN：全部Node夹具回归和新owner相同SHA S管理预检通过，旧缺schema／DTO／restore失败仍可阻断。实际生产比较还只做未固定S诊断，不启动正式矩阵。暂不提交。

### Task 10：预算精确分类、完整回归及双提交审阅包

**Files:** H_work 新增 `scripts/performance/budget-failure-classifier.mjs`、`budget-failure-classifier.test.mjs`；证据目录保存登记副本、qualification输入、TRX／日志、原始失败、保护回执、产品与共享夹具精确补丁。

**Interfaces:** `classifyBudgetFailure({failure,registry,functionalProof,costProof,sourceIdentity}):{classification:"expected-counterfactual-cost"|"blocked",reason:string}`。failure含测试FQN／参数、异常种类、源码文件／行和断言归因；不能解析到唯一登记片段即blocked。

- [ ] 写 `FunctionalFailureOnSameLineIsBlocked`：同一行DTO Equal、Alert Clock Equal、Total、权限或异常不可因附近预算断言而免责；错文件hash／case参数／无功能或成本证明／candidate角色均blocked；准确登记成本可分类，但原TRX失败仍保留（RF4）。
- [ ] RED→实现→GREEN：`node --test scripts/performance/budget-failure-classifier.test.mjs`。只消费已批准的19源位置／31片段，不按类名、关键词或异常类型宽泛匹配。多断言同一行的模糊TRX需人工逐项归因证据，否则阻断并保留原失败。
- [ ] 对两个实现分别运行 `./scripts/check-contracts.sh domain`／`integration`／`gateway`、`./scripts/check-local-runtime.sh unit`、`node --test scripts/performance/*.test.mjs`、`"$WEBAPI_PNPM" --dir console build`。执行环境设置 WEBAPI_NODE／WEBAPI_PNPM 为已核对的本机程序；每次dotnet通过现有check-contracts独立owner。基线专用测试只在B构建，共享Performance测试覆盖到双方，候选src字节=C760；原预算测试文件不修改、不跳过，原失败和补充功能测试结果分开。
- [ ] 候选完整回归零失败／跳过／取消；基线功能和安全回归全部通过，原全量测试只可出现已唯一归因的成本失败。提交前原生功能验证使用直接复制实际角色源码的现有check-contracts测试流程，标明工作树来源。现有performance runner拒绝脏产品src的边界保持，不能用父H417 archive冒充尚未提交的基线；真实B/C的S共享预检与8认证各两次2.5秒诊断在任务11固定后执行。
- [ ] 新独立审阅覆盖14产品路径、权限／排序、7容器和完整目标结构、模板／role安全及失败分类；修复后重跑受影响测试。生成 `commit-review.json`、`baseline.patch`／`harness.patch`、两份文件清单及保护／秘密回执，两个父均H417。commit-review记录候选C760、两补丁SHA和精确路径；获准提交后增加真实baseline／harness提交字段。请求一次具体双提交授权前不暂存或提交；原HEAD／index保持。

### Task 11：获准固定快照后的全档前置资格

**Files:** 固定资格证据包；若出现新源码缺陷，只在已审阅任务路径修复并重走精确补丁／授权流程，不随手扩大范围。

**Interfaces:** 消费已授权并完成的真实B_current和H_align SHA，C_current=C760；双方共享实际固定H_align指纹和分类manifest。每个来源从Git archive构建，test资格快照将同一已固定共享测试覆盖到该角色，记录实际src/test/tool哈希和SourceRevision。

- [ ] 获得精确两个本地提交授权后，逐包验证父、文件、补丁、index及原保护，提交到两个隔离分支；验证提交blob与审阅包完全一致。禁止推送或合并main。未获授权则停在审阅包，不能创建“临时提交”绕过。
- [ ] 在固定资格快照重跑Task10的两角色回归／分类／构建，保存实际数量，不以旧2,826充数。CLI与程序集身份、14产品diff白名单、同H输入先验全部通过。
- [ ] 先用 `./scripts/check-gateway-query-performance.sh validate --diagnostic compare --baseline "$baseline_commit" --candidate "$candidate_commit" --harness "$harness_commit" --experiment current-business-combined-v1 --preflight management --sql-audit "$private_audit_dir" --output "$evidence_dir/source-audit"` 收集私有SQL并生成共同清单；审计不得冒充固定资格。三个commit变量从已完成的commit-review解析，candidate=C760；private_audit_dir为新0700私有目录，evidence_dir为新公开证据父目录，classification_file指向新审阅清单。两目录不重叠，审计子文件0600、按owner分隔，只复制SQL形状和出处，不复制账号秘密。实际H输入指纹必须等于H_align；清单逐模板绑定两source文件／行号并审阅固定哈希。未审阅共同清单前，不调用默认旧B分类清单的预检。
- [ ] 再用 `./scripts/check-gateway-query-performance.sh compare --baseline "$baseline_commit" --candidate "$candidate_commit" --harness "$harness_commit" --experiment current-business-combined-v1 --preflight management --classifications "$classification_file" --output "$evidence_dir/fixed-preflight"` 启动固定driver的全档管理入口。固定三身份与空证据目录必须核对；preflight明确阻止Gateway，数据重建后进行全部分类／业务门禁，不沿用审计的complete状态。
- [ ] 新owner的九个scene/scale模板及Gateway控制库分别验证克隆／role；受控资格驱动固定调用runComparison的管理预检，在compare调度下验证全部S／M／L、三场景、完整原正式管理样本及源/库证明，只设置preflight=management而不启动Gateway。记录各列表SQL及行量，超过原候选预算即阻断，不改字段或预算。
- [ ] 复验双方总SQL=权限+数据、未归类0；若M／L产生S审计未覆盖的新形状，仅新增对应私有诊断审阅，再整体固定共同清单并重跑所有受影响预检，不忽略新hash。小／大档EXPLAIN保持递归CTE只读／超时／多语句拒绝，故障、模板漂移、配置／ACL演练及原纯索引／兼容迁移门禁均有固定回执。
- [ ] 固定微基准S／M／L、代生命周期及真实B/C的S管理与8认证各两次2.5秒诊断通过后，准备正式运行审阅包：三个SHA、共用清单hash、G0–G4、未关闭事项、准确数据时间窗、环境／资源、原失败和owner清理。诊断使用明确validate，不算正式窗口；正式窗口仍0，不把相同SHA协议诊断当作优化验收。

### Task 12：另获授权后的原正式矩阵与工作包结论

**Files:** `docs/evidence/gateway-query-performance/current-business-combined-baseline/` 下独立运行证据，不覆盖任何旧目录。

**Interfaces:** 消费Task11审阅包；正式CLI `compare`使用同一三个固定SHA、`--experiment current-business-combined-v1 --classifications "$classification_file"`；不得加preflight或validation标签，也不得运行脏driver。

- [ ] 取得正式288运行授权并再次核验实际资源空闲、来源、配额、时间窗和所有门禁；未授权保持待执行。时间窗不足时重新seed并全套复验同源数据，不延长token／Snapshot产品限制或拼接过期证据。
- [ ] 完成原144对／288窗及原全部管理、SQL、微基准、代生命周期证据。重试保存失败原目录和失败窗；未完成／超时／意外响应／采样缺口不能填补为通过。客户端或后端饱和如实报告，不删除原结果。
- [ ] 独立核验完整性、收益和原预算，报告候选与反事实实际吞吐／P95／内存／SQL／行量，保留组合归因边界及未使用索引。G5完整不自动代表G6收益达标；未达标继续阻断工作包关闭。
- [ ] owner-only清理、实际秘密扫描、原649文件保护及最后独立审阅完成。说明4192仍未部署、两个原Minor实际处理结果和生产恢复未验收；不自动推送、合并或部署。

## 计划自检与执行交接

覆盖映射：G0→1／9／10／11，G1→4–8／10，G2→1–3／11，G3→5–7／9／11，G4→8／10／11，G5→12，G6→11／12；RF1–RF5均有对应命名测试。原schema／DTO／权限／index门禁与失败保留贯穿各阶段。

本轮只生成计划和源绑定预算登记，自检包括全部现有路径、接口字段、链接、约束、测试覆盖和权限阶段；不宣称RED／GREEN或原生资格已执行。登记制作首轮文本匹配失败已保留，修正了登记片段而未修改原测试。

请审阅本计划。确认后沿用已选当前会话方式执行任务1–10，随后以具体审阅包获取双提交授权；固定资格通过后再审阅正式运行。无需重新选择方法，不从本轮“确认设计”推导产品实施或运行授权。
