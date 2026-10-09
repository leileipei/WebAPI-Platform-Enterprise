# 环境访问地址 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 提供环境级公开/内网入口配置、外部前缀、真实 Route 地址和环境文档，并保留发布入口历史。

**Architecture:** 扩展环境元数据与独立入口上下文；纯函数统一规范化和拼接，服务端按实际 Scope 投影。读取运行 Snapshot 与工作 Route 时采用显式视图，元数据不进入 Gateway 编译。

**Tech Stack:** 现有 .NET 10、EF Core/Npgsql 10、PostgreSQL、React 19.2、TypeScript、Node 22+、xUnit；保持现有锁文件，不新增依赖。

**Spec:** [用户已确认的地址设计](../specs/2026-10-09-environment-access-address-design.md)。2026-10-09 用户回复“确认设计”。

状态：用户已回复“确认计划，按推荐执行”；选定 Native，正在隔离工作区执行。

## Global Constraints

- URL 列 varchar(2048)，可空；base_path varchar(512)，默认 `/`；access_address_revision bigint，默认 1。
- public URL 如已配置，生产必须 HTTPS；非生产和 internal 允许 HTTP/HTTPS；Origin 无 userinfo/query/fragment/非根路径。
- 外部 basePath 由反向代理剥离，保存不修改 Route/Snapshot、监听、DNS/TLS/LB，不联网。
- 旧请求遗漏新字段保留原值，显式 null 清空 URL；旧环境不推断地址，旧发布不补造入口历史。
- internal 字段仅环境详情中具备 environment.write 和真实 read_write Scope 时返回，不进入 Scope Tree/列表。
- 发布入口每个 Release 至多一条不可变上下文；地址元数据不改历史候选摘要和运行快照协议。
- 原始 OpenAPI、API Revision 与 Artifact 不因环境文档生成而改变；API 响应 no-store，示例不含凭证。
- 1440/1280、键盘、草稿冲突和撤权清空；原账号/SSO/数据与既存本地修改保留。

## Review Focus

1. 旧客户端保存环境时遗漏新字段，地址不能被清空；A2 字段存在性测试。
2. IPv6、默认端口、Unicode 主机及重复斜杠的规范化不能产生不同含义；A1 规范化及共享向量测试。
3. 运行快照包含工作区后来删除的 Route，running 仍按历史快照生成，不能混用最新版本；A3 视图测试。
4. 共享 Scope Tree 或旧异步响应泄露 internal URL；A2/A5 投影和撤权测试。
5. 发布重试与地址变化并发，历史上下文不能被覆盖或再次生成；A4 事务测试。

---

## 执行准备与验收约定

路径以隔离 enterprise 工作区为根。已读当前 main 的 `40759aac50f1a6281223b0ec0d45d58e6fb44cb5`；它与规格核对的 `4ceda71` 产品文件无差异，属于合并提交。执行前再次固定实际 SHA，不将变化中的 main 当验收身份。

计划批准及执行方式选定后使用 using-git-worktrees：先检测既有隔离，优先原生工具；从已核对提交创建本任务隔离工作区。纳入本次两份规格和计划，记录摘要；不得复制原目录未跟踪证据、密钥或其他本地修改。已有无关工作树不复用。

先运行 `./scripts/check-contracts.sh domain`、`integration`、`gateway` 和 `./scripts/check-console.sh` 记录基线。前者已有 UUID PostgreSQL/Redis 隔离及资源所有权清理；不使用共享 `check.sh` 容器做本批验收。Node/pnpm 若不在 PATH，通过 load_workspace_dependencies 查找后设置 WEBAPI_NODE/WEBAPI_PNPM，不安装未知版本。

每任务先写行为失败测试并运行 RED（零匹配、编译错误不算有效 RED），再实现、运行 GREEN。涉及已有行为的任务运行列明回归。任务通过后仅在批准的隔离分支提交本任务文件，提交名列于任务末；不自动提交原目录或推送。

所有测试命令均为未来执行步骤，本计划编写阶段未执行产品测试。

