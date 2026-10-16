# 第4包C：页面闭环与验收 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. 当前会话逐项执行，不派发子代理。

**Goal:** 完成应用概览/监控/审计、双Key轮换及最后有效凭证提示，建立40页454单元可追溯台账和隔离验收包。

**Architecture:** 扩展既有Application页面和查询，轮换协调现有发布/审批/双网关ACK，不另建发布引擎。固定版本验收覆盖账号和统计两个子计划，原环境只做保护核对。

**Tech Stack:** 既有React/TypeScript、管理API、Prometheus/Loki/Tempo、EF Core、Node验收工具、SDK E2E与浏览器交互。

**Spec:** [规格§10–15](../specs/2026-10-15-account-credential-closeout-design.md)及[总计划](2026-10-15-account-credential-closeout.md)；消费A2 Identity和B7 UsageQuery DTO，不另定义相同字段。

## Global Constraints

- app.read只允许业务摘要和Credential统计，不授予metric/audit/log/trace权限。各入口由服务端独立授权。
- AK掩码末4位；SK/APIKey只创建成功时内存展示一次，关窗/离开清除，不进URL、存储或公共证据。
- 已创建/工作配置不等于已发布；只有当前目标环境同一版本/序列/Hash双节点确认才表示新Key已上线。
- 原发布审批、无关API/App保留、窗口权限、回滚及JSON Transform等既有行为不变；无自动消费方迁移或撤销。
- “NoData”只有B7完整零覆盖可显示；Partial不能证明旧Key安全停用。原有历史未记录显示未知，不推造CreatedBy/RevokedBy。
- 1440px桌面优先，沿用现有tokens/组件/路由和高信息密度；键盘、焦点、窄窗及离开保护验收。
- 原40页454单元不可删项/合并计数；已关闭、部分、缺失、待复验、经确认延期必须以真实证据区分。
- 固定源码提交、推送、合并main、4192切换均须分别具有相应授权；第2包继续暂停。

## Review Focus

1. C1：窗口在确认期间自然到期、其他管理员改动、未发布替代Key必须改变/阻断旧风险确认。
2. C2：应用有权但其中环境/监控/审计无权时，只返回可见数据且禁止通过链接恢复越权查询。
3. C3：新Key已创建但尚未双ACK、消费方未确认、Partial数据都不能使向导宣称可安全撤销。
4. C4：原型截图、固定源码与真实运行证据属于不同身份；不得把截图覆盖率当成功能完成率。
5. C5：私有队列被克隆后不能向原CP补报；旧应用不能绕过新库强制改密标记进行所谓回退。

---

### Task C1：最后有效凭证影响预览和并发确认

**Files:**
- Create: `src/WebApi.Contracts/Applications/CredentialImpactContracts.cs`、`src/WebApi.Infrastructure/Applications/CredentialImpactService.cs`。
- Modify: `src/WebApi.Contracts/Applications/ApplicationContracts.cs`、`src/WebApi.Infrastructure/Applications/ApplicationService.cs`、`src/WebApi.ControlPlane/Applications/ApplicationEndpoints.cs`。
- Test/Create: `tests/WebApi.Integration.Tests/CredentialImpactTests.cs`。

**Interfaces:** `CredentialImpactRequest(DateTimeOffset ValidFrom,DateTimeOffset ExpiresAt,string Status)`；`CredentialImpactConfirmation(string ImpactHash,bool ConfirmCloseAllAccess)`；`CredentialImpactDto(string ImpactHash,long CredentialRevision,long ApplicationRevision,bool LastWorkingCredential,IReadOnlyList<Guid> AtRiskVisibleEnvironmentIds,bool PublishedImpactUnknown)`。
`CredentialImpactService.PreviewAsync(Guid credentialId,CredentialImpactRequest request,string? ifMatch,ActorContext actor,CancellationToken ct):Task<CredentialImpactDto>`；`RequireConfirmationAsync(Guid credentialId,CredentialImpactRequest request,CredentialImpactConfirmation? confirmation,ActorContext actor,CancellationToken ct):Task`在现有更新事务内调用。`CredentialUpdateRequest`末尾追加可选`CredentialImpactConfirmation? ImpactConfirmation=null`，原安全更新请求仍兼容，风险更新缺确认则返回409及预览入口。

