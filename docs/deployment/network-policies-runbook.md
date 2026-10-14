# CORS、IP 与 JSON Transform 独立验收操作说明

状态（2026-10-14）：固定提交 `226dfe450865cd322d81eb5747bec791a71f7519` 已通过五组3359项回归、独立TEST/PROD正式场景、15项1440px CUA、P3-01至P3-12严格证据门禁及正向SDK E2E。最终独立审查结论以[交付索引](../evidence/network-policies/2026-10-14-226dfe4/delivery-index.md)为准。本批未推送、未合并main、未升级4192；第2包保持暂停。

## 范围与配置

平台支持9类策略。新增CORS、IP过滤和Transform沿用项目策略、路由绑定、提交时冻结、独立审批及双节点ACK流程，运行快照为2.3。JSON方向各自最多32项有序add/replace/remove/copy/move操作，JSON Pointer使用~0/~1转义。默认每方向256 KiB，允许1024至1048576字节且不能超过部署限额。配置本身32 KiB；原API契约仍需单独核对。

Transform可同时修改业务请求/响应头、后端路径、Query与JSON正文。认证、平台内部头、传输与表示头由平台管理。预览只执行同引擎JSON映射，不做真实转发；样例/结果仅存页面内存。请求映射错误按原因返回400/413/415/422，配额503；响应映射错误502或配额503。禁止把控制错误当作原业务JSON成功。

CORS来源必须明确；凭证模式不能使用通配来源。IP AllowList空集合不能用于开放访问。跨环境晋级需明确选择目标超时、限流、CORS和IP策略修订，分别提供timeoutMs、burst/refillTokens/windowMs、allowedOrigins及ranges；其他策略有环境参数时也应按制品声明处理。完整Transform配置冻结，目标选择不能重写正文规则。工作策略保存成功与网关运行生效是两个阶段。

允许的Content-Language、Content-Disposition支持有正文和无正文请求的Set/Remove；认证、内部、传输及受保护表示头仍不能覆盖。匹配CORS派生路由的异常多方法预检受控返回403，IP拒绝优先；普通业务OPTIONS、未绑定CORS及原生404/405保持各自语义。

IP空AllowList不允许保存；明确的空DenyList表示允许所有已知客户端IP，未知IP仍拒绝。正式观测短窗口三源Available；晋级后夹具控制面查询端采样配置0.1、TEST网关1，不能将本夹具作为生产采样配置一致性证明。

## 固定版本与独立运行

先审阅精确补丁、父SHA、文件清单及日志，获得该具体本地提交授权。禁止把未提交代码标为旧SHA，禁止自动推送、main合并或升级4192。

固定提交后使用现有`buildRelease({revision,directory,root,nodePath,nugetSeedVolume})`构建同一镜像。由`verifyNetworkPolicySuites`归档该固定提交并运行domain/integration/gateway/console/runtime五组完整测试；runtime额外包含network-policies实际C#夹具。不能用工作区日志替换固定源详细日志。

独立项目目录必须以`webapi-enterprise-local-test-<新UUID>`命名；用`initializeNetworkPolicyFixture`初始化全新数据库，自动选择随机端口。禁止既有目录、受保护端口与恢复4192数据。调用前工作区源必须与固定提交一致。固定测试收据目录由`fixed-suites-pointer.json`明确记录，文件权限0600；场景开始会复验收据/原日志SHA/详细通过数。

Docker默认地址池耗尽时，先只读检查所有IPAM分配，再用现有`WEBAPI_CONTRACT_TEST_SUBNET=10.251.x.0/24`测试选项及`WEBAPI_RUNTIME_TEST_NETWORK_POOL=10.248`独立运行池。不能清理他人的网络。Backend/Keycloak夹具只在本轮独立owner网络中启动，默认行为保持兼容。

使用当前源码目录下的专用函数：

```js
import {initializeNetworkPolicyFixture,verifyNetworkPolicySuites,
  networkFixtureDirectory} from './scripts/network-policies/fixtures.mjs';
// revision为已授权的实际40位SHA，imageId取该固定构建的release.json。
// 各目录由操作人选择为本轮独立、私有目录，不复用已有owner。
await verifyNetworkPolicySuites({revision,directory:suiteDirectory,node:nodePath,pnpm:pnpmPath});
const cloneDirectory=networkFixtureDirectory(privateParent);
await initializeNetworkPolicyFixture({revision,imageId,cloneDirectory,releaseFile});
// 使用writePrivate写cloneDirectory/fixed-suites-pointer.json：{directory:suiteDirectory}
```

执行`scenario.mjs <revision> <imageId> <cloneDirectory> <fixtureDirectory>`。fixtureDirectory必须不存在。源TEST与目标PROD各有两个Gateway进程，控制面/Worker/Console及四个Gateway必须使用同一imageId。驱动必须真实核对响应、回显正文、计数、缓存、重试、配额、冻结、旧协议门禁、原Hash历史回滚及晋级；不能把未执行动作标actual。

## QA与证据

从本owner的私有browser-context.json读取入口和账号；使用独立浏览器会话，避免覆盖4192浏览器Cookie。执行P3-11的15项操作，在1440px宽度保留PNG截图与动作记录：创建、编辑、校验、复制、9绑定、两方向预览、null/大数、只读、412、dirty导航、scope晚到、Review、晋级映射、日志/Trace。不能仅截页面代替操作；无法执行时保持未执行。

