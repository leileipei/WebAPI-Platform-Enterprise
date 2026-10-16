# 本机演示环境恢复清单

本清单对应`webapi-enterprise-local`及当前本机演示业务。历史随机项目已经完成隔离冷备份/恢复演练；本次长期演示实例尚未执行新的冷备份。以下内容是可执行准备清单，不代表已有当前数据副本。源代码、Docker镜像、业务数据备份分别保存。

## 当前实例与资源

管理入口4192；两网关4196/4197；管理员`admin`。环境、API、发布、告警规则及事件ID见[演示验收证据](../evidence/local-runtime/demo-activation.json)。当前实际运行版本及依赖镜像以私有`.runtime/local/release.json`为准，不能由源码ZIP的交付提交推断运行镜像。

| 持久数据 | 当前卷名 |
|---|---|
| PostgreSQL业务库 | `webapi-enterprise-local_pg` |
| 网关A LKG | `webapi-enterprise-local_lkg-a` |
| 网关B LKG | `webapi-enterprise-local_lkg-b` |
| DataProtection会话密钥 | `webapi-enterprise-local_dp-keys` |
| Prometheus指标 | `webapi-enterprise-local_prometheus-data` |
| Loki访问日志 | `webapi-enterprise-local_loki-data` |
| Tempo调用链 | `webapi-enterprise-local_tempo-data` |

Redis是缓存，恢复后由持久业务事实重建。所有运行秘密源文件在`.runtime/local/secrets/`；挂载到服务的秘密卷由工具管理，不通过手工改卷接管。

## 冷备份准备

1. 妥善保存`.runtime/local/runtime.json`、`release.json`、原Git提交和相同架构的Docker镜像及依赖镜像。运行目录状态与owner不能用于直接覆盖另一项目身份。
2. 在维护窗口按[长期运行手册](local-runtime-runbook.md#离线完整备份与恢复演练)执行`createBackup`。该过程会停止当前应用、双网关与三源；完成后保持停止，需显式start。普通启停不会备份，也不会删除卷。
3. 备份目标为新建私有目录，包含PG逻辑dump、七份数据卷归档、runtime/release、secrets及逐文件SHA清单。物理PG恢复与逻辑dump导入是两种替代方案，不能叠加。
4. **另行保存本轮演示凭据**：当前备份工具复制`secrets/`，不会复制其外部的`demo/`目录。两名审核员密码及调用API Key需另存至私有介质，文件0600、目录0700。不要把凭据加入验收补充包或源码ZIP。

需补充保存的三份文件：

- `.runtime/local/demo/local-demo-api-approver.password`
- `.runtime/local/demo/local-demo-security-reviewer.password`
- `.runtime/local/demo/consumer-api-key`

原管理员密码文件为`.runtime/local/secrets/bootstrap-password`；若用户已经执行`complete-bootstrap`删除该文件，备份不会重新生成密码，使用用户已经保存的管理员凭据。PG中的密码/API Key摘要不能反向恢复明文。

不要将`demo/journal.json`中的原owner直接用于新恢复项目的操作。恢复工具为新随机项目创建新owner，业务ID由恢复数据库保留；私有操作日志只属于原部署。

## 隔离恢复检查

使用长期运行手册的`restoreColdBackup`命令，目标必须是全新随机`webapi-enterprise-local-test-*`项目与不存在的目录，端口选空闲loopback。工具先验证哈希，再恢复到空卷；不得覆盖长期项目，也不得删除原卷。

恢复检查按以下顺序记录实际结果：

- 用已保存的管理员账号登录，核对演示组织、项目、环境、API、调用应用及两名审核员的角色/Scope。
- 检查发布记录的独立审批与历史冻结ACK，区分旧实例与新实例；不能把旧ACK改写为新进程确认。
- 使用原API Key分别调用两网关，核验LKG的实际响应。新实例需要正常审批的新发布取得当前实例ACK后才可称Ready。
- 查询备份时已存在的固定时间范围，核对日志、采样Trace、告警Recovered及处理时间线、审计记录。
- 核对三源和Worker状态；Collector重启不会补造丢失遥测，Unknown/Partial按真实情况记录。
- 确认原长期项目仍保留。仅在明确结束演练后，对精确记录的随机项目使用`cleanupFixture`，不执行全局prune或长期项目down -v。

本清单没有安排自动备份、自动通知或生产切换。企业数据库恢复、跨架构/大版本迁移、外部秘密管理及目标服务器验收需在对应实际环境验证。
