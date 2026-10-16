# 网关重启运行确认说明

本批修复软件重启后旧发布冻结InstanceId与当前实例就绪证据之间的缺口，保留全部发布/审批/账号/Scope流程。没有新增数据库迁移或业务发布。

## 发布确认和运行确认

仍处于Publishing的发布，只接受ReleaseTarget冻结实例的ACK。新实例不能替代原实例完成审核发布；Failed、RolledBack或旧期望配置不能产生新运行确认。

仅当发布Succeeded、配置Published、环境Active且版本/序列仍为当前desired时，节点可用自身secret对当前InstanceId成功应用相同ReleaseId/Hash的快照进行确认。协议能力、UTC近期AppliedAt等校验仍执行。确认写入gateway_nodes.metadata.runtimeApplication，并产生去重runtime_applied事件；历史gateway_acks、release_targets、release_records和release.succeeded审计不改写。

重新注册更换InstanceId时清除旧运行确认，保留其它Metadata。维护查询再次验证严格JSON类型、当前实例、发布、版本、序列、摘要，并要求fresh心跳/Ready/启用/版本一致。普通心跳和HTTP200不能单独产生确认。

## 观察入口

完成授权升级后，原运行目录manage.sh status会分别显示publicationAcknowledged和runtimeConfirmed，acknowledged为有效运行证据的汇总。新实例恢复同一成功快照时，通常publicationAcknowledged=false、runtimeConfirmed=true；这不代表新实例补充了历史发布ACK。

管理UI节点事件会显示runtime_applied，事件详情包含实例、发布、版本、序列、Hash、应用时间，不含秘密。已有页面、路由和权限不变。

## 日常重启与回退

继续使用原运行目录manage.sh / manage-idp.sh的固定提交入口。升级会显式替换到本批工具提交，保留sso-deployment.json、SSO客户端只读秘密卷和4194独立IdP。

正常平台restart后等待新实例注册、加载LKG/desired并上报运行确认，检查两节点Ready、fresh、同一desired、runtimeConfirmed或有效原实例ACK。确认缺失/错配则保留Degraded并检查节点事件，不手工写数据库、不新发业务版本绕过检查。

回退到原0b42应用无需删除Metadata字段，不覆盖数据卷；旧应用忽略新增字段，旧维护工具可能继续显示Degraded，这是原已知限制。回退不是撤销业务版本，v4/seq4和历史发布不变。

## 附加维护锁修复

回归发现等待锁时EEXIST后持有者正常释放，检查operation.lock报ENOENT。现在仅重试这个正常释放窗口；owner、私有权限、外来文件与符号链接拒绝仍保留。既有死进程清理及其他替换竞态不属于本批扩大重构。