## 文件职责

| 文件组 | 职责 |
| --- | --- |
| Domain/Governance、Contracts/Governance | 纯地址规范、字段存在性和投影 DTO |
| Infrastructure/Governance | 环境元数据保存/读取和调用地址生成 |
| Infrastructure/Catalog | 环境 OpenAPI 副本和路由映射 |
| Infrastructure/Releases | 执行事务内捕获历史入口 |
| console/src/environments | 编辑、复制与异步权限边界 |
| scripts/delivery、tests/delivery | UUID 场景及验证证据 |

## Task 1 (A1)：地址规范与唯一拼接

**Files:** Create `src/WebApi.Contracts/Governance/EnvironmentAccessContracts.cs`、`src/WebApi.Domain/Governance/EnvironmentAccessAddressValidator.cs`、`src/WebApi.Domain/Governance/ClientApiAddressBuilder.cs`、`tests/WebApi.Domain.Tests/EnvironmentAccessAddressTests.cs`、`tests/fixtures/environment-access-addresses.json`。

**Interfaces:** `EnvironmentAccessSettings(string? PublicOrigin,string? InternalOrigin,string BasePath)`；`Normalize(EnvironmentAccessSettings input,bool isProduction) -> EnvironmentAccessSettings`；`BuildTemplate(string origin,string basePath,string routePath) -> string`；`BuildExample(string origin,string basePath,string routePath,IReadOnlyDictionary<string,string> values) -> string`。模板允许原 Route 占位符，示例逐参数编码；catch-all 不自动扩展为可执行地址，未完整提供值时只返回模板。

- [x] **Step 1：写断言。** `Assert.Equal("https://api.test/gateway/mes/orders/{id}",BuildTemplate("https://api.test","/gateway","/mes/orders/{id}"))`；根前缀不加双斜杠；示例 id=`a/b` 生成 `a%2Fb`；userinfo/query/fragment/`..`/`%`/反斜杠均抛 422。覆盖 HTTPS 生产限制、IPv6、默认端口、IDNA 主机、2048/512 边界。
- [x] **Step 2：RED。** `./scripts/check-contracts.sh domain --filter EnvironmentAccessAddressTests`；确认以上行为断言实际失败。
- [x] **Step 3：实现。** 用 URI 解析及严格前缀段验证完成 Normalize，单一字符串拼接；固定向量保存输入/规范输出/错误码，后续 Console 复用，不使用网络或相对 URL 覆盖路径。
- [x] **Step 4：GREEN。** 同一命令通过，含非法输入；原 SnapshotCompilerTests 和 PolicySnapshotCompilerTests 回归通过。
- [x] **Step 5：隔离提交。** `feat(environments): define access address normalization`。

## Task 2 (A2)：兼容保存、迁移与权限投影

**Files:** Modify `src/WebApi.Infrastructure/Persistence/Entities/EnvironmentRecord.cs`、`Persistence/Configurations/EnvironmentRecordConfiguration.cs`、`Persistence/Migrations/WebApiDbContextModelSnapshot.cs`、`src/WebApi.Contracts/Governance/GovernanceContracts.cs`、`src/WebApi.Infrastructure/Governance/GovernanceService.cs`、`src/WebApi.ControlPlane/Governance/GovernanceEndpoints.cs`；Create `src/WebApi.Contracts/Governance/OptionalJsonProperty.cs`、`src/WebApi.Infrastructure/Persistence/Migrations/20261009010000_EnvironmentAccessAddresses.cs` 及其 Designer、`tests/WebApi.Integration.Tests/EnvironmentAccessPersistenceTests.cs`；DI Modify `src/WebApi.ControlPlane/ControlPlaneApp.cs`。

**Interfaces:** `OptionalJsonProperty<T>(bool IsSpecified,T? Value)` 及转换器，只在属性出现时指定；创建/更新 request 追加三个 Optional 字段。`EnvironmentDto` 为公开投影；`EnvironmentDetailDto` 逐项保留 EnvironmentDto 的顶层字段并追加可选 GatewayInternalUrl，无权时 JSON 完全省略 internal 属性，不用 Environment 包装旧字段。`AccessAddressRevision` 独立递增；ETag 仍取环境 Revision。

