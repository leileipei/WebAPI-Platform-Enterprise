# 发布与交付中心交付索引

2026-10-10：P1–P14完成验证，标准阶段可配置、环境访问地址与跨环境发布晋级已进入本机安装。首个Pipeline产品版本为 `71183e941eef99efb3ad4328abd7c91d02938a1d`；后续M1文案更新也已完成，当前安装版本为 `e21c3a3e26dbb4cee3e8cd4700d39aebae1b93c1`。用户确认的四个监控容器身份例外属于首次安装记录，本次更新保持这四个现存ID。

|交付内容|可复核材料|
|---|---|
|原4192安装、数据/秘密/旧工具保全、SSO、升级后冷恢复|[安装验证](installation.md)、[实际证明](71183e941eef99efb3ad4328abd7c91d02938a1d/installation/proof.json)|
|TEST→PROD与DEV→TEST→UAT→PROD真实交付链及23项故障|[隔离验收](verification.md)、[实际证明](71183e941eef99efb3ad4328abd7c91d02938a1d/runtime/proof.json)|
|2637项回归、零失败/跳过；末次227脚本和13项精确安装边界复核|[测试结果](test-results.json)|
|首次独立最终审查；C1/I1–I5修复、原版本M1延期，后续已修正|[审查与处置](final-review.md)、[机器摘要](final-review-disposition.json)|
|M1文案本机更新、502项本轮测试、候选包完整隔离流程与原环境冷恢复|[更新记录](m1-update.md)、[机器摘要](m1-update.json)|
|安装前的30次推进、15组并发诊断；旧根因未定|[诊断报告](follow-up.md)、[机器摘要](follow-up.json)|
|首次原安装失败、旧包冷恢复、四项例外确认及修正探针|[中断恢复](installation-interruption.md)、[真实探针](infrastructure-maintenance-probe.json)|
|逐项执行与裁决|[完整账本](execution-ledger.md)、[裁决清单](rulings.md)、[覆盖矩阵](coverage.md)|
|本轮GitHub合并检查、完整回归与文档校正|[合并检查](merge-check.md)、[机器摘要](merge-check.json)|
|标准线性阶段设计和14项实施计划|[规格](../../superpowers/specs/2026-10-09-release-pipeline-design.md)、[计划](../../superpowers/plans/2026-10-09-release-pipeline.md)|
|前期环境URL及TEST→PROD交付|[前期交付索引](../release-promotion/delivery-index.md)|

控制台[发布流水线](http://127.0.0.1:4192/delivery/pipelines)和[流水线运行](http://127.0.0.1:4192/delivery/pipeline-runs)可在原账号登录后查看。原项目没有自动激活或业务运行。每阶段保留人工发布、测试、验收/审批与生产验证责任；第一版不包含任意脚本或并行编排。

应用分支`feature/api-delivery-promotion`及隔离工作区保留；原主目录本地改动未改写。本轮代码交付检查见[合并检查](merge-check.md)，GitHub合并结果以[仓库main](https://github.com/leileipei/WebAPI-Platform-Enterprise/tree/main)及对应合并请求为准；历史安装记录中的“未合并、未推送”指当时阶段。私密冷备/失败数据库/安装回执与日志保留，不进Git。企业DNS/TLS/LB与业务验收未执行，不能把环境URL保存称为域名开通。既往两次间歇性并发/投影异常未定位，后续通过不等于根因修复；M1非生产/失败状态标签已修正并完成本机更新，首次审查的延期记录按原版本保留。
