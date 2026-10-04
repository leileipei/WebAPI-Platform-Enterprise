# 独立本机完整运行环境 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking. Preserve the selected Native execution method in this session.

**Goal:** 将已交付真实核心、双网关、三源与站内告警部署为独立、持久、可恢复的本机环境，保留旧环境和数据。

**Architecture:** 新建固定 Compose 项目 `webapi-enterprise-local`，以不可变提交构建应用镜像和静态页面，使用独立数据库与持久卷。先初始化管理端，再由用户建立环境并显式绑定网关；生命周期命令精确管理本项目，真实验收使用随机同构项目。

**Tech Stack:** .NET 10.0.401 SDK（现有摘要）、ASP.NET/YARP/EF Core、PostgreSQL、Redis、OpenTelemetry Collector、Prometheus、Loki、Tempo、Node 22+、Python 3、Docker Compose；沿用锁文件，无新增第三方产品依赖。

**Spec:** `docs/superpowers/specs/2026-10-04-local-runtime-design.md`，用户已确认；规格提交 `b09528c`，产品基线 `df5f4d1`。

## Global Constraints

- 固定 Compose 项目名为 `webapi-enterprise-local`。旧项目、数据库、密钥与 4180/4181/5090 入口保留，新库不自动复制旧库。
- 默认控制台 `http://127.0.0.1:4190`，网关 A/B 为 `http://127.0.0.1:4196`、`http://127.0.0.1:4197`；端口可配置，冲突报错，内部服务不映射宿主端口。
- 应用运行 UID/GID 10001；秘密文件 0600；节点名称 `local-gateway-a`、`local-gateway-b`，秘密不同。未绑定环境时不得使用 Guid.Empty。
- `restart: unless-stopped`；正常启停不调用 `down -v`、不删除卷、不执行全局 prune、不安装 LaunchAgent。
- 告警默认槽 15 秒、查询延后 30 秒、租约 30 秒、并发 4；指标导出 15 秒、Trace 采样 0.1。
- Prometheus 保留 7d/2GB；Loki/Tempo 保留 168h，使用普通持久卷；Collector/Prometheus/Loki/Tempo 初始内存限制 256/512/512/768 MiB。
- 默认不创建示例业务资源、审核账号或规则；demo 仅提供内网示例后端。发布双独立审批、最少两节点、ACK、LKG 与并发校验保持。
- 不挂载共享源码、bin/obj 或依赖缓存到长期容器；同提交静态构建；用户库、配置、会话与秘密不入交付包。
- 旧库迁移、生产 TLS/HA、7 天实际保留、企业集成、外部通知、生产容量/SLA 均不在本次验收范围。

## Review Focus

1. 同名 Compose 资源存在但没有本阶段所有权标记：所有写操作应拒绝接管；Task 1/5 测试。
2. 0600 宿主秘密与 UID 10001 不匹配：只读按服务分发，网关不能读取数据库/另一网关秘密；Task 4 测试。
3. 重复 init 或 bootstrap 文件已删除：不换密钥、不重置密码、不新建第二管理员；Task 5 测试。
4. 端口被其他应用占用或已被本项目占用：前者启动前失败，后者重复 up 幂等；Task 5 测试。
5. configure 验证成功后环境停用，或重建过程中失败：不能报告 Ready、不能覆盖旧绑定或重新生成密钥，明确可重试状态；Task 6/7 测试。

## 文件结构与运行契约

脚本使用 Node 内置模块；shell 入口仅定位 Node 并传递参数，不拼接 shell 命令。模块之间传递对象，通过 `spawn/execFile` 参数数组调用 Docker/Git。配置 JSON 中不得出现密码或 cookie。