- [ ] **Step1 RED:** `Unpublished_replacement_does_not_remove_running_risk`、`Changed_revision_or_window_invalidates_impact_hash`、`Explicit_close_all_requires_authorized_confirmation`。
  ```csharp
  Assert.True(preview.LastWorkingCredential); Assert.Contains(testEnvironmentId, preview.AtRiskVisibleEnvironmentIds);
  Assert.Equal(412, staleEtag.Status); Assert.Equal(409, changedRisk.Status);
  Assert.Equal("credential_impact_changed", changedRisk.Code); Assert.Equal(200, explicitlyConfirmed.Status);
  ```
  参数化撤销、缩短过期时间、未来生效空窗、两Key重叠、另一管理员同时改权限/发布、确认期间时间跨临界点；显式环境越权不得返回其ID。
- [ ] **Step2:** `bash scripts/check-contracts.sh integration --filter FullyQualifiedName~CredentialImpactTests`，预期RED。
- [ ] **Step3:** `POST /api/v1/credentials/{id}/impact`，使用原credential.manage权限和应用范围。计算当前工作有效窗口及可见环境已发布窗口；Hash绑定目标变更、目标/应用/相关资源修订、已发布版本/序列/Hash、风险窗口/集合，用CanonicalJson SHA256。时间本身不进Hash，随时间改变的风险判定/窗口集合进入；更新事务同锁内复验，ETag412优先，风险变409。可明确确认关闭所有访问，不增审批流程；撤销仍只是工作配置，生效走原发布。
- [ ] **Step4:** 重跑Step2及`bash scripts/check-contracts.sh integration --filter FullyQualifiedName~ImportAndCredentialTests`，PASS。旧客户端正常延长窗口不受额外确认阻断；有风险请求应受控拒绝。
- [ ] **Step5:** 保存风险真值表和确认Hash输入字段白名单，不含AK/SK，不提交。

### Task C2：应用摘要、Overview/Metrics/Audit和权限入口

**Files:**
- Create: `src/WebApi.Contracts/Applications/ApplicationOverviewContracts.cs`、`src/WebApi.Infrastructure/Applications/ApplicationOverviewQuery.cs`、`ApplicationAuditQuery.cs`。
- Modify: `src/WebApi.Infrastructure/Applications/ApplicationService.cs`、`src/WebApi.Contracts/Applications/ApplicationContracts.cs`、`src/WebApi.ControlPlane/Applications/ApplicationEndpoints.cs`、`src/WebApi.ControlPlane/ControlPlaneApp.cs`。
- Create: `console/src/applications/ApplicationOverview.tsx`、`ApplicationMetrics.tsx`、`ApplicationAudit.tsx`、`application-state.mjs`、`application-state.d.mts`、`console/tests/application-state.test.mjs`。
- Modify: `console/src/pages/Applications.tsx`、`console/src/api/types.ts`、`console/src/api/client.ts`。
- Test/Create: `tests/WebApi.Integration.Tests/ApplicationOverviewTests.cs`、`ApplicationAuditScopeTests.cs`。

**Interfaces:** `ApplicationSummaryDto(int VisibleActiveApiCount,int AvailableCredentials,int TotalCredentials,string Activation)`；`ApplicationOverviewDto(ApplicationDto Application,ApplicationSummaryDto Summary,CredentialUsagePage Usage)`消费B7；ApplicationDto追加可选`ApplicationSummaryDto? Summary=null`。
`ApplicationOverviewQuery.ReadAsync(Guid id,Guid? environmentId,UsageWindow window,ActorContext actor,CancellationToken ct):Task<ApplicationOverviewDto>`；`ApplicationAuditQuery.ReadAsync(Guid applicationId,int page,int pageSize,ActorContext actor,CancellationToken ct):Task<AuditPageDto>`沿用既有AuditDto。新增`GET /applications/{id}/overview`和`GET /applications/{id}/audit`。前端`usageCountText(count:string|null,state:string):string`禁止Number/parseInt处理计数。

- [ ] **Step1 RED:** `App_summary_uses_visible_distinct_active_api_windows`、`App_read_does_not_grant_metrics_audit_logs_or_traces`、`Audit_resource_filter_cannot_be_supplied_by_browser`。
  ```csharp
  Assert.Equal(2, summary.VisibleActiveApiCount); Assert.Equal("WorkingConfiguration", summary.Activation);
  Assert.Equal(403, unauthorizedAudit.Status); Assert.Equal(403, unauthorizedMetrics.Status);
  Assert.All(audit.Items, x=>Assert.Contains(x.ResourceId, serverResolvedResourceIds));
  ```
  Node测试`usageCountText('9007199254740993','Available')`保留全部数字、Partial文案含“已采集/至少”、null显示原因不显示0；旧字段LastUsed不得覆盖B7可见值。