验收前后用`captureNetworkPolicyProtection`只读记录主工作区与原实例文件/数据摘要；`makeNetworkPolicyProtectionEvidence`逐行保留复合主键和重复行。复用原动态列/追加规则；不复制秘密内容到公开目录。modelCoverage必须关联固定Integration日志中已通过的NetworkPolicyCapabilityTests.AllNineBindings_ManagementPipelineAndFrozenPolicyRevision；第2包暂停收据需保留。

`assembleNetworkPolicyEvidence`装配P3-01至P3-12、固定详细日志、实际镜像/容器、CUA及保护材料。目录/文件权限0700/0600，拒绝symlink与路径逃逸；秘密列表单独私有保存。失败保留材料，新一轮用新目录。

```text
node scripts/network-policies/acceptance.mjs <revision> <imageId> <directory> <cloneDirectory>
WEBAPI_NETWORK_POLICY_RESULT_DIRECTORY=<SDK挂载的材料目录>
dotnet test tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj -c Release --filter 'Scenario=NetworkPolicies'
```

E2E缺材料必须失败，不能skip。正式验收读取内部身份、HTTP、次数、JSON原字节、ACK与Hash，校验实际Console静态资源，检查7个应用容器及两环境节点。公开材料不包含密码、API Key、JWT、正文秘密或缓存HMAC。

## 管理员操作与实例

1. 在页头选择组织、项目和环境。进入“策略中心”，选择CORS、IP名单或Transform；只读账号可以查看，具有`policy.read`及相应数据范围的账号可以使用同引擎JSON预览，不能因此保存配置。
2. CORS逐行填写精确来源，例如`https://a.example.test`。使用credentials时不得填写`*`。IP名单按实际客户端地址来源选择模式，填写IPv4/IPv6 CIDR；可信转发地址模式还受部署的可信代理配置约束，不能通过客户端自填Header获得信任。
3. Transform分别添加业务Header、后端路径前缀、Query操作以及JSON请求/响应方向。Header、路径和Query只调整后端副本；调用方仍按原API路径、方法和授权访问。JSON操作按顺序执行，父节点和源路径须满足对应操作要求。
4. 请求体实例使用本轮合成回显样例：`{"n":9007199254740993,"nil":null,"items":[],"copy":"c","drop":true,"move":"m"}`。依次add `/items/-`值`"mapped"`、replace `/n`值`9007199254740993`、remove `/drop`、copy `/copy`到`/copied`、move `/move`到`/moved`。预览结果应保留大整数和null，得到`{"n":9007199254740993,"nil":null,"items":["mapped"],"copy":"c","copied":"c","moved":"m"}`。原始JSON值在编辑器中按文本填写，不先经过JavaScript Number转换。
5. 响应体是独立方向。回显实例对200响应add `/body/items/-`值`"response"`；实际后端须有对应结构。预览仅检验JSON规则；执行服务端校验、保存工作修订后，在Route策略绑定中选择策略。9类绑定各有0–1000显示优先级，固定执行阶段不随该数值改变。
6. 提交环境发布申请，核对冻结的策略修订、两方向正文规则、大小限制及对外Schema/示例。由不同账号完成独立审批；核对两个当前Gateway实例的成功ACK，再通过真实请求检查结果。不能以“保存成功”或单个ACK代替运行验收。
7. TEST晋级到PROD时，选择已经独立测试验收的制品，逐项明确目标Route、Cluster、环境参数策略、目标应用凭证和API授权。Transform两方向规则、方法/状态和字节上限整体冻结。目标两级审批及双节点ACK后仍需独立生产验证；本轮受控HTTPS入口验证并未配置企业DNS/TLS/LB。
8. 在访问日志中查看CORS/IP/Transform决策、固定原因和实际JSON操作数，再由TraceId打开真实Server/Client调用链。GET请求可以只有响应JSON操作，不能推断请求JSON也执行过。范围窗口大于新节点实际采集覆盖时，页面会显示“数据覆盖不完整”；缩短窗口并核对来源，不能把Partial当作Available。
9. 回滚沿用发布中心的独立回滚申请及审批流程，核对更高部署序列、当前实例ACK和原快照字节Hash。2.0/2.1/2.2历史快照不补写新字段；2.3需要完整能力声明。本轮旧节点测试是能力声明门禁，未安装旧版二进制。

截图、操作记录、固定日志和严格验收Hash见交付索引。公开实例仅含固定合成夹具，不包含真实业务正文、密码或调用凭证。发生412时保留输入，先查看最新修订，再明确采用或放弃；切换项目/预览方向会取消旧预览并清空样例结果。

## 依赖审计与失败保留

2026-10-14重新构建发现Pkcs 10.0.0触发NU1902/NU1903，实际恢复阶段失败，未开始该版本正式业务场景。经独立设计与精确14文件提交授权，固定Pkcs 10.0.13并重新生成12份受影响锁文件；未关闭NuGet审计或TreatWarningsAsErrors。固定07fb1fa的构建和全量回归重新通过。原阻断日志与先前场景诊断失败均保留，旧结果不替代本轮固定证据。

## 清理与后续集成

失败默认保留owner与私有材料。主动清理前确认保护收据和证据归档，调用`cleanupNetworkPolicyFixture({cloneDirectory,backend})`，先验证并清理Backend自身owner，再复用已有runtime/PROD清理逻辑；禁止直接全局prune或清理第2包资源。

正式验收后按Task17执行一次全分支独立审查、修复阻断问题并补验。提交、推送、main合并、4192升级分别按具体审阅结果授权，不由本说明默认执行。

第3包最终状态：原3项Important均已在226dfe4完成TDD修复及全套新固定验收；本次授权提交交付文档并合并、推送GitHub，实际集成状态以Git记录为准；4192未升级。