| 文件 | 职责 |
| --- | --- |
| `scripts/local-runtime.sh`、`scripts/runtime/cli.mjs` | 命令解析、退出码、依赖组合 |
| `scripts/runtime/state.mjs` | schema、目录/文件权限、原子保存与互斥锁 |
| `scripts/runtime/docker.mjs` | Docker 参数数组、所有权检查、精确对象清单 |
| `scripts/runtime/build.mjs` | git archive、独立构建、镜像/静态版本清单 |
| `scripts/runtime/lifecycle.mjs` | init/up/start/stop/restart/bootstrap 完成 |
| `scripts/runtime/configure.mjs`、`scripts/runtime/status.mjs` | 登录验证绑定、状态与就绪语义 |
| `deploy/compose.runtime.yml`、`deploy/compose.runtime.gateways.yml` | 管理栈与双网关 overlay |
| `deploy/compose.runtime.demo.yml` | 可选两示例后端，控制接口关闭，无宿主端口 |
| `deploy/runtime/Dockerfile`、`deploy/runtime/init-volumes.sh` | 发布产物运行镜像、精确卷/秘密初始化 |
| `src/WebApi.RuntimeTool/` | 无第三方包的内部 HTTP 健康探测工具 |
| `src/WebApi.ControlPlane/Security/PersistentDataProtection.cs` | 可选持久会话密钥配置 |
| `scripts/check-local-runtime.sh`、`scripts/runtime/acceptance.mjs` | 独立同构验收与精确清理 |
| `scripts/package-local-runtime.py`、`scripts/verify-local-runtime-delivery.py` | 不可变交付与完整性核验 |
| `docs/deployment/local-runtime-runbook.md`、`docs/evidence/local-runtime/` | 中文运行手册和真实证据 |

默认目录为工程根 `.runtime/local`，0700；标记 `owner.json={schemaVersion:1,projectName,ownerId}`，ownerId 是随机 UUID。生产命令不允许 `--project`；验收 harness 内部可注入 `webapi-enterprise-local-test-<UUID>` 与匹配目录。所有容器、网络与卷均含 Compose 项目标签及 `com.webapi.runtime.owner=<ownerId>`，初始化前检查已有对象；两标记任一不符即失败。验收不得利用环境变量直接把长期项目变成可清理项目。

`runtime.json={schemaVersion:1,projectName,ownerId,ports:{console,gatewayA,gatewayB},bootstrapUsername,binding:null|{environmentId,nodeNames,allowedOrigins},demoEnabled,releaseId,initialized,bindingPhase,pendingConfiguration:null|{binding,demoEnabled}}`；`release.json={schemaVersion:1,sourceRevision,createdAt,imageId,imageTag,dependencyImages,staticFiles,configVersion:1}`。秘密保存在单独目录，JSON 仅记录受控相对路径。状态读取不输出秘密内容。配置变更使用同目录临时文件、fsync/rename；每次写操作持有目录锁，失败或退出释放锁，陈旧锁按进程与所有权核验后处理。

退出码：0=命令成功；2=参数/配置不合法；3=依赖/端口/所有权前置检查失败；4=启动或服务就绪超时；5=认证或环境校验失败；6=迁移/构建失败。`status --json` 返回服务事实，未配置网关单独标记 `Unconfigured`，源失败标记 `Unavailable`；不能只用 HTTP 200 推导完整健康。

`RuntimeStatus={schemaVersion:1,projectName,sourceRevision,phase,services,sources,nodes,storage}`；phase 取 `Uninitialized|ManagementReady|Unconfigured|Registered|Ready|Degraded|Stopped`；services 每项为 `{name,state,health}`；sources 每项为 `{name,state,checkedAt}`；nodes 每项为 `{name,instanceId,desiredVersion,currentVersion,acknowledged,state}`；storage 每项为 `{name,volumeId,bytesUsed}`。无法读取的状态/用量使用 null 与明确原因，不能补0。

---

### Task 1: 运行状态与所有权边界

**Files:** Create `scripts/runtime/state.mjs`, `scripts/runtime/docker.mjs`, `tests/runtime/state.test.mjs`, `tests/runtime/ownership.test.mjs`。

**Interfaces:** `loadState(directory):Promise<RuntimeState>`、`saveState(directory,state):Promise<void>`、`withRuntimeLock(directory,action):Promise<T>`；`assertOwnership(state,resources):void`、`docker(args,options):Promise<{stdout,stderr,exitCode}>`。RuntimeState 与 release schema 如上；资源来自项目标签和精确同名对象的并集，任何所有权不符均拒绝。

- [x] 写失败测试：`unknownProjectIsNeverAdopted` 断言同名项目但不同/缺 ownerId 拒绝；`stateContainsNoSecrets` 断言 schema 拒绝 password/cookie；`stateRejectsSymlinkAndForeignDirectory` 断言符号链接及已有无标记目录不能初始化；`lockSerializesWriters` 断言并发写不丢字段。
- [x] 运行 `node --test tests/runtime/state.test.mjs tests/runtime/ownership.test.mjs`，确认因新模块缺失失败。
- [x] 实现上述模块，严格校验 UUID、端口 1–65535/互不重复、环境非空 GUID；所有权核验在任何 Docker mutation 前执行，不执行 shell 字符串。
- [x] 运行同一命令，全部通过；测试使用假 Docker 适配器，确认拒绝路径 mutation 调用数为 0。
- [x] 仅提交本任务文件：`feat: add owned local runtime state`。

