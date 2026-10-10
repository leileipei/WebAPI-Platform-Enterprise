# 发布与交付中心交付索引

2026-10-10：P1–P14完成验证。标准阶段可配置、环境访问地址与跨环境发布晋级已进入本机安装；本次Pipeline固定产品源码 `71183e941eef99efb3ad4328abd7c91d02938a1d`。用户确认的四个监控容器身份例外是安装保全结论的一部分。

|交付内容|可复核材料|
|---|---|
|原4192安装、数据/秘密/旧工具保全、SSO、升级后冷恢复|[安装验证](installation.md)、[实际证明](71183e941eef99efb3ad4328abd7c91d02938a1d/installation/proof.json)|
|TEST→PROD与DEV→TEST→UAT→PROD真实交付链及23项故障|[隔离验收](verification.md)、[实际证明](71183e941eef99efb3ad4328abd7c91d02938a1d/runtime/proof.json)|
|2637项回归、零失败/跳过；末次227脚本和13项精确安装边界复核|[测试结果](test-results.json)|
|一次独立最终审查；C1/I1–I5修复、M1延期|[审查与处置](final-review.md)、[机器摘要](final-review-disposition.json)|
|首次原安装失败、旧包冷恢复、四项例外确认及修正探针|[中断恢复](installation-interruption.md)、[真实探针](infrastructure-maintenance-probe.json)|
|逐项执行与裁决|[完整账本](execution-ledger.md)、[裁决清单](rulings.md)、[覆盖矩阵](coverage.md)|
|标准线性阶段设计和14项实施计划|[规格](../../superpowers/specs/2026-10-09-release-pipeline-design.md)、[计划](../../superpowers/plans/2026-10-09-release-pipeline.md)|
|前期环境URL及TEST→PROD交付|[前期交付索引](../release-promotion/delivery-index.md)|

控制台[发布流水线](http://127.0.0.1:4192/delivery/pipelines)和[流水线运行](http://127.0.0.1:4192/delivery/pipeline-runs)可在原账号登录后查看。原项目没有自动激活或业务运行。每阶段保留人工发布、测试、验收/审批与生产验证责任；第一版不包含任意脚本或并行编排。

应用分支`feature/api-delivery-promotion`及隔离工作区保留；主目录未合并、未推送、本地改动未改写。私密冷备/失败数据库/安装回执与日志保留，不进Git。企业DNS/TLS/LB与业务验收未执行，不能把环境URL保存称为域名开通。既往两次间歇性并发/投影异常未定位，后续通过不等于根因修复；M1非生产/失败标签文案延期。
