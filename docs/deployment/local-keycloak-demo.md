# 本机 Keycloak 持久演示运维

当前入口与能力范围见[本机运行摘要](current-local-status.md)。4192已包含流量策略、系统设置、OIDC SSO及网关重启运行确认；应用提交与镜像以`.runtime/local/release.json`为准。本文件保留Keycloak原阶段实施与运维记录，旧0b42f9e版本限制只适用于文末标注的历史版本。

本批为现有 OIDC / PKCE 登录的本机部署扩展。Keycloak 使用 deploy/sso-images.lock.json 的 26.8.0 摘要，独立 PostgreSQL 和独立 owner。保留原 admin、审核员与网关业务版本。仅 localhost HTTP 演示，企业 HTTPS / IdP / 生产验收另行执行。

## 入口与秘密

平台：http://127.0.0.1:4192/ 。在登录页选择“本机 SSO 演示 · Keycloak”，跳至 http://localhost:4194 。4193保留为独立导航预览。演示账号 sso-demo-viewer，仅 Viewer 角色和指定环境 read Scope；无本地密码。

IdP 私有目录位于原运行目录的 idp 子目录；viewer-password 为演示密码，idp-admin-password 为独立 IdP 管理员密码，客户端秘密位于 client-secret。目录0700、文件0600，不进入 Git 或交付包。平台秘密只读挂载至控制面的 /run/sso/client-secret，容器UID10001，文件0600。不要打开或截取秘密内容作为验收截图。

## 正常维护

部署交付会将已提交工具通过 Git archive 固定到原运行目录 tooling/{工具commit}，提供原运行目录 manage.sh 和 manage-idp.sh。日常通过这两个入口维护；不要用旧工作区工具重建 runtime-config.json。

- manage.sh status / start / restart / stop：正常平台生命周期，自动重新读取 sso-deployment.json。
- manage-idp.sh status / start / restart / stop：独立 IdP 生命周期，stop 保留全部卷。
- manage-idp.sh provision --password-file {原admin私有密码文件}：测试连接、精确登记回调、启用身份源、显式绑定用户及只读 Scope。不设自动默认跳转，不按邮箱匹配，不自动开户。
- manage-idp.sh rollback --password-file {原admin私有密码文件}：先通过平台 API 停用身份源、撤销旧票据，再禁用连接配置并重建控制面；保留 IdP 数据与用户。
- manage-idp.sh connect：恢复连接后仍须 provision 完成连接测试与启用；旧票据不会复活。

维护入口需要 Node22+ 与 docker。WEBAPI_NODE 可指定 Node 的绝对路径。

## 备份与新 owner 恢复

平台冷备份使用 createBackup：停止写入，生成 PostgreSQL 逻辑dump及7卷tar；保留持久会话密钥、账号秘密、原审核员凭据、运行状态和 SSO 配置并逐文件 SHA256。停止后必须显式 start 原平台。

IdP：manage-idp.sh backup --target {全新私有目录}。停止 Keycloak 和其 PostgreSQL 后备份4卷及保护文件，完成后自动启动；失败仍执行恢复启动。既有备份不覆盖。

初始化失败后用start续跑：Prepared只复制既有私有输入，不重新生成秘密；SecretsReady继续启动；Ready不重复复制。恢复中断后start重新核验备份摘要、拒绝已经运行的服务，利用每卷staging/done标记续跑；完成卷不会重新覆盖，未完成文件先在本卷临时目录解包后移动。

恢复必须先恢复平台到新的 disposable owner 和空卷，再用同一批工具的 local-keycloak.sh restore --runtime-directory {恢复平台目录} --directory {全新IdP目录} --target {IdP备份} --port {独立新端口}。IdP生成新的project/owner，数据库与稳定 Subject从备份恢复；不登记原平台回调、不自动连接。平台恢复的 SSO 配置默认 enabled=false，更新owner和公开地址，不能直接指向原4192入口。

显式重接恢复副本：先connect，再用rebind-restored --password-file {恢复副本admin私有密码文件}。此入口只接受备份记录的原Provider和稳定Subject，先停用旧身份源与Viewer，通过现有API重绑到新Provider，再恢复原用户状态与Viewer/read权限。普通provision继续拒绝重绑已存在账号；不要使用旧Provider直接修改Issuer。

完成备份恢复验收后只清理本批 disposable platform / IdP，禁止自动删除持久项目。不要用数据库恢复覆盖产生了新业务写入的原实例。

## 实施证据

实际执行结果和脱敏截图见 docs/evidence/local-keycloak-demo；通过原平台数据摘要和秘密摘要证明原账号/角色/Scope/业务数据保留。结构连接测试的 Passed 只证明元数据和秘密引用可解析，真实登录须另行验证。

技术依据：[Keycloak容器](https://www.keycloak.org/server/containers)、[数据库配置](https://www.keycloak.org/server/db)、[启动realm导入](https://www.keycloak.org/server/importExport)。新realm用import启动；已存在realm不以重新导入覆盖，回调通过IdP管理API精确修改。

## 历史0b42f9e网关状态限制（当前已修复）

以下是原阶段版本的诊断记录。当前应用已加入重启后的运行确认，不以历史发布ACK推断当前实例运行状态；本机正常重启Ready已验证，原v4/seq4与发布记录保留。

原0b42f9e应用的发布ACK冻结于发布目标InstanceId。正常平台重启后新实例可加载相同v4/seq4并产生新Ready心跳，但ACK API会拒绝新的冻结目标，getRuntimeStatus总状态为Degraded。SSO接入和实际网关请求仍可工作。本批保留应用镜像与v4/seq4，不伪造ACK、不重新发布新业务版本；后续需单独修复运行就绪与发布确认的生命周期模型。不能将本批重启结果写成“总状态Ready”。