- [ ] **Step2:** `bash scripts/check-contracts.sh integration --filter 'FullyQualifiedName~ApplicationOverviewTests|FullyQualifiedName~ApplicationAuditScopeTests'`及`node --test console/tests/application-state.test.mjs`，预期RED。
- [ ] **Step3:** 列表页SQL批量聚合每页摘要，无按应用/凭证N+1；详情保留原credentials/permissions/runtime，增加Overview/Metrics/Audit。Metrics仅复用现有带applicationId的可信观测查询及metric.read；无权限不预取数据。Audit服务端解析App/credentials/permissions关联资源并走原AuditAccessQuery/逐环境范围，客户端不能放宽资源谓词；创建/撤销人从可信审计动作取，历史缺失显示未记录，不推断Owner。保留Owner和停用操作原语义。
- [ ] **Step4:** 重跑Step2及`bash scripts/check-contracts.sh console`，全部PASS；验证跨环境、组织级应用(ProjectId=null)、分页和无统计能力节点状态。
- [ ] **Step5:** 保存DTO契约、权限矩阵和SQL数；页面实际浏览器验收在C5，不提前计为通过，不提交。

### Task C3：Credential表与可操作轮换向导

**Files:**
- Create: `console/src/applications/CredentialRotationWizard.tsx`、`CredentialImpactDialog.tsx`、`rotation-state.mjs`、`rotation-state.d.mts`、`console/tests/rotation-state.test.mjs`。
- Modify: `console/src/pages/Credentials.tsx`、`console/src/pages/Applications.tsx`、`console/src/api/client.ts`、`console/src/api/types.ts`。
- Test/Create: `tests/WebApi.EndToEnd.Tests/CredentialRotationFlowTests.cs`。

**Interfaces:** `rotationStep(state):'choose'|'create'|'publish'|'consumer'|'observe'|'revoke'|'republish'|'done'`；`canAdvanceRotation(state):boolean`；`maskAccessKey(value:string):string`仅显示末4位。state字段固定为old/newCredentialId、environmentId、first/secondReleaseId、first/secondAck `{configVersion:string,deploymentSequence:string,payloadHash:string,nodeIds:string[]}`、consumerConfirmed:boolean、usageState:string、oldRevoked:boolean；未获得的值null，ACK从可信发布详情获取，secret不属于state。

- [ ] **Step1 RED:** `Created_key_without_dual_ack_cannot_advance_to_consumer`、`Consumer_confirmation_is_required`、`Partial_usage_never_claims_old_key_unused`、`Changed_impact_requires_repreview`。
  ```javascript
  assert.equal(canAdvanceRotation(createdButUnpublished), false);
  assert.equal(canAdvanceRotation(twoDifferentHashes), false);
  assert.equal(rotationStep(twoMatchingAcks), 'consumer');
  assert.equal(maskAccessKey('abcdef1234'), '••••1234');
  assert.equal(canAdvanceRotation(noConsumerConfirmation), false);
  ```
  E2E断言无关API/App快照内容保持、双Key overlap真调用成功、第一次发布后新Key成功、撤销后未第二次发布旧Key仍按旧快照工作、第二次双ACK后旧Key401、新Key200；SSO/权限撤销/ETag冲突中断时不自动重放写操作。
- [ ] **Step2:** `node --test console/tests/rotation-state.test.mjs`，预期RED。SDK E2E待C5独立运行环境准备，不用跳过假装GREEN。
- [ ] **Step3:** 表展示AK掩码、窗口、LastUsed/可见环境计数、完整性、新建/撤销人可信来源；新建成功一次性Secret/APIKey沿用原安全组件。向导逐步选择旧Key、创建重叠窗口、跳转现有发布/审批并跟踪双ACK、人工确认消费方迁移、观察新旧计数、C1显式风险确认撤销、第二次发布及双ACK。Partial只告知不足，允许有权管理员明确承担关闭风险，不能显示“已证明无调用”。发布候选按既有选择规则保留无关资源，不能只发布新Key孤立快照。
- [ ] **Step4:** 重跑Step2、C1/C2后端测试及console完整套件；PASS。浏览器1440px逐步操作/SDK真实调用在C5验证。
- [ ] **Step5:** 保存步骤状态表、路由返回/刷新恢复策略和权限变化证据。URL可保留非秘密release/credential ID；secret不跨刷新保存，不提交。

### Task C4：40页454单元关闭台账及可验证规则

