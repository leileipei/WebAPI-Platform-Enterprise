# 标准阶段流水线覆盖账本

本期范围：2–8 个不同环境的线性标准阶段，逐阶段人工发布、证据登记和独立验收。任意脚本、并行编排、自动业务测试与自动回滚不在本期范围。数据库/API 测试中的模拟 ACK 与真实 Gateway 证据分别记录。

|要求|实现/回归归属|真实验收归属|
|---|---|---|
|链顺序、必需测试、时间范围、生产末位及审批模板|PipelineDefinitionRulesTests、PipelineDefinitionTests、PipelinePersistenceTests|两环境及四环境实际链|
|不可变版本、显式激活、合法模式退出、单运行|PipelineActivationTests、PipelineRunTests|并发运行、跳阶段、旧接口绕过|
|根制品、逐环境行为一致、目标凭证和映射|PipelineGateContextTests、PipelinePromotionTests、PipelineStageEvidenceTests|每跳实际行为摘要、双节点调用、外来凭证拒绝|
|来源证据及审批/验收/生产验证独立性|PipelineExecutionTests、PipelineApprovalTests、PipelineNonProductionApprovalTests、ProductionVerificationTests|三类人工测试、独立验收、两级审批、独立生产验证|
|投影、暂停、超时、撤销、迟到节点事实|PipelineProjectionTests、PipelineTimeoutTests、AckTests|排队撤销/撤权、部分应用、Worker 重启、迟到 ACK|
|三类人工重新办理、旧摘要拒绝、精确快照恢复|PipelineRecoveryTests、PublishTests、RollbackTests|未下发/成功/失败尝试重开、精确重试、审批回滚|
|范围与委托、受限计数、旧异步响应|PipelineReadTests、Console pipeline/state/action/read 测试|受限委托、撤权清屏、实际 409/412|
|界面、SSO、键盘、原审批筛选返回|Console 全量测试、现有 SSO 回归|1440/1280 截图逐张查看、真实本机 OIDC|
|所有类型化 Gateway 组及所有受保护报告冷恢复|runtime backup、pipeline-evidence 测试|新 owner 克隆、每组双节点调用、每份报告下载摘要|
|固定源码、应用包和静态文件、资源所有权|pipeline-cli/evidence/runtime/network 测试|容器镜像与实际文件摘要、所有隔离资源清理为 0|
|原实例历史数据、授权、Secret、旧工具保全|pipeline-installation/inventory/maintenance 测试|P14 安装前后真实主键清单、固定包、SSO、原版本/序列及冷备恢复|

当前状态：P1–P13完成，最终审查C1/I1–I5在一次修正中全部RED→GREEN并通过新固定候选实际验收。P14保全工具已准备，原实例安装尚未执行；企业验收尚未执行。新增审查回归归属PipelineReviewRegressionTests、generated maintenance/cleanup行为测试及第三阶段两宽度真实浏览器映射/预检。
