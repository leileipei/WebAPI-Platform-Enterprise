# CORS、IP 与 JSON Transform 独立验收操作说明

状态：实现与工作区回归已完成；固定镜像正式验收、1440px CUA 和最终独立审查尚未执行。本文件不是4192部署授权，也不是上线验收报告。第2包保持暂停。

## 范围与配置

平台支持9类策略。新增CORS、IP过滤和Transform沿用项目策略、路由绑定、提交时冻结、独立审批及双节点ACK流程，运行快照为2.3。JSON方向各自最多32项有序add/replace/remove/copy/move操作，JSON Pointer使用~0/~1转义。默认每方向256 KiB，允许1024至1048576字节且不能超过部署限额。配置本身32 KiB；原API契约仍需单独核对。

Transform可同时修改业务请求/响应头、后端路径、Query与JSON正文。认证、平台内部头、传输与表示头由平台管理。预览只执行同引擎JSON映射，不做真实转发；样例/结果仅存页面内存。请求映射错误按原因返回400/413/415/422，配额503；响应映射错误502或配额503。禁止把控制错误当作原业务JSON成功。

CORS来源必须明确；凭证模式不能使用通配来源。IP AllowList空集合不能用于开放访问。跨环境晋级需明确选择目标CORS/IP策略修订，完整Transform配置冻结；目标选择不能重写正文规则。工作策略保存成功与网关运行生效是两个阶段。

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

## 清理与后续集成

失败默认保留owner与私有材料。主动清理前确认保护收据和证据归档，调用`cleanupNetworkPolicyFixture({cloneDirectory,backend})`，先验证并清理Backend自身owner，再复用已有runtime/PROD清理逻辑；禁止直接全局prune或清理第2包资源。

正式验收后按Task17执行一次全分支独立审查、修复阻断问题并补验。提交、推送、main合并、4192升级分别按具体审阅结果授权，不由本说明默认执行。
