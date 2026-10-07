# 跨环境审批中心 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 提供跨当前用户授权环境的本人待审批、本人已处理和全部可见审批申请，列表资格与既有办理规则一致。

**Architecture:** 从既有Release/ApprovalTask及冻结规则查询个人资格，抽取共享ApprovalEligibilityService，服务端授权后计数和分页。独立Console页面保留详情及原两级审批动作。

**Tech Stack:** 现有.NET10、EF/Npgsql10/PostgreSQL、React19.2/Vite6.4、xUnit/Node；不新增依赖。

**Spec:** [已确认正式规格](../specs/2026-10-08-notification-cross-environment-approvals-design.md)，提交`adc7dfd`、用户回复“确认”；本计划负责§8和§10审批部分。独立于SMTP/Webhook计划；共同交付见同日交付计划。

状态：用户已确认实施计划；正在执行。沿用Native，统一一次交付前整分支审查。

## Global Constraints

- 范围A只实现待办汇总，不复制或晋级DEV/UAT/PROD配置。
- 原生产两级、每级1–5人、冻结角色及候选、非申请人和独立席位规则保持，管理员不绕过独立审批。
- 列表/详情需release.read；PendingMine额外需approval.act和真实环境read_write Scope；不修改既有approve/reject权限契约。
- 每个Release一行；page>=1、pageSize1–100/默认50；CreatedAt DESC/ReleaseId DESC；查询预算10秒，不能部分返回伪造总数。
- 查询、标签计数、列表在同一快照；隐藏未授权环境名称/数量/申请人和风险内容；无读取权限的数据清空。
- 轮询5秒，仅可见页面；不新增SLA/审批过期、自动批准或审批提醒。
- 1440px优先、1280可操作；原账号/SSO/业务数据和主目录七项修改保留，QA只写自有UUID实例。

## Review Focus

1. 第51条才有本人可办任务、一级多席位、第二级角色用户：资格先筛选，不能第一页空就报无待办；A2测试。
2. 组织只读与特定环境可写并存：并集合格Scope而非选错第一条授权，自批仍拒绝；A1/A2测试。
3. 第一级多人并发批准与管理员兼任角色：每人占一个席位，第二级不能提前出现；A1测试。
4. 身份可读但无API风险权限：显示受限而非零风险，不曝光候选/批注/凭证；A2测试。
5. 切换Shell环境、迟到网络结果、抢先办理后保留批注与撤权清空：URL/身份绑定和安全保护不能互相抵消；A3测试。

---

## 文件职责与验证入口

与通知计划共用执行工作区及固定SDK/测试运行器；不依赖通知N1–N8的产品类型。Files路径以工作区为根；`N`使用通知计划注明的固定Node路径。基线先确认无tracked产品修改，原未跟踪资料保留。

| 文件组 | 职责 |
| --- | --- |
| Infrastructure/Releases/ApprovalEligibilityService.cs | 当前步骤、冻结角色、独立席位与现有动作权限共同规则 |
| Contracts/Releases/ApprovalInboxContracts.cs | 筛选、标签计数、窄列表项及资格DTO |
| Infrastructure/Releases/ApprovalInboxService.cs | 参数化授权查询、同快照计数/分页、风险投影 |
| ControlPlane/Releases/ApprovalInboxEndpoints.cs | 只读no-store入口，错误与会话保护 |
| console/src/approvals | 独立页面状态、URL筛选、操作及详情返回 |

## Task 1 (A1)：共享真实审批资格

**Files:** Create `src/WebApi.Infrastructure/Releases/ApprovalEligibilityService.cs`、`src/WebApi.Contracts/Releases/ApprovalInboxContracts.cs`、`tests/WebApi.Integration.Tests/ApprovalEligibilityTests.cs`；Modify `Releases/ReleaseService.cs`、`Contracts/Releases/ReleaseContracts.cs`和ControlPlane/Worker DI。

**Interfaces:** `ApprovalEligibility(bool CanAct,int? CurrentStepOrder,string? ReasonCode)`；`ApprovalEligibilityService.EvaluateAsync(ReleaseRecord,ScopeRef,ActorContext,CancellationToken) -> Task<ApprovalEligibility>`与`RequireTaskAsync(ReleaseRecord,ScopeRef,ActorContext,CancellationToken) -> Task<ApprovalTask>`；后者在治理锁内执行，精确保留现有异常类型及规则。ReleaseDto追加可选ApprovalEligibility，不改已有字段和候选hash；DTO只读调用要把身份传入，后台无人身份时不伪造资格。