- [ ] **Step 1：写断言。** `OmittedPropertiesPreserveSavedAddress` 旧 PUT 保存名称后地址未变；`ExplicitNullClearsOnlySelectedOrigin` 清空公开入口保留内网入口；`ReadScopeNeverReceivesInternalOrigin` 检查 detail、list、scope-tree JSON 不含该属性和值；`ProductionFlagValidatesExistingOrigin` 非法转换422且不写库；412无审计副作用；非地址更新不递增地址修订。
- [ ] **Step 2：RED。** `./scripts/check-contracts.sh integration --filter EnvironmentAccessPersistenceTests`。
- [ ] **Step 3：实现。** 属性存在性、A1规范化、权限投影、审计与迁移一起完成；普通 env PUT 沿用修订并发保护，不能声称新增幂等回执。迁移从旧结构增加空地址与默认前缀，不改 API/Route/Snapshot/节点行；规范化值相同时地址修订不增。
- [ ] **Step 4：GREEN。** 同上及 `--filter GovernanceTests`；迁移前旧请求、迁移后显式清空和角色撤销全部通过；ModelSnapshot 与生成迁移匹配。
- [ ] **Step 5：隔离提交。** `feat(environments): persist scoped gateway access metadata`。

## Task 3 (A3)：真实 Route 地址、示例与环境文档

**Files:** Create `src/WebApi.Infrastructure/Governance/EnvironmentApiAddressService.cs`、`src/WebApi.Infrastructure/Catalog/EnvironmentApiDocumentService.cs`、`src/WebApi.ControlPlane/Governance/EnvironmentAccessEndpoints.cs`、`tests/WebApi.Integration.Tests/EnvironmentApiAccessTests.cs`；Modify A1 Contracts、`ControlPlaneApp.cs`。

**Interfaces:** `AccessView=Working|Running`；`RouteAccessDto(Guid RouteId,string Method,string PathTemplate,string? PublicTemplate,string? InternalTemplate,long? RunningConfigVersion)`；`EnvironmentApiAccessDto(Guid EnvironmentId,long AccessAddressRevision,bool Configured,AccessView View,IReadOnlyList<RouteAccessDto> Routes)`。`GetAsync(Guid envId,Guid apiId,Guid? versionId,AccessView view,ActorContext actor,CancellationToken ct) -> Task<EnvironmentApiAccessDto>`；`EnvironmentApiDocumentService.BuildAsync` 同参数返回 `Task<JsonDocument>`。HTTP 默认 running，未知 view 422。

- [ ] **Step 1：写断言。** `RunningUsesSnapshotAfterWorkingRouteDeletion` 发布后删工作路由，running仍有原方法路径；`VersionMismatchDoesNotSubstituteLatest` 指定非运行版本不返回其他版本；`DocumentCopyLeavesStoredContractUntouched` 原始文档/Revision/摘要相同；无地址409，多Route逐条输出，歧义422，外组织404，internal无权省略；示例无 Authorization/Secret。
- [ ] **Step 2：RED。** `./scripts/check-contracts.sh integration --filter EnvironmentApiAccessTests`。
- [ ] **Step 3：实现。** running 读取数据库 Snapshot，并使用对应成功发布冻结版本契约；节点不一致标明运行状态不可确认，不把 desired 当全节点已应用。working读取授权 Route 与当前版本，返回明确工作标识。用已有契约解析器生成 OpenAPI 副本，servers=Origin+prefix、paths=真实路由；契约操作到路由不唯一时拒绝，不删操作伪造完整文档。两新GET no-store，满足规格§5全部权限。
- [ ] **Step 4：GREEN。** 同上与 Catalog/ContractManagement 已有相关回归；检查输出文档路径和方法与对应视图一致，HTTP响应不产生外部请求。
- [ ] **Step 5：隔离提交。** `feat(environments): generate route access and environment documents`。