**Files:**
- Create: `docs/evidence/work-package4/requirements-closeout.json`、`docs/acceptance/account-credential-closeout.md`。
- Create: `scripts/account-credential/requirements.mjs`、`tests/account-credential/requirements.test.mjs`。
- Read-only source: `docs/evidence/work-package4/2026-10-15-readiness/original-40-page-requirements.json`及原DOCX，不改已批准索引。

**Interfaces:** `validateRequirementsCloseout(source,closeout,artifacts):{pages:number,units:number,statusCounts:Record<string,number>}`；每unit保留原稳定ID/页面/原文/段落索引，追加status、reason、implementationPaths、testNames、evidenceRefs、sourceRevision、runtimeOwner、verifiedAt。evidenceRefs必须有存在文件及SHA256，不能只填口头“通过”。CLI `node scripts/account-credential/requirements.mjs <closeout.json> <evidence-root>` 返回JSON，非法退出非0。

- [ ] **Step1 RED:** `all_original_units_are_preserved_once`、`closed_requires_matching_source_test_and_runtime_evidence`、`deferred_requires_recorded_user_decision`。
  ```javascript
  assert.equal(result.pages, 40); assert.equal(result.units, 454);
  assert.throws(() => validateRequirementsCloseout(source, closedWithoutEvidence, artifacts));
  assert.throws(() => validateRequirementsCloseout(source, silentlyDroppedUnit, artifacts));
  assert.equal(prototypeOnly.status, '待复验');
  ```
- [ ] **Step2:** `node --test tests/account-credential/requirements.test.mjs`，预期RED。
- [ ] **Step3:** 逐项核对40页并登记五状态，不批量默认关闭。Package4重点页1/18/19/20/34必须有本轮实现+测试+UI证据；其余可引用身份清楚的既有证据，版本差异未复核则待复验。API向导第五步既定延期、停用立即生效与不可变发布矛盾、第2/5包及生产集成边界单列保留；新增非P4缺口进入后续清单而非擅自实现。缺少用户延期决定只能“部分/缺失/待复验”，不可自标“经确认延期”。
- [ ] **Step4:** 重跑Step2及上述CLI，40/454均完整且无无证据关闭。C5运行后更新实际证据链接；此时台账允许保留待复验。
- [ ] **Step5:** 交付逐页关闭/保留原因和缺口数量，明确“454项已登记”不等于“454项已完成”；不提交。

### Task C5：精确审阅包、固定构建与独立全场景验收

**Files:**
- Create: `scripts/account-credential/scenario.mjs`、`evidence.mjs`、`acceptance.mjs`、`verify.mjs`、`sdk.mjs`、`tests/account-credential/scenario.test.mjs`、`evidence.test.mjs`。
- Modify: `scripts/runtime/acceptance-backup.mjs`（在已有LKG备份内验证新队列完整性，不创建另一个原环境备份流程）。
- Modify: `tests/WebApi.EndToEnd.Tests/ExternalScenario.cs`，只接入A5新账号HTTP改密及新场景参数；新增SDK测试文件见A5/C3。
- Create: `docs/deployment/account-credential-operations.md`、`docs/evidence/work-package4/final-summary.md`；实际结果按固定版本写入私有owner及脱敏证据目录，不预造结果。

**Interfaces:** `runAccountCredentialScenario({revision,imageId,directory,fixtureDirectory}):Promise<object>`；`validateAccountCredentialEvidence(proof,files,{revision,imageId,privateValues}):object`；CLI `node scripts/account-credential/acceptance.mjs <revision> <imageId> <evidenceDirectory> <fixtureDirectory>`；`verify.mjs <revision> <evidenceDirectory> <pnpmPath>`调用现有固定五组验证，显式`extraTestDirectories:['tests/network-policies','tests/account-credential']`。所有输入revision要求40位SHA、真实镜像标签和owner匹配。
`runSdkEndToEnd({revision,imageId,directory,fixtureDirectory}):Promise<object>`位于sdk.mjs，校验同owner网络/镜像/归档源码，为SDK测试只读挂载该owner的context.json及私有密码路径，通过`WEBAPI_E2E_CONTEXT_FILE`传入；不得调用有固定5092/4182端口和另起owner的旧scripts/e2e.sh冒充本轮环境。