同一服务提供内部可组合 `QueryActionableReleaseIds(ActorContext actor,IReadOnlyList<Guid> visibleEnvironmentIds) -> IQueryable<Guid>`：可见环境仅由可信查询层提供，资格仍在数据库谓词中校验身份、角色、写Scope、独立席位及当前步骤。EvaluateAsync和RequireTaskAsync使用这一谓词核对指定Release，再按原状态机返回原因/席位；A2在分页/Count查询中组合此子查询，不能另写一份资格规则或把全部ID拉进内存。

- [ ] **Step 1：写行为用例。** `MixedScopeUsesWritableEnvironmentGrant`：组织read+环境read_write时当前冻结角色`Assert.True(CanAct)`；`ApplicantAndUsedSeatAlwaysRejected`即管理员自批也403；`ConcurrentSeatsDoNotExposeStepTwoEarly`一级2席位只通过1个时`Assert.Equal(1,CurrentStepOrder)`；`ApprovalActionWithoutReleaseReadKeepsExistingContract`原动作权限不新增读取门槛，列表仍无读取权限不可见；`ComposableQueryMatchesCommandEligibility`在功能权限与冻结角色分属不同角色时、混合Scope时比较子查询命中与Evaluate/命令结果；撤权、停用范围、身份Inactive和错误角色不能办理。
- [ ] **Step 2：RED。** Run `./scripts/check-contracts.sh integration --filter ApprovalEligibilityTests`，补最小签名后确认真实断言失败。
- [ ] **Step 3：实现。** 抽取现有CurrentTaskAsync规则与authorization要求，命令调用RequireTaskAsync；窄Evaluate不得写任务或缓存权限，资格原因不包含受限角色详情。冻结模板不读当前ApprovalFlow替代。
- [ ] **Step 4：GREEN及审批回归。** 同上及`--filter 'ApprovalTests|ComparisonReleaseTests|PolicyReleaseTests|RollbackTests'`，原两级、风险冻结和回滚保持。
- [ ] **Step 5：提交。** `refactor(approvals): share frozen role and scope eligibility`。

## Task 2 (A2)：服务端跨环境分页、计数与风险投影

**Files:** Create `src/WebApi.Infrastructure/Releases/ApprovalInboxService.cs`、`src/WebApi.ControlPlane/Releases/ApprovalInboxEndpoints.cs`、`tests/WebApi.Integration.Tests/ApprovalInboxTests.cs`；Modify A1 Contracts、DI及必要索引迁移 `20261008020000_ApprovalInboxIndexes.cs/.Designer.cs`和ModelSnapshot（不改业务行）。

**Interfaces:** `ApprovalInboxFilter(string View,Guid? OrganizationId,Guid? ProjectId,Guid? EnvironmentId,string? Status,int Page=1,int PageSize=50)`；`ApprovalInboxCounts(int PendingMine,int HandledMine,int AllVisible)`；`ApprovalInboxItemDto`字段精确对应规格§8.2；`ApprovalInboxPageDto(PageResult<ApprovalInboxItemDto> Page,ApprovalInboxCounts Counts)`。`ApprovalInboxService.ListAsync(ApprovalInboxFilter,ActorContext,CancellationToken) -> Task<ApprovalInboxPageDto>`。服务端参数错误422、无权ID404、Inactive会话401，no-store。