## Task 4 (A4)：发布入口历史与重试保护

**Files:** Create `src/WebApi.Infrastructure/Persistence/Entities/ReleaseAccessContext.cs`、`Persistence/Configurations/ReleaseAccessContextConfiguration.cs`、`Persistence/Migrations/20261009011000_ReleaseAccessContexts.cs` 及 Designer、`src/WebApi.Infrastructure/Releases/ReleaseAccessContextService.cs`、`tests/WebApi.Integration.Tests/ReleaseAccessContextTests.cs`；Modify ModelSnapshot、`Releases/PublishCoordinator.cs`、`ReleaseService.cs`、`src/WebApi.Contracts/Releases/ReleaseContracts.cs`、`ControlPlaneApp.cs`、`src/WebApi.Worker/WorkerApp.cs`。

**Interfaces:** `CaptureAsync(ReleaseRecord release,EnvironmentRecord environment,CancellationToken ct) -> Task<ReleaseAccessContext>` 调用者已持环境锁/现有命令事务；`ReadAsync(Guid releaseId,ActorContext actor,CancellationToken ct) -> Task<ReleaseAccessContextDto?>`，内网额外投影。ReleaseDto 末尾追加可选 `AccessContext`、当前公开地址及 `AccessAddressChanged`，旧候选字节不变。

- [ ] **Step 1：写断言。** `DuplicatePublishKeepsFirstContext` 同幂等键发布仅一行；`AddressEditDoesNotRewriteReleaseHistory` 当前地址与历史分开；`LegacyHistoryRemainsUnknown` 旧Release为null；`RollbackCapturesCurrentEntry` 回滚不恢复旧地址；失败任务和并发捕获不留两行；SnapshotHash及CandidateHash与未加入元数据时一致。
- [ ] **Step 2：RED。** `./scripts/check-contracts.sh integration --filter ReleaseAccessContextTests`。
- [ ] **Step 3：实现。** 在 StartAsync 从 Ready 进入 Building 的同事务捕获，重入只读原行；恢复重试创建新Release时新记录注明 RecoveryOf，上一次上下文仍不可变；普通命令幂等重试与失败恢复是不同语义，测试分别覆盖。历史读取从关联Release权限取得Scope。
- [ ] **Step 4：GREEN。** 同上与 `--filter 'FullyQualifiedName~ApprovalTests|FullyQualifiedName~RollbackTests|FullyQualifiedName~ComparisonReleaseTests'`；旧发布/回滚、DI ControlPlane/Worker 均可启动。
- [ ] **Step 5：隔离提交。** `feat(releases): retain immutable gateway entry context`。

## Task 5 (A5)：环境编辑与调用入口 UI

**Files:** Create `console/src/environments/EnvironmentAccessEditor.tsx`、`access-address.mjs`、`access-address.d.mts`、`access-state.mjs`、`access-state.d.mts`、`ApiAccessPanel.tsx`、`console/tests/environment-access.test.mjs`；Modify `console/src/pages/Governance.tsx`、`ApiDetail.tsx`、`Routes.tsx`、`ReleaseDetail.tsx`、`console/src/api/types.ts`、`console/src/styles/tokens.css`。

**Interfaces:** `buildAddressTemplate(settings,routePath) -> string|null` 与A1向量相同；`accessReducer(state,event) -> state` 用identity/epoch丢弃旧响应；`EnvironmentAccessEditor({environment,onSaved,onClose})` 保留原环境字段；`ApiAccessPanel({environmentId,apiId,versionId,view})` 消费A3，复制仅公开无凭证文本。