### Task 2: 不可变运行镜像与内部探测

**Files:** Create `scripts/runtime/build.mjs`, `deploy/runtime/Dockerfile`, `src/WebApi.RuntimeTool/Program.cs`, `src/WebApi.RuntimeTool/WebApi.RuntimeTool.csproj`, `src/WebApi.RuntimeTool/packages.lock.json`, `tests/runtime/build.test.mjs`。

**Interfaces:** `buildRelease({revision,directory,nodePath,pnpmPath}):Promise<ReleaseManifest>`；运行镜像包含 `/app/{control-plane,worker,gateway,console-host,migrator,runtime-tool,demo-backend}`，静态目录 `/app/console-dist`。`WebApi.RuntimeTool probe <url>` 使用 5 秒 HttpClient 超时，禁重定向；2xx 返回 exit0，其余非0，不打印响应正文/凭据。

- [x] 写失败测试：`buildUsesResolvedCommitNotWorkingTree` 断言只使用解析后的 40 位 commit；`failedBuildKeepsCurrentRelease` 断言失败不替换 release.json；`rejectUnfrozenConsoleDependencies` 断言 package/lock 与依赖不匹配时失败。
- [x] 运行 `node --test tests/runtime/build.test.mjs`，确认失败。
- [x] 实现 git archive 到独立临时目录，后端 `dotnet restore --locked-mode` 后逐应用 publish；前端 frozen lock 构建，核验静态哈希。临时构建缓存可单独使用，但长期镜像只 COPY 发布目录。SDK 基础镜像取现有 lock；镜像默认 USER 10001:10001，probe 可作为健康检查执行，不读业务秘密。
- [x] 构建明确提交的镜像，检查其 USER、OCI revision、静态清单；运行 probe 对受控 200/503/重定向/超时 HTTP 端点，分别确认 exit0/non0。仅成功后原子保存新 ReleaseManifest。
- [x] 提交：`feat: build immutable local runtime images`。不改当前 core-test 镜像或挂载。

### Task 3: 持久会话与显式上游白名单

**Files:** Create `src/WebApi.ControlPlane/Security/PersistentDataProtection.cs`, `tests/WebApi.Integration.Tests/PersistentSessionTests.cs`；Modify `src/WebApi.ControlPlane/ControlPlaneApp.cs`, `src/WebApi.Infrastructure/Routing/UpstreamAddressPolicy.cs`, `src/WebApi.Worker/WorkerApp.cs`, `src/WebApi.Gateway/GatewayApp.cs`；Add focused tests to `tests/WebApi.Integration.Tests/CatalogTests.cs`, `tests/WebApi.Gateway.Tests/GatewayRuntimeTests.cs`。

**Interfaces:** `PersistentDataProtection.Configure(IServiceCollection,IConfiguration):void` 消费 `DataProtection:KeysDirectory` 与 `DataProtection:ApplicationName`，两者同时配置才启用，要求绝对目录与非空应用名；`UpstreamAddressPolicy.ReadAllowedOrigins(IConfiguration):string[]` 消费 `Upstream:RequireExplicitAllowedOrigins`，为 true 时缺失/空列表返回空允许集，为 false 时保持旧测试后端默认值。

- [x] 写失败测试 `sameKeysAndApplicationNameKeepCookieValid`：同数据库/同 keys 目录重建控制面后原 cookie `/auth/me` 为200；`differentApplicationNameRejectsCookie` 为401；`persistentSessionStillRequiresCsrfAndRejectsCrossOrigin` 两种非法写为403。测试自行建立两服务器，保持 cookie 原样，不重新登录。
- [x] 写失败测试 `explicitEmptyAllowlistRejectsTestBackend`：runtime flag=true 时默认测试上游为422；显式白名单匹配可用；flag 未设置保持现有行为；在网关测试中确认未授权 origin 的快照不能激活。
- [x] 运行既有集成/Gateway 检查脚本的上述过滤测试，确认新行为尚不存在而失败；读旧校验环境允许，但不得执行其数据清理。
- [x] 实现可选 AddDataProtection/PersistKeysToFileSystem/SetApplicationName 与三应用一致的白名单读取。保留 cookie、权限、CSRF、Origin、审批行为。
- [x] 运行新增过滤测试与现有 SessionTests/BootstrapTests/GatewayRuntimeTests，通过后提交：`feat: persist local sessions and require runtime allowlists`。

