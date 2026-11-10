# 核心闭环配置与运维

版本以 [global.json](../global.json)、[Directory.Packages.props](../Directory.Packages.props)、[console/package.json](../console/package.json)和锁文件为准。主题手册及历史安装结果从[运维目录](deployment/README.md)选择，本页不声明持久服务已采用这些版本。

## 环境与固定依赖

.NET SDK 10.0.401，目标 .NET 10，EF Core 10.0.12、Npgsql 10.0.3、YARP 2.3.0、StackExchange.Redis 3.3.1；所有 NuGet 使用集中版本及 packages.lock.json。React 19.2、TypeScript 5.9.3、Vite 6.4.3、Tabler 3.48，前端用 pnpm-lock.yaml 锁定。固定镜像摘要以 deploy/compose.test.yml 和 deploy/compose.e2e.yml 为准，不能把标签最新版本替代锁定摘要而不回归。

测试 Compose 使用固定 SDK 镜像与源码挂载；[运行镜像构建](../scripts/runtime/build.mjs)和[运行部署](../deploy/compose.runtime.yml)另有固定源码的应用镜像流程，不能把所有安装都称为 SDK 挂载模式。仓库也已有观测组件与配置，但这些实现及本机 loopback 验证不代表企业生产镜像、TLS/HA、外部密钥管理或容量已验收。

## 显式初始化

1. 将数据库密码写入受限文件并使用 `WEBAPI_DB_PASSWORD_FILE`；不要把密码写进 Compose、命令参数或源码。数据库连接字段由 DatabaseConnectionSettings 解析，迁移程序必须使用同一目标。
2. 对全新数据库执行 Migrator 的 `--seed-catalog`；首管理员只能通过显式 `--bootstrap`、`WEBAPI_BOOTSTRAP_USERNAME` 和 `WEBAPI_BOOTSTRAP_PASSWORD_FILE` 创建。没有默认账号或默认密码。完成初始化后撤掉初始化密码文件和相关环境设置。
3. 平台管理员创建组织、项目与环境。真实人员的角色和 Scope 应按管理员授权配置；交付不会替你创建持久审批人员、授予业务访问或填入企业连接。
4. 在环境上配置审批模板。Production 必须两级顺序审核，申请人不能审批，同一账号不能占两席。模板提交时冻结，后续改模板不改已提交申请。

## 核心服务配置

|服务|必须配置|说明|
|---|---|---|
|ControlPlane / Worker|WEBAPI_DB_HOST、WEBAPI_DB_NAME、WEBAPI_DB_PASSWORD_FILE|各进程共用源库；数据库凭证仅文件读取|
|ControlPlane|Nodes:Enrollments:{n}:EnvironmentId / NodeName / SecretFile|每节点单独身份，与环境和名称绑定|
|ControlPlane / Worker / Gateway|Upstream:AllowedOrigins:{n}|必须显式允许后端 origin，禁止任意上游地址|
|Worker|Redis:Connection、Publish:AckTimeoutSeconds|超时写入每次发布事实；改变设置不延长已有截止时间|
|Gateway|Gateway:EnvironmentId / NodeName / SecretFile / LkgDirectory / ControlPlaneUrl、Redis:Connection|每进程独立 LKG 持久卷；重启生成新 instanceId|
|ConsoleHost|Console:DistDirectory、Console:ControlPlaneUrl|提供前端静态文件和同源 API 代理，保留原 Host|

前端本地 Vite 默认端口 4181，代理 5090。集成测试控制面另有动态端口，不得从页面名称猜环境 ID。ConsoleHost 的目标必须与用户访问域的同源部署方案一致；Cookie、CSRF 和 Origin 校验仍启用。上线域名、反向代理信任边界及 HTTPS Cookie 必须按目标环境核验。

## 发布与故障处置

工作区保存不会改变正在代理的流量。Submit 冻结候选、完整资源修订与审批模板；Worker 构建前重新检查实际发布人权限；PostgreSQL 同事务写 Snapshot 原字节、期望指针、递增序列、冻结实例、审计与 Outbox。Redis 是可重建缓存。

只有所有冻结实例给出有效 ACK 才成功。一个节点确认、心跳 Ready 或 health 200 都不等于完整发布成功。失败后各节点可能运行不同版本；先看节点实际版本/序列，再决定创建独立恢复重试或经两级审批的历史回滚。不能手工把失败记录改成功或补 ACK。

回滚复用历史精确原字节/hash，configVersion 可下降，deploymentSequence 必须增加。恢复重试保留原 Failed 与旧 ACK，建立新的 RecoveryOf 记录及当前冻结实例。进程重启后新实例不能填旧确认席位。LKG 不可写或目标验证失败时继续旧有效流量，不发成功 ACK。

## 备份、恢复与日志

PostgreSQL 是业务与发布事实源：备份源库、迁移历史、Snapshot bytea、审计、节点身份摘要和 Outbox；凭证/节点秘密须由受控密钥管理独立备份。两个网关 LKG 卷分别保存 current/previous 文件。不要只备份 Redis。

恢复前先核对[运行工具的预算与硬配额阻断条件](deployment/local-runtime-runbook.md#恢复目前的阻断条件)和[数据库恢复约束](database-core.md)。当前默认硬配额集成缺失，不能直接执行旧恢复示例或用合成证明代替。具备有效恢复前提后，才按已批准方案停写、核对一致性点、迁移、期望指针/LKG/节点事实并通过正常协议协调；本地测试不证明目标备份可恢复，目标环境仍需记录 RPO/RTO。

管理 Snapshot 下载是脱敏查询视图，不能拿它充当网关的原字节 LKG。内部节点 desired 端点携带完整运行校验材料，限制在专用网络；日志、截图、工单不得包含 API Key、Cookie、节点 Secret、初始化密码或凭证摘要。故障代码按白名单返回；TraceId 可用于审计定位。

## 临时 E2E 清理

[核心闭环 e2e.sh](../scripts/e2e.sh)每次生成随机 `webapi-enterprise-e2e-*` 项目，记录在浏览器 metadata 或进程环境。退出路径会尝试删除该精确项目的容器、网络、卷及随机秘密，仍须核对结果；不能用退出码代替清理证明。

[运行工具验收](../scripts/runtime/acceptance.mjs)是另一条入口：目前还受恢复集成保护条件阻断，其结束逻辑保留夹具供复核，不能套用核心 E2E 的清理说明。

强杀进程可能留下测试卷。仅在核对**本次精确项目名**、确认其中全是可丢弃合成数据后，使用 Compose 的同一配置/环境清理该项目。禁止按前缀批量清理、禁止清理持久 core-test 或企业数据库。不要将 `.runtime` 或 `.secrets` 送入交付包。

AMD64 编译/运行、真实企业网络、TLS、数据库 HA、容量压测和生产密钥轮换仍为目标环境验收事项。
