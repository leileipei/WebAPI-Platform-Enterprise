# WebAPI 本机持久 Keycloak SSO 接入结果

2026-10-06。本批SSO演示已部署并通过真实登录验证，原admin、两名审核员、原密码、角色/Scope与业务数据保留。平台应用仍为0b42f9e11977f8453887d6b23e45192ab2e42d61，运行镜像sha256:2bcd8799e48b3452970045d619c274827f74fe866cb7f4345239a0cac2b1197a。工具固定一次本地提交acb10a5f06a1734aec40a5d49ec1355d9fa191c8，不push/merge；后续执行证据和端口订正属于提交后的补充文档。

## 体验入口

- 平台：[4192](http://127.0.0.1:4192/environments)。已有admin会话可先退出，在企业登录区选择“本机 SSO 演示 · Keycloak”；勾选重新验证可再次显示IdP密码表单。
- IdP实际端口为4194，Issuer `http://localhost:4194/realms/webapi-local-demo`。拟用4193被原Node监听占用，检测在新增IdP目录和卷之前退出，保留该服务后改4194。操作以实际idp.json为准；通用工具原提交文档的4193示例由本结果订正。
- 演示账号 `sso-demo-viewer`，密码只在原运行目录 `idp/viewer-password`。没有平台本地密码，仅唯一Viewer角色和现有组织/项目/环境的read Scope。可查看环境管理，不能新建/编辑、管理SSO或写API。环境Scope不会升级为项目Scope，项目级API目录按既有规则不可访问。
- 原admin本地登录仍可用。身份源未设自动默认跳转，未知Subject拒绝且不会自动创建账号。

## 持久维护

原运行目录：`/Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.runtime/local`。

维护入口manage.sh和manage-idp.sh指向Git archive固定的96个工具/部署文件，SHA读回与tooling.json一致。日常使用这两个入口；不要用旧工作区工具重新生成runtime-config.json。

```bash
"/Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.runtime/local/manage.sh" status
"/Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.runtime/local/manage-idp.sh" status
```

平台支持start/restart/stop/configure，均重新读取owner受控sso-deployment.json。IdP支持start/restart/stop/backup。详细恢复与回退见local-keycloak-demo.md。回退先停用Provider撤票，再禁用连接、重建控制面，不自动删除持久卷。

IdP为独立Keycloak26.8.0摘要和独立PostgreSQL，4个持久卷；只发布127.0.0.1:4194，数据库无宿主端口。平台客户端秘密UID10001:10001/0600，卷只读挂载；密码与秘密未进入Git、截图或公开交付包。

## 已执行验证

166项工具/console单元测试通过。一次独立全批审查：Critical0、Important2、Minor3；作者一轮修复，未进行第二次独立审查。初始化中断续跑、部分卷恢复续跑、显式恢复身份绑定及清理顺序均已验证。

隔离与持久环境各8项真实HTTP流程通过：授权码+S256 PKCE、环境读取与写403、未知Subject401且不开户、正常维护入口重启保留会话/Subject/设置、停源撤票且启用不能复活旧票据、停用户撤票、退出、本地管理员恢复。当前持久冷备份完成后再次实际登录、读取环境和退出通过。

原生浏览器7张1440×1000截图完成视觉检查，验证登录跳转、返回4192、Viewer只读/管理权限拒绝，以及原admin恢复。最后浏览器保留admin的SSO管理页面。

7卷平台与4卷IdP在隔离实例完成真实新owner空卷恢复，实际在第二卷前注入中断，首卷done标记不变且服务未启动；续跑后原Subject恢复、经API停旧源和Viewer→重绑→恢复状态，真实登录成功、写403。两组本批副本已清理，未删除原平台或当前持久IdP。

当前持久环境另留最终冷备份：平台20个保护文件、IdP12个保护文件逐项SHA256读回通过。包含SSO配置和两名审核员凭据；这份最终当前备份未重复恢复，不能把此前隔离恢复证明写成“当前备份已恢复”。私有位置见persistent-backups.json，不入公开补充包。

原用户/角色/Scope与各保护业务/配置表摘要一致，原告警规则摘要亦直接比对通过，原秘密摘要一致；14个历史交付ZIP不变。双网关4196/4197实际/orders均200；v4/seq4未变；13个原服务运行，四个诊断源Available。无新增业务发布。

## 当前明确限制

原0b42f9e应用的发布ACK冻结旧InstanceId。正常重启后的网关新实例心跳Ready并加载相同v4/seq4，但冻结目标ACK不接受新实例，所以getRuntimeStatus总状态是Degraded。真实业务请求200不替代当前实例ACK。本批保留应用镜像和业务版本，不伪造ACK、不额外发布；需后续单独修复网关就绪/发布确认生命周期模型。本批SSO验收通过不能写成“整个平台全部Ready”。

本机HTTP的Development+FixtureEnabled例外只精确允许localhost:4194并映射到受控Docker alias；不是企业HTTPS/PKI或真实企业IdP/生产接入验收。

## 证据索引

- verification.json / code-review.md：166项单测、一轮审查与修复边界。
- preflight.json / final.json：原数据保护、实时状态与ACK限制。
- isolated-live.json / persistent-live.json / post-backup-login.json：实际SSO流程。
- backup-restore.json / persistent-backups.json：隔离恢复与当前持久备份的不同证据范围。
- security-and-preservation.json：角色/Scope、只读秘密、精确回调、真实双网关请求、固定96文件和14历史ZIP。
- ui/qa.json / ui/report.md / 7张jpg：原生桌面UI验证。
- immutable-tools.json / retained-deliveries.json / execution-ledger.md：工具身份、既有交付保留和执行记录。
