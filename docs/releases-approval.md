# 发布候选、独立审批与幂等

创建Draft检查基准版本与所选版本revision，并把完整资源revision集合记录到草稿；提交时再次检查，随后将版本、Route、Cluster、Destination、Policy以及该环境消费方配置冻结为精确字节。之后的工作区编辑不修改CandidateBytes。生产审批策略与任务在提交时冻结；模板修改只影响之后的申请。非生产提交进入Ready，生产必须完成两级审批。

模板按顺序1、2，每级1到5位审核人；所有席位需不同人员。申请人不得自批；平台管理员同样不能自批或占据两个席位。当前步骤要求approval.act、当前Role、实际Environment Scope与活动用户状态；平台管理员具备功能权限和Scope时可审核，但仍满足人员独立规则。UI身份不参与判断。拒绝/取消后不能通过重复审批恢复Ready。

ReleaseDTO 只输出安全候选视图，凭证 Hash 留在内部冻结字节中，浏览器详情不返回凭证摘要。当前源码已包含下发和真实节点 ACK 接口，不能沿用早期仅完成审批闭环的阶段说明；固定版本的实际运行验收仍须分别核对。

Idempotency-Key绑定actor、实际Scope、operation与规范化请求SHA-256；相同请求返回持久化原结果，相同key不同内容409。键与资源写入/审计加入同一PG事务；统一授权写锁使并发重复只有一份结果。读取缓存结果前重新检查权限，撤权后的旧会话不能用幂等结果绕过权限。相同命令重试不重复审计。导入已经接入同一机制；凭证创建不持久化明文Secret作为幂等结果。

Snapshot 构建与可靠下发见[持久发布](contracts/durable-publish.md)，节点身份及 ACK 见[节点契约](contracts/node-lifecycle.md)，失败与回滚见[恢复约定](contracts/rollback-recovery.md)。当前源码和证据范围以[状态页](deployment/current-project-status.md)为入口，不把首期阶段描述作为今天的实现结论。
