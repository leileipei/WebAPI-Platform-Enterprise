# 独立本机运行环境整体审查与修复记录

审查范围：`c874b928570a12b2d77be626dee831e764012621` 至 `0f4c71a76bc8669fb139d03632edf74d839407f8` 的本阶段增量；参照已批准的设计、实施计划、Review Focus及执行账本。执行方式为Native：实现者逐任务实施，一名独立fresh-context审查者作一次整体审查。审查者为 `local_runtime_final_review`，使用gpt-6-astra；只读审查及受控假适配器复现，没有修改Docker资源。

审查原结论：1项Critical、5项Important、无Minor。实现者接受这些等级并进行一次修复，不派第二次审查；最终门禁由失败测试转通过及完整回归构成。

| 等级 | 发现与用户影响 | 修复及覆盖测试 |
| --- | --- | --- |
| Critical | 只按Compose项目标签枚举，漏掉无标签但精确同名的卷、网络、容器；Compose可能接管非本环境数据 | 标签发现与精确资源名发现取并集；双标签不匹配即拒绝写操作，诊断失败关闭。`unlabelledExactResourcesPreventAnyDockerMutation`覆盖三类资源；真实无标签同名PG卷测试确认初始化拒绝、无秘密生成、原卷不变 |
| Important | configure失败提前覆盖此前Applied配置，旧健康ACK可能使状态错误宣称Ready | 将提案保存为pendingConfiguration，保留已应用binding/demoEnabled；Validated或待应用时为Degraded；注册成功后才推广。`failedReconfigurePreservesAppliedBindingAndStoresPendingProposal`、`pendingConfigurationCannotBorrowOldHealthyAckToClaimReady` |
| Important | 备份固定停止未声明的网关/示例服务，默认未绑定或未启用demo部署无法备份 | 在锁内重读状态，只停止实际声明服务，核验全部七个已存在且归属正确的数据卷；已停止环境临时启动PG完成dump；未应用提案时拒绝备份。`backupStopsOnlyDeclaredServicesForUnboundAndNonDemoDeployments`及真实三分支备份 |
| Important | 首次端口冲突保存状态后，显式新端口重试被忽略 | 未初始化且无资源时允许检查新端口后保存；初始化/资源创建后拒绝改端口；CLI只传显式端口。`portConflictCanBeRetriedWithExplicitPortsBeforeResourcesExist` |
| Important | 已初始化环境整个秘密目录丢失时重复init重新生成密钥 | 明确报错要求恢复，不创建目录或启动Docker操作。`missingWholeInitializedSecretDirectoryNeverRegenerates` |
| Important | start/up/restart只等管理端及源可访问，即按配置推断Registered，忽略失败网关或Worker | 返回实际综合服务、源、环境与节点状态。`startReportsObservedFailureRatherThanConfiguredRegistration`；真实生命周期/备份测试核验Unconfigured与Registered |

上述修复的失败测试均在修改实现前运行：备份测试因缺少导出失败，其余测试复现断言失败。修复后的定向测试26/26通过，完整单元84/84通过，容器回归5/5通过且无跳过。封存执行时发现已跟踪的旧deliverables清单被源码校验拒绝，补充失败测试后将整个交付输出目录排除，旧清单不改；最终完整单元85/85通过。完整结果在`final-review.json`及`regression.json`封存。

审查者明确未裁决的三项，由实现者裁决并列入执行账本：

1. 旧40页原型及旧可观测阶段的全面正确性不重复审查：本次只验证增量及相关回归，代价是旧范围不形成新的全面证明。
2. 公网TLS/HA、生产容量/SLA、企业上游、外部通知及实际连续7天保留不在本阶段验收范围：保留未验边界，代价是本机结果不能作为生产验收结论。
3. 长期环境缺管理员名及显式业务绑定：按已批准计划交付精确命令，不能猜名或植入验收业务；代价是源码与工具可交付，但长期环境尚未实际启用。

应用、前端、部署和依赖输入与已完成真实链路/重启/告警/恢复/UI验收的`tested-release.json`对应提交保持一致。后续变更仅本机运行脚本、相关测试、文档和交付工具；备份与所有权修复另有实际容器回归。证据中complete=true属于随机合成验收，不能推导长期用户环境已经初始化或绑定。

延期小问题：无。最终门禁不是第二次审查；它是实现者对上述发现修复结果的测试核验。

长期启用后的端口更正：原默认4190被Fetch标准阻止，原整体审查和随机显式端口验收未发现这一默认值问题。追加失败测试后改为4192并拒绝禁用端口，88/88单元通过；真实长期项目重试init及admin登录通过，浏览器登录页打开。该更正不是第二次整体审查。部署门禁只允许这一精确默认端口字节替换，其余部署字节及应用/前端/依赖仍冻结；详见browser-port-correction.json。
