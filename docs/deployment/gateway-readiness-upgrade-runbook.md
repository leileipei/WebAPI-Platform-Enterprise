# 4192 升级后维护入口

2026-10-06。已部署提交 63681d172cfff7fdd5a80efb7bdd7dfbb2a32c10。日常操作继续使用原运行目录下的固定入口，不从新工作树无参数 init，也不重置原账号或重新 seed Keycloak。

## 查看与重启

```bash
"/Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.runtime/local/manage.sh" status
"/Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.runtime/local/manage.sh" restart
"/Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.runtime/local/manage-idp.sh" status
"/Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.runtime/local/manage-idp.sh" restart
```

控制台：http://127.0.0.1:4192/nodes。Keycloak：http://localhost:4194/。原管理员用户名 admin；现有密码文件仍在原运行目录 secrets/bootstrap-password，SSO Viewer 密码仍在 idp/viewer-password。密码内容不记录在交付材料中。

正常平台 restart 后等待新实例注册和运行确认：两节点 Ready / fresh、启用，current 与 desired 同为 v4/seq4；当前新实例通常 runtimeConfirmed=true、publicationAcknowledged=false。查看节点详情中的 runtime_applied 事件。四诊断源应为 Available。

若为 Degraded，先检查节点事件、当前实例和期望快照；不手工补 ACK、不写数据库、不发布新业务版本绕过就绪检查。v4/seq4 是本次验收的既有业务配置，后续合法业务发布另按审批流程执行。

## 私有备份与回退

私有备份路径登记在本工作树 .runtime/gateway-readiness-upgrade/backup-pointer.json；平台和 IdP 各有 backup-manifest.json 及逐文件 SHA256。本次演练已实际恢复这些备份到独立空卷，但没有覆盖原卷。

软件回退使用平台冷备份内的旧 release.json（0b42 应用镜像），显式指定原运行目录与 admin，保留所有数据卷、绑定、SSO overlay 和独立 IdP。原维护入口备份在 .runtime/gateway-readiness-upgrade/old-maintenance/；回退旧工具时须一并恢复 tooling.json、manage.sh、manage-idp.sh，脚本权限保持 0700。

旧镜像能读取原数据，演练已验证登录和流量。旧维护工具可能继续显示本次修复前的 Degraded；这不代表回退修改了业务版本或历史发布。不要执行删除卷、重建账号或数据库恢复来进行普通软件回退。

数据恢复属于单独维护操作；如果已有新业务写入，应先核定恢复时间点，不能用本次冷备份直接覆盖当前数据。独立 IdP 与平台恢复副本均应使用新 owner，平台副本 SSO 保持 disabled，避免误连原 IdP。