### Task 4: 持久编排、服务秘密与卷初始化

**Files:** Create three runtime Compose files, `deploy/runtime/init-volumes.sh`, `tests/runtime/deployment.test.mjs`；不修改现有校验 Compose 与 observability yaml。

**Interfaces:** Compose 输入为 runtime 模块生成的非秘密变量/配置文件，镜像用 Task 2 image ID；两网关 overlay 仅在 binding 存在时加载。服务专属秘密卷：postgres 仅 DB；control-plane 为 DB+nodeA+nodeB+HMAC+cursor；worker 为 DB+HMAC+cursor；gateway-a/b 各自 node+HMAC；migrator 仅 DB。引导密码仅一次性 migrator bootstrap mount。

- [x] 写失败测试 `onlyThreeLoopbackPortsPublished`、`unconfiguredRenderHasNoGatewayIdRequirement`、`runtimeSourcesUsePersistentVolumesNotTmpfs`、`allResourcesCarryOwnerId`、`gatewayCannotReadDatabaseOrOtherNodeSecret`、`secretsReadableByNonRootAndNotWorldReadable`。
- [x] 运行 `node --test tests/runtime/deployment.test.mjs`，确认缺少运行编排失败。
- [x] 实现 Compose：PG/LKG-a/b/DPKeys/Prom/Loki/Tempo 普通独立卷；internal services 无宿主端口；应用10001与只读rootfs/受控tmpfs；collector诊断reader和prometheus诊断job保留。PG768MiB、Redis128MiB、CP512MiB、Worker256MiB、Console128MiB、Gateway各512MiB，demo各128MiB，实际用量在验收记录。
- [x] 一次性 helper 在精确本项目卷内建立目录并按服务 UID 分发宿主0600秘密，输出文件0600且属目标用户；不打印文件内容。长期应用只挂载各自秘密卷为只读，不挂宿主全部秘密；helper root权限仅用于这些卷，结束后退出。引导密码的容器内副本使用独立临时卷，仅挂载到该次bootstrap migrator，结束后精确删除此临时卷；宿主引导文件保留给用户读取，complete-bootstrap再删除。使用现有第三方镜像实际 UID 为源目录初始化，不假设全都10001。
- [x] Compose config JSON 静态检查通过；随机隔离项目实际启动验证权限、probe健康和持久卷类型，重启源后目录不清空；退出精确清理该次合成卷。提交：`feat: compose persistent local runtime services`。

### Task 5: 幂等初始化与非破坏启停

**Files:** Create `scripts/local-runtime.sh`, `scripts/runtime/cli.mjs`, `scripts/runtime/lifecycle.mjs`, `tests/runtime/lifecycle.test.mjs`, initial `docs/deployment/local-runtime-runbook.md`。

**Interfaces:** `initializeRuntime(options,deps):Promise<RuntimeState>`、`operateRuntime(operation,state,deps):Promise<RuntimeStatus>`，operation 仅 up/start/stop/restart；`completeBootstrap(state,{credentialsSaved:true}):Promise<void>`。CLI：`build --revision <commit>`；`init --admin <name> [--password-file <path>]`；up/start/stop/restart/status；`complete-bootstrap --credentials-saved`。Node 路径可由 WEBAPI_NODE 显式指定，不假设系统默认版本。

- [x] 写失败测试：`repeatedInitDoesNotRotateSecretsOrCreateAnotherAdmin`；`deletedBootstrapFileDoesNotRebootstrap`；`foreignPortFailsBeforeMutation`；`ownPortsAllowRepeatedUp`；`stopNeverDeletesVolumes`；`failedMigrationDoesNotStartApplication`；`completeBootstrapDeletesOnlyOwnedPasswordFile`。
- [x] 运行 `node --test tests/runtime/lifecycle.test.mjs`，确认失败。
- [x] 实现前置检查、锁、端口占用归属检查、独立秘密生成、卷准备、PG/Redis启动、迁移/bootstrap、管理栈 up。init 强制非空管理员名；首建密码默认随机32字节Base64，0600，日志仅显示文件路径；用户名已记录时禁止换名触发新管理员。重复 init 校验状态及现有秘密，不生成新值。
- [x] 启动等待总预算180秒，单probe5秒；未配置网关报告管理端可用/网关未配置，不等待网关 ready。正常stop仅Compose stop，start/up复用相同配置；失败保留诊断与数据。写入手册上述精确命令、引导密码由用户读取方式及幂等说明。
- [x] 运行该组测试及随机项目真实两次init、stop/start；确认管理员/秘密指纹未变、卷名称和ID一致、无密码日志；提交：`feat: initialize and operate owned local runtime`。