- [ ] **Step 1：写断言。** Node向量测试与A1同输出；`assert.equal(next.internalUrl,undefined)` 在撤权时清空；迟到其他环境响应被拒绝；未配置复制按钮禁用；412保留草稿；历史与当前地址不能混标；未知/未运行版本无可执行链接。
- [ ] **Step 2：RED。** `"$WEBAPI_NODE" --test console/tests/environment-access.test.mjs`。
- [ ] **Step 3：实现。** 复用已有Editor保存与冲突流程；样例路径预览注明样例，真实接口Panel显式选working/running；内网字段只有专用详情请求返回，永不写localStorage。成功文案“地址已配置，连通性未验证”；提供公开调用示例和环境文档下载。
- [ ] **Step 4：GREEN。** 同命令与 `./scripts/check-console.sh`；UUID评审实例检查1440/1280、键盘、弹窗、地址清空、Scope切换、失权、模板复制，记录截图及API事实。
- [ ] **Step 5：隔离提交。** `feat(console): configure and preview environment api entries`。

## Task 6 (A6)：真实隔离验收、交付与本机升级准备

**Files:** Create `scripts/check-environment-access.sh`、`scripts/delivery/environment-access-scenario.mjs`、`environment-access-evidence.mjs`、`tests/delivery/environment-access-evidence.test.mjs`、`docs/deployment/environment-access-runbook.md`；Modify `docs/console-coverage.md`；复用既有 `scripts/runtime/build.mjs` 固定提交构建，不重复建设构建器；证据写 `docs/evidence/environment-access/<固定SHA>/`。

**Interfaces:** shell入口 `domain|integration|gateway|console|e2e|browser|verify`；前三项委托隔离check-contracts；`runEnvironmentAccessScenario({sourceRevision,directory,ports}) -> Promise<EnvironmentAccessProof>`；proof保存实际源码/镜像/迁移/浏览器身份与逐检查结果。`validateEnvironmentAccessProof(proof,files) -> {passed:boolean,errors:string[]}`，无截图或未记录运行事实必须失败。

- [ ] **Step 1：写证据拒绝测试。** `assert.equal(validateEnvironmentAccessProof(wrongSha,files).passed,false)`；伪截图、空检查、错误实例、原始凭证或无迁移证据失败。
- [ ] **Step 2：RED。** `"$WEBAPI_NODE" --test tests/delivery/environment-access-evidence.test.mjs`。
- [ ] **Step 3：实现。** UUID容器项目/随机本机端口/新测试账号，自有DB，清理按owner核验；使用实际公开入口配代理剥离前缀证明生成地址可调用；元数据保存本身不触发调用。固定提交构建、摘要和升级运行手册一并生成。
- [ ] **Step 4：GREEN与交付。** 全部check-contracts、check-console各跑一次，新增e2e/browser/verify全部通过且零skip；记录旧核心发布回滚、SSO和节点回归；仅此时更新Coverage。文档注明本机证据不等于生产DNS/TLS验收。
- [ ] **Step 5：隔离提交。** `test(environments): seal isolated access address delivery`。

## 安装和阶段完成门禁

- [ ] 独立整分支审查：源码、迁移、权限、旧客户端及实际证据均检查；问题修复后仅重跑相关项。
- [ ] 在原4192实例只读核对实际源码/镜像身份、业务数据、SSO、观测和卷；执行时再次查明实际路径，不猜测旧交付记录仍是当前状态。
- [ ] 在获准本机安装范围内，固定候选包、原卷/附件冷备及保留新数据的恢复路径，验证后按运行手册维护窗口升级；不重置环境和账号。原入口截图与真实请求必须证明实际安装成功。
- [ ] 记录 `sourceVerified`、`isolatedAcceptance`、`localInstalled`、`productionAcceptance` 分别为实际状态，禁止用测试通过代替安装；生产验收默认false。
- [ ] A阶段完成后从其已验证提交建立B阶段基线；A可以独立交付，B不影响A已完成能力。

## 规格覆盖自查

§1/2边界→全局约束；§3模型→A2/A4；§4规则→A1；§5接口→A2/A3；§6页面→A5；§7历史/晋级接口→A4及B计划；§8验收→A6/安装门禁；§9后续→保持延期。五项Review Focus均有明确行为测试。当前只完成计划自查，所有执行复选框保持未勾选。
