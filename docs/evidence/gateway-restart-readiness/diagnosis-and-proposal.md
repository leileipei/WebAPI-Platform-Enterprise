# 网关重启就绪问题：已验证根因与待确认修复

2026-10-06。用户“继续”后推进现有网关重启问题。当前阶段只完成调查、隔离和基线验证，未修改产品源码、提交或升级4192。

## 实时证据

原运行目录manage.sh status：13个原服务运行，collector/prometheus/loki/tempo均Available。两节点心跳fresh、reportedStatus=Ready、currentVersion=desiredVersion=4、currentSequence=desiredSequence=4，但acknowledged=false，aggregate=Degraded。

原数据库只读查询：当前环境v4/seq4的releaseStatus=Succeeded，冻结目标2个，历史成功ACK2个，当前实例成功ACK0个。没有查询密码/IdentityHash或写入原库。

当前应用0b42f9e11977f8453887d6b23e45192ab2e42d61；隔离工作树从已固定维护工具acb10a5f06a1734aec40a5d49ec1355d9fa191c8创建。主工作区、SSO工作树和已封装ZIP未修改。原生工作树工具返回Not a git repository，核验.worktrees ignored后用Git fallback建立feature/gateway-restart-readiness。

166项既有runtime/console单元测试基线通过，0失败。此为工具/console基线，尚非本批C#回归或实机修复证明。

## 根因链路

1. NodeRegistry.RegisterAsync在InstanceId改变时把当前版本/序列清零并置NotReady，新实例与历史实例严格分开。
2. 网关SnapshotActivation从LKG或控制面加载同一快照，ConfigWatcher.AcceptAsync调用NodeClient.AckAsync。
3. AckService.RecordAsync强制target.InstanceId==ack.InstanceId，即冻结的发布目标；新实例因此被409 ack_target_mismatch拒绝。
4. NodeClient.AckAsync对409直接返回，心跳仍报告运行Ready与v4/seq4；不会生成当前实例的成功发布ACK。
5. scripts/runtime/status.mjs只按gateway_acks中当前InstanceId/期望版本/序列的成功记录判定确认，故总状态Degraded。

冻结发布ACK本身是有意的治理规则：已有RestartedInstanceCannotFillOldFrozenTarget等测试保护。不能通过取消实例校验、改写历史ACK、伪造节点确认或重新发布业务版本消除现象。

## 待确认方案

局部扩展现有确认路径，保留Publishing阶段和冻结发布事实的全部规则。只有认证并仍启用的当前实例，针对状态Succeeded且仍为环境当前期望的发布，报告成功应用并满足精确ReleaseId、ConfigVersion、DeploymentSequence、PayloadHash、协议能力和UTC应用时间校验时，才可生成独立的当前实例运行确认。

运行确认保存在现有节点Metadata，并添加去重的runtime_applied节点事件；不新增表或迁移，不修改历史GatewayAck、ReleaseTarget、ReleaseRecord状态和审核轨迹。轮询重复请求幂等；重新注册、新期望序列、禁用、旧实例或错误摘要均不能复用确认。原发布成功ACK仍可作为原实例的运行证据。

维护status查询兼容“当前实例历史成功ACK”或“校验通过的当前实例运行确认”，后者必须再次对齐当前期望发布及摘要，才能与fresh/Ready/版本一致等条件共同判定就绪。用户心跳声明和简单HTTP200仍不能独立证明就绪。

预计修改AckService及一个职责独立的RuntimeApplicationReceipt帮助类、runtime/status.mjs；按实际需要补NodeRegistry的确认失效处理。外部管理API、40页UI、账号/权限、业务发布流程和v4/seq4不变。

## 计划验证与交付边界

先用真实PostgreSQL/API场景写红测试，再实现；覆盖成功发布后双节点重启、重复确认、旧实例/未知节点/禁用/错摘要/错序列/过期应用时间、Publishing冻结目标与Failed发布拒绝、新发布不能借用旧确认、历史发布事实不变；增加真实双网关LKG恢复与流量验证，以及status真实查询验证。完成C#和166项工具/console回归、一次独立代码审查后交付可审阅修复。

4192现在没有切换。不可变长期构建需要另一次明确授权本地提交；备份/切换升级提供具体审阅结果后再执行。原账号与SSO配置/IdP/业务数据必须保留，不新增业务发布。