- [ ] **Step 1：写行为用例。** `MineOnSecondPageCountsBeforePagination`种子51条含多席位，`Assert.Equal(expectedVisible,total)`且不能按截断列表计数；`AllOrganizationsRespectFunctionalAndScopeGrants`核对跨组织角色及真实Scope，不能泄漏无权名称；`HandledHistoryRequiresCurrentReadScope`、`RoleChangeMatchesA1Eligibility`；`RestrictedRiskHasNoCountsOrComments`断言受限而不是0；`CountsAndRowsUseSameSnapshot`控制并发完成避免标签计数与列表混合时点；`InvalidFilterDoesNotEchoForeignScope`统一404；10秒超时错误而非空成功。
- [ ] **Step 2：RED。** Run `./scripts/check-contracts.sh integration --filter ApprovalInboxTests`。
- [ ] **Step 3：实现。** EF/参数化SQL投影可见环境、审批任务和冻结角色集合，分类资格与A1同一规则；数据库先授权/分类再排序Count/Skip/Take，不读取全部Release候选或逐条ReleaseDtoAsync。可见风险以现有API/version/schema实际权限投影，未授权不返回counts/comment。同快照产生三个标签总数，status只过滤当前列表并返回当前筛选元数据。
- [ ] **Step 4：GREEN与查询计划。** 同上及A1测试，检查50/100分页、授权集合与多任务去重；对自有大数据fixture记录EXPLAIN与查询次数，不能用无界内存加载解决分页；全部通过/零skip。
- [ ] **Step 5：提交。** `feat(approvals): query personal tasks across authorized environments`。

## Task 3 (A3)：独立审批页面、操作及跨环境返回

**Files:** Create `console/src/approvals/{api.ts,inbox-state.mjs,inbox-state.d.mts,ApprovalFilters.tsx,ApprovalActionDialog.tsx}`、`console/tests/approval-inbox.test.mjs`、`scripts/approvals/{scenario,evidence}.mjs`、`tests/approvals/evidence.test.mjs`；Modify `pages/{Approvals,ReleaseDetail}.tsx`、`main.tsx`、`Shell.tsx`及styles。

**Interfaces:** `parseApprovalQuery(string) -> ApprovalInboxFilter`、`approvalQuery(filter) -> string`、`inboxReducer(state,event) -> state`按actorAuthority/epoch过滤旧响应；`ApprovalActionDialog({item,onResult,onClose})`调用原approve/reject，沿用同操作幂等键；详情链接携带经验证的returnTo（只允许本机/approvals路由），真实Scope导航不借当前Shell推断。`runApprovalScenario({revision,imageId,cloneDirectory}) -> Promise<ApprovalScenarioResult>`；`validateApprovalEvidence(proof,files,identity) -> AcceptanceResult`。

JS AcceptanceResult固定 `{passed:boolean,errors:string[]}`；ApprovalScenarioResult含内部revision/imageId、查询计数/实际动作checks、截图与动作manifest及逐文件摘要，不能只返回“passed”标签。

- [ ] **Step 1：写行为/证据用例。** `pendingAcrossShellEnvironmentsRetainsUrlFilter`、`lateResponseCannotReplaceNewAuthority`、`conflictKeepsCommentButLostReadClearsIt`、`serverEligibilityControlsBothListAndDetail`；`assert.equal(request.view,'PendingMine')`默认；伪截图/错提交/未取得服务端计数的evidence必须throw，外部returnTo拒绝。
- [ ] **Step 2：RED。** Run `N --test console/tests/approval-inbox.test.mjs tests/approvals/evidence.test.mjs`。
- [ ] **Step 3：实现。** 独立Approvals替代Releases approvalOnly，3标签及Scope筛选/真实环境风险步骤/等待时长，5秒可见轮询；服务端资格控制动作，412/409保存批注但读取撤权清空。Shell变化不隐式重置跨环境筛选，返回URL维持筛选及页码。原详情仅补资格和返回，不更改发布动作语义。
- [ ] **Step 4：GREEN及实际操作。** 同上与`./scripts/check-console.sh`；自有UUID克隆新建 `approval-<uuid>-applicant/reviewer/security/reader` 四类账号及预先声明Scope：申请人可创建、两独立审核者各有各步骤角色+写Scope、reader仅read；混合Scope专用新账号同时有组织read与一个环境read_write，不给reader扩权。验证跨至少两环境、51条分页、一级多席位、只读/自批/撤权、1440/1280焦点/弹窗/批注/跨环境详情返回。可独立于通知模块验收。
- [ ] **Step 5：封存提交。** `feat(console): deliver cross-environment personal approval inbox`；实际截图/动作身份脱敏保存 `docs/evidence/approvals/`，不把模拟前端请求当真实浏览器证据。

## 覆盖与自查

规格§8.1→A1/A2；§8.2→A2；§8.3→A3；审批相关§10→各任务及共同交付D2/D3。五项Review Focus都有实际断言；页面“All”含有审批任务的可见申请，不含所有发布记录。该模块不新增通知类型、不依赖N模块，不用前端逐环境拼列表。