### Task 6: 环境绑定、状态与安全重试

**Files:** Create `scripts/runtime/configure.mjs`, `scripts/runtime/status.mjs`, `tests/runtime/configure.test.mjs`, `tests/runtime/status.test.mjs`；Modify CLI 与运行手册。

**Interfaces:** `configureRuntime(state,{environmentId,allowedOrigins,username,passwordFile,demoEnabled},deps):Promise<RuntimeState>`；`getRuntimeStatus(state,deps):Promise<RuntimeStatus>`。CLI `configure --environment <guid> --origins-file <json-array-file> --username <name> --password-file <path> [--demo]`；status 可无登录报告容器/源/版本/存储事实，节点 ACK 使用本项目只读 SQL，非 HTTP live 推断。

- [x] 写失败测试：`inactiveOrUnauthorizedEnvironmentLeavesBindingUnchanged`；`differentEnvironmentRejectedBeforeRecreate`；`sameBindingDoesNotRotateSecrets`；`environmentDisabledAfterValidationNeverReportsReady`；`recreateFailurePreservesRetryableBinding`；`statusSeparatesLiveRegisteredAndReady`。
- [x] 运行 `node --test tests/runtime/configure.test.mjs tests/runtime/status.test.mjs`，确认失败。
- [x] 使用现有 LocalClient 登录固定控制台4190（或已配置端口），发真实 Origin 与CSRF，GET `/environments/{id}` 验证 Active，读取允许节点状态；不创建环境、不赋予权限、不保存cookie。白名单仅允许HTTP(S) origin，无userinfo/path/query/fragment；demo profile必须显式加入两demo origins，不偷偷扩充。
- [x] 原子保存已验证binding与配置阶段，重建CP/Worker加载新配置，再启动两网关；失败记录阶段供相同参数重试，保留秘密、LKG和此前binding，不宣称事务式容器回滚。无快照时节点 Registered/NotReady 是正确结果；只有已有发布快照且当前实例ACK匹配才为Ready。
- [x] status 显示源实际探测、运行版本、节点当前实例/期望版本/ACK、PG与源卷磁盘使用；不读出秘密。通过单测与真实同环境重试/未知环境拒绝后提交：`feat: bind local gateways and report runtime state`。

### Task 7: 同构真实验收与恢复手册

**Files:** Create `scripts/check-local-runtime.sh`, `scripts/runtime/acceptance.mjs`, `scripts/runtime/acceptance-scenario.mjs`, `tests/runtime/acceptance.test.mjs`, `docs/evidence/local-runtime/verification.json`；Complete runtime runbook。

**Interfaces:** `runAcceptance({releaseManifest,directory,projectName}):Promise<AcceptanceProof>`；仅接受 `webapi-enterprise-local-test-<UUID>`，directory basename 与 projectName 完全匹配且owner marker一致。AcceptanceProof含版本、实际chain、restart、failure、backupRestore、cleanup、preservedOldEnvironment、limitations，任一必需项失败 complete=false/non0。

