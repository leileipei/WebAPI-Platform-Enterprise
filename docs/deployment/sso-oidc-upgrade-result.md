# SSO/OIDC：4192 本机环境升级结果

2026-10-06完成本批一次本地提交、不可变构建、原 owner 冷备份、隔离恢复与回退演练，随后升级现有4192。原账号和业务数据保留。

## 当前状态

- 控制台：http://127.0.0.1:4192/settings/sso，原admin及原密码；双网关4196/4197保持运行。
- 固定提交 `0b42f9e11977f8453887d6b23e45192ab2e42d61`，feature/sso-oidc，仅127个已审阅源码/工具变化文件。没有push/merge；本轮新说明和证据未自动二次提交。
- 新镜像 `sha256:2bcd8799e48b3452970045d619c274827f74fe866cb7f4345239a0cac2b1197a`，从Git archive构建，锁定依赖，不借用SSO测试镜像。控制面、Worker、控制台及双网关镜像一致。
- 最终复核2026-10-05T16:28:28.114Z：Ready，13服务运行，Collector及Prometheus/Loki/Tempo可用。当前新进程双ACK v4/seq4，原/orders双网关HTTP200。
- 工作候选逐项等于原v3批准业务，原两名不同审核人正常完成两级审批，再发布等价配置。没有直接改库或沿用旧进程ACK。新增发布、快照、登录审计和心跳是正常运行记录，详情见publication.json。

## 数据与身份边界

新增20261005120000_SsoOidc、20261005123210_SsoDisplayEmail、20261005152401_SsoSecretVersion三项迁移：四个SSO表、本地账号邮箱唯一索引及受保护指纹列。四表均0行，不创建默认Provider、外部绑定或合成用户。原3账号、密码/秘密、角色/权限/Scope、设置和业务资料，以及原v1至v3快照行与字节摘要保全。

原持久DataProtection keyring与应用标识继续使用，升级前本地会话在升级后验证通过。FixtureEnabled保持false；没有配置企业PublicBaseUrl、AllowedOrigins或SecretFiles。当前可使用本地登录及SSO治理页面。企业登录还需受信HTTPS控制台Origin、精确Issuer/端点Origin、Client ID、受控部署秘密文件和显式Subject绑定，另行完成企业IdP验收。

## 备份与回退

冷备份含七数据卷、逻辑dump、运行配置、持久会话密钥、原秘密及三份演示凭据，共21文件，所有SHA读回通过。精确私有路径与manifest摘要见cold-backup.json；备份内容、运行目录、密码/API Key和会话Cookie不进入公开交付。

实际恢复到新owner和空卷，旧版登录/业务/双网关HTTP200通过；再迁移新版本，原会话/数据与四个SSO空表通过；切回旧软件并保留新增表及迁移记录后，原登录/数据/HTTP200仍通过。没有migration Down。恢复项目、卷及秘密已精确清理，剩余均0；原环境未执行回退或覆盖原卷。

未来软件回退可保留增量schema，但开通SSO后须撤销SSO会话或调整Cookie/应用标识，防止旧版本接受SSO票据。数据库回退须停止写入并明确数据范围，不能直接覆盖升级后新增业务记录。

## 验证与交付

本轮新跑347项集成测试、70项前端测试和TypeScript/Vite构建均通过。不把历史682项测试或10项隔离IdP场景当成本轮重跑结果。12项浏览器检查、6张稳定截图查看通过，范围和尺寸事实见ui/qa.json。

最终检查脚本最初误用快照主键id；已修正为config_version_id，并完整核对原v1至v3聚合摘要，没有修改产品源码、数据库或放宽断言。

原SSO源码封存包保持原样，其中“不提交/未升级”属于历史源码验收状态。最新部署事实以本补充与current-sso-delivery-index为准。三源可用不代表维护期间历史缺口已补齐；企业上游、正式IdP、生产TLS/HA、容量、外部通知及生产灾备仍未验收。
