# WebAPI Enterprise 网关重启就绪：本机升级结果

2026-10-06，已完成授权范围内的本机升级。4192 的正常维护重启后，双网关均为 Ready，当前与目标配置保持 v4/seq4，4196、4197 的 /orders 实测均为 200。真实 Keycloak 4194 登录、原本地账号和权限保持可用。

本说明是既有《网关重启就绪修复》交付的升级补充。旧包中的“尚未提交 / 尚未升级”是当时状态；当前状态以本说明及 gateway-readiness-upgrade 的执行证据为准。16 份既有 ZIP 均保留且摘要未变。

## 运行身份

| 项目 | 当前结果 |
| --- | --- |
| 本地源码提交 | 63681d172cfff7fdd5a80efb7bdd7dfbb2a32c10，仅 9 个已审阅源码/测试文件 |
| 应用镜像 | sha256:9d350a453b8f8441e68d786283bfe13374b0bf5660cce02e33e972af4d246f17 |
| 构建来源 | 固定提交 Git archive，锁定现有依赖，镜像用户 10001 |
| 原运行目录 | enterprise/.runtime/local，project=webapi-enterprise-local，原 owner 保持 |
| 平台端口 | 控制台 4192，网关 4196 / 4197 |
| 身份源 | 独立 Keycloak 4194，原 Provider、Subject、client-secret 引用保持 |
| 维护工具 | 同一固定提交的 scripts / deploy，共 96 文件，全部 SHA256 读回核验 |
| Git 操作 | 一次本地提交；未推送、未合并；原主工作区状态保持 |

## 实际验收

1. 提交前重新通过 611 项 C#、167 项工具/控制台测试及 20 项真实 PostgreSQL 状态查询检查。
2. 平台 7 卷和独立 IdP 4 卷冷备份完成，分别 20 / 12 个文件逐项校验摘要；包括原账号凭据、数据保护密钥和 SSO 配置。原服务备份后恢复运行。
3. 在全新演练 owner 的空卷恢复平台和 IdP。副本 SSO 保持 disabled，未接入原 IdP；原账号、数据和 IdP Subject 恢复通过。
4. 演练中实际验证旧镜像登录/流量、新镜像 Ready/流量，再切回旧镜像登录/流量。无新增迁移或业务发布。仅演练副本被清理，容器、卷、秘密文件剩余均为 0。
5. 原 4192 升级后，原管理员会话仍可用；所有保护表、历史发布成功审计及秘密/SSO 配置摘要保持。安装固定维护工具后，实际执行平台与 IdP 的正常 restart，再次通过 Ready 检查。
6. admin 和两名原审核员登录通过；真实 Keycloak Viewer 登录和环境读取通过，组织写入请求返回 403。执行注销的同一客户端再次查询身份返回 401。重启后真实 SSO 登录仍通过。
7. 1440 × 1000 实际浏览器检查：管理员双节点 Ready；当前实例 runtime_applied 事件可见；真实浏览器跨主机 SSO 回调成功，Viewer 节点可读、编辑身份状态按钮禁用。最后恢复管理员，保留网关节点页面。
8. 页面操作后再次只读核验：31 张保护表、发布成功审计、密码与 SSO 配置、原主工作区状态、16 份历史交付摘要保持；双网关调用仍为 200。

## 运行确认与历史发布事实

本次重启后的双网关 runtimeConfirmed=true、publicationAcknowledged=false。新实例确认自己加载了既有成功快照，不补写历史发布 ACK，不改变 ReleaseTarget、发布记录或审批流程。新确认记录在节点 Metadata 中，并产生去重 runtime_applied 事件。

环境 Active、双节点启用、心跳新鲜、当前与 desired 的版本/序列/摘要一致，才汇总为 Ready。仅心跳或 HTTP 200 不能替代运行确认。本次业务配置始终 v4/seq4，没有新增业务发布。

## 验收脚本的一次修正

首轮最终验收在重启前停止：注销响应更新了 LocalClient Cookie，但检查误用了另一 SsoBrowser 的 Cookie 副本。已仅修正私有验收脚本，重跑通过；应用源码、提交和镜像未变。

当前注销清除执行注销客户端的平台 Cookie；它不声称撤销另行复制的有效 Cookie，也不结束 Keycloak 会话。详细证据见 verification-harness-correction.json 和 final.json。

## 证据与交付边界

执行证据位于 ../evidence/gateway-readiness-upgrade/：local-commit、immutable-build、cold-backup、backup-restore、rehearsal-cleanup、upgrade、final、handoff，以及三张实际 UI 截图。日常入口和回退原则见 gateway-readiness-upgrade-runbook.md。

补充 ZIP 包含本次结果、维护说明、执行证据、9 文件源码增量及 96 文件固定维护工具、逐文件摘要清单。私有备份、数据库、账号密码、Cookie、原始会话日志、运行目录和依赖不进入交付包；旧镜像与私有冷备份保留本机。

以上为本机运行验收。生产 HTTPS、真实企业 IdP、生产高可用和容量验收未执行。未重跑所有外部 Compose EndToEnd 场景，本批实际新增的是上述恢复、升级、回退和正常重启验收。