- [x] 写失败测试 `fixtureCannotTargetPersistentProject`、`readinessAloneCannotPassAcceptance`、`cleanupRejectsForeignOwner`、`cleanupFailureMakesAcceptanceFail`。运行 `node --test tests/runtime/acceptance.test.mjs` 确认失败。
- [x] 构建同构随机项目，通过正常治理API创建合成组织/环境、独立审核账号与权限、API/消费方/生产审批链。沿用现有prepare-e2e/observability-scenario的请求结构，在新harness内适配本项目节点名/端口；不直接调用依赖旧Compose/共享bin的旧场景。
- [x] 真实双网关请求、三源收录、规则触发/恢复及人工Ack；验收采样1、指标1秒、槽2秒/延后5秒/forSeconds4仅保存在随机项目override，长期默认不变。等待按实际收录结果推进，追踪原HTTP请求Trace与安全日志，不仅合成OTLP。
- [x] 保存实际对象ID/版本/日志/Trace后全部stop/start；重建CP保留有效cookie并继续拒绝非法写；网关新InstanceId重ACK；核对持久数据和新采集。Redis重启后再发布一个真实版本，源故障/Worker恢复验证缺口与租约语义。
- [x] 编写中文离线完整备份步骤：显式stop本项目，PG dump、LKG/DPKeys/三源卷、配置/版本和必要秘密分别保存到0700目录；备份含秘密不得入源码包。随机恢复演练使用另一独立随机项目，从备份恢复上述持久数据并验证登录、对象、两网关LKG与源历史；不得覆盖长期项目。Redis无需恢复缓存。备份完整性通过文件清单哈希核验。
- [x] 捕获精确旧core-test容器/镜像/卷挂载和旧入口状态前后对照；随机项目只精确清理owner/project匹配的白名单卷/容器/秘密，finally执行且保留失败证据。使用CUA验证新控制台1440px的登录、环境、节点及观测页；不以协议测试代替真实UI。
- [x] 运行 `./scripts/check-local-runtime.sh unit`、`integration`、`e2e`；必要旧会话/网关回归通过，无跳过隐瞒；记录资源峰值及未验边界。提交：`test: verify persistent local runtime recovery`。

### Task 8: 长期环境启用、审查与独立交付

**Files:** Create runtime packaging/verification scripts, `docs/evidence/local-runtime/final-review.md`, `deliverables/manifest-local-runtime.json`；Modify `scripts/package.sh` only add `local-runtime <commit>` dispatch；不改旧打包逻辑或旧manifest。

**Interfaces:** `scripts/package.sh local-runtime <immutable-commit>` 输出 `deliverables/WebAPI_Enterprise_独立本机运行环境源码及验收.zip`；validator核验commit源文件逐字节、静态产物同提交、ZIP SHA、证据complete与秘密排除。用户环境的实际source/image与交付manifest分开记录，升级目标明确。

- [x] 写打包失败测试：不完整验收、秘密/用户配置混入、静态来源不符、旧交付包被替换均拒绝；可在 `tests/runtime/package.test.mjs` 使用最小git fixture验证。
- [x] 完成本分支一次独立整体代码审查（Native执行技能要求的review），关注所有权、秘密权限、bootstrap幂等、失败恢复、cookie隔离、源持久与真实证据；修复有根据的重要问题，针对性复验，不无理由重复整个旧阶段复审。
- [x] 从已通过验收的不可变提交构建长期版本，初始化并启动 `webapi-enterprise-local` 管理端与三源。管理员名通过 `init --admin` 显式提供；若用户尚未提供，交付精确命令并只请求此必要信息，不猜用户名。用户读取本阶段密码文件完成登录，建立/选择环境后提供ID与上游白名单，才绑定长期双网关。期间其余交付工作继续。
- [x] 以长期项目status事实区分已运行管理栈与已绑定双网关；未经绑定不得称完整环境已运行。用户提供配置后完成真实节点注册检查；若尚无发布配置，明确Registered/NotReady并给出页面发布路径，不自动造业务成功。
- [x] 从不可变提交打新包，执行 `python3 scripts/verify-local-runtime-delivery.py <zip> <manifest>`，输出源/静态一致、SHA、排除项、旧交付哈希未变。应用Docker镜像不强制塞入ZIP，包内记录可复现build命令与源提交。
- [x] 提交证据/手册/manifest，最终报告固定入口、当前实际运行状态、启停命令、密码文件位置（不显示内容）、已验证恢复链路与未验证边界。只在所有必需项真实通过且用户必要配置已完成时宣称独立完整运行环境完成。

## 计划自检与执行交接

规格1–3由Task1/4/8落实，规格4由Task2落实，规格5由Task5/6落实，规格6由Task3/4/5/7落实，规格7–8由Task4/6/7落实，规格9由Task7/8落实。五项Review Focus各有具体失败测试；变量、状态schema、命令、项目名和默认值一致。运行数据与合成验收分离，执行过程中需要用户提供的管理员名/环境ID/上游地址在Task8明确处理，不把等待时间当作授权或伪造配置。

计划已获用户确认并以Native执行。Tasks 1-7已完成，Task 8按“管理员名未提供则交付精确命令”分支交付；长期服务初始化和双网关业务绑定仍待必要输入，不能称完整长期环境已运行。封存结果见独立交付manifest及执行账本。