- [ ] **Step1 RED:** `Missing_gate_wrong_owner_or_mixed_revision_is_rejected`、`Original_runtime_or_foreign_resource_cannot_be_cleaned`、`Secret_in_public_evidence_blocks_delivery`、`Restored_queue_cannot_report_to_original_control_plane`。
  ```javascript
  assert.throws(() => validateAccountCredentialEvidence(mixedRevision, files, context));
  assert.throws(() => validateAccountCredentialEvidence(missingCrashEvidence, files, context));
  assert.equal(result.failed, 0); assert.equal(result.skipped, 0);
  assert.equal(result.originalRuntimeChanged, false);
  ```
- [ ] **Step2:** `node --test tests/account-credential/*.test.mjs`，预期新增验收器RED；以真正失败输入证明门禁不是仅验证布尔标记。
- [ ] **Step3:** 实现工具并先做候选全部回归：console、runtime及新增Node、domain、integration、gateway五组零失败/跳过；八应用构建、锁定恢复、NuGet审计、SDK测试可编译、40页台账及秘密扫描。保存失败原文；不得关闭审计或放宽阈值解决失败。生成父SHA、精确文件清单/补丁SHA256、每文件Hash和验证清单，**此处停在本地快照授权门禁，未授权不提交**。
- [ ] **Step4（精确提交获授权后）:** 在隔离分支固定批准文件；用`buildRelease({revision,directory})`生成镜像、`node scripts/account-credential/verify.mjs <固定SHA> <私有证据目录> <已固定pnpm绝对路径>`重跑五组，不接受候选日志代替。建立新的空库TEST/PROD owner，动态端口、双Gateway、真实PG/Redis/IdP及观测源，所有资源通过既有fixture guard；禁止使用原4192或第2包owner。
- [ ] **Step5:** 运行P4-01–07：空库强制改密/旧库克隆迁移/原Hash与授权保持/SSO；真实Redis故障、审计回滚、并发Stamp/重置；双网关有效/无效Key、JWT/匿名、缓存/重试/503；真实重复批、伪造快照/窗口、实例退役；杀进程/断网/ACK丢失/磁盘故障/回退/容量/24h过期；UTC/溢出/清理并发/可见范围。时间旅行用独立fixture注入TimeProvider，不调整宿主系统时钟、不伪造签名时间；关键恢复至少一轮真实持久卷进程终止。
- [ ] **Step6:** 运行P4-08–09：TEST/PROD分别双节点ACK、跨环境审批、双Key两次发布与无关资源保留、原策略回滚；实际浏览器1440px完成改密/重置/Overview/数值/未知/Partial/Metrics/Audit/轮换/风险确认，键盘焦点/离开保护和窄窗。截图/SDK请求记录绑定SHA、owner、权限身份和UTC；私有Secret输入截图遮蔽或不截。
- [ ] **Step7:** SDK E2E由runSdkEndToEnd在独立SDK容器运行`dotnet test tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj --no-restore --logger 'console;verbosity=detailed'`，显式传入该owner的ExternalScenario配置和A5每用户passwordFiles；其余所需apiId/clusterId等字段沿用现有prepare-e2e上下文，管理员/审核者改密后才能造数据，不改成访问4192。新旧组合分别验证新CP+旧GW为Partial、新GW+旧CP有限排队/404；旧版本用固定226dfe4归档镜像，不能拿当前工作区冒充。
- [ ] **Step8:** 冷备恢复演练仅使用独立owner：DB+LKG/collector游标一致恢复不重复；克隆到新owner先隔离原网络/endpoint、重绑身份与水位，绝不把克隆队列报原CP。强制改密账号存在时旧应用降级必须阻断；文档给出先使用兼容新应用恢复的路线，不把数据库Down当安全回退。
- [ ] **Step9:** 运行严格acceptance CLI，要求全门禁证据/镜像静态资源Hash/原始测试计数零失败跳过/场景断言/截图索引/秘密扫描/台账一致。更新C4已实际完成条目，保留未完成项。复核main原文件hash/status、原4192控制清单/服务/账号/数据及第2包仍暂停；清理仅本owner，失败材料先保留再按所有权受控清理。
- [ ] **Step10:** 使用`superpowers:requesting-code-review`完成整体代码审查；只能在适用指令明确要求时使用代理审查，否则用可用审查工具。Important以上问题必须修复并重跑受影响固定门禁；新快照仍需精确授权。最终写`final-summary.md`及操作说明，明确实现/固定验收/未验证/延期，不自行推送、合并或部署。

通过标准：P4-01–10逐项有可核验原始证据；所有必要测试零失败/跳过；不要求虚构固定测试总数。任一缺报完整性、安全、权限、迁移、原环境保护或秘密门禁失败均阻断“第4包验收完成”。
