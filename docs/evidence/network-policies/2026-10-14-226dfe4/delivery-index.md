# 第3包 CORS、IP 与 JSON Transform 交付索引

日期：2026-10-14。状态：固定代码及本机独立验收完成。本次交付文档已获提交、main合并及GitHub推送授权，实际集成状态以Git记录为准；未升级4192，第2包保持暂停。

固定代码：`226dfe450865cd322d81eb5747bec791a71f7519`；镜像：`sha256:4223703e79dfc88fe13631a32ac7bedf0c0a6da81370dd0610b908a7e6b9a8aa`；独立owner：`cdf8e9e1-9bb7-4d41-87c8-26272b7d36c9`。本轮新空库，未恢复原4192数据。

## 能力与验收

CORS、IP过滤、请求/响应Header、后端路径、Query及双向JSON add/replace/remove/copy/move均完成。原API身份、方法、授权、审批与回滚模型保留。JSON每方向默认256 KiB、上限1 MiB，null与原始数值保真、有界gzip/br、原子失败、安全重试和映射后缓存均有固定测试或实测记录。

- 固定五组：3359通过、0失败、0跳过（Runtime234、Console297、Domain1065、Integration1409、Gateway354）。
- [严格门禁](acceptance-result.json)：P3-01至P3-12全部通过；实际7应用同镜像、3个Console静态文件Hash一致、当前实例ACK、历史原字节Hash及隐私排除均核对。
- [真实SDK E2E](sdk-e2e-result.json)：1通过、0失败、0跳过，[原日志](sdk-e2e.log)及[来源/Hash清单](acceptance-input.json)可复核；未使用缺材料skip。
- [逐项覆盖](coverage-ledger.json)与[交付清单](delivery-manifest.json)关联要求、代码版本、日志和实际材料。

## 最终审查处置

一次新上下文全分支审查发现0 Critical、3 Important、0 Minor；三项均已在226dfe4完成真实RED→GREEN及新固定完整门禁，[审查处置](final-review.json)保留范围与验证边界。

1. 允许的Content-Language/Content-Disposition使用YARP原生请求头变换，真实双节点有正文及无正文Set/Remove通过。
2. 异常多方法预检不再产生路由歧义500；受控拒绝、IP优先、原代际回收、正常多方法与普通/无CORS的OPTIONS及原生404/405通过。
3. 前端允许明确的空DenyList，AllowList仍须非空；[实际保存及重命名](ui-extra/empty-deny-list-extra.png)与后端规则一致。

按批准执行方式只进行一次实现者修复和TDD复验，没有把第二次独立审查作为已执行。

## 页面与运维

[15项实际CUA](public/P3-11.json)全部在本轮1440px固定版本执行；原JPEG保存在独立验收工作区，交付PNG解码像素相同，见[像素记录](ui-extra/pixel-receipt-all.json)。

- [JSON响应预览](public/json-response-preview.png)保留大整数、精确小数与null；[并发修订](public/revision-412.png)保留输入；[范围迟到](public/scope-late.png)使用本owner真实30秒有界暂停/恢复。
- [九类绑定](public/nine-bindings.png)、[冻结Review](public/release-review.png)、[审批与历史ACK](ui-extra/release-approvals-extra.png)、[跨环境映射](public/promotion-mapping.png)及[超时/限流顶部](ui-extra/promotion-mapping-top-extra.png)可审阅。
- [只读配置](public/readonly.png)无保存/发布入口，policy.read仍允许[不持久化预览](ui-extra/readonly-preview-extra.png)。
- [实际Trace](public/logs-trace.png)与[日志策略决策](ui-extra/logs-decisions-extra.png)关联同一真实根请求。页面1小时范围如实显示Partial；[正式短窗口](public/P3-10.json)Metrics/Logs/Trace均Available。

正式观测窗口采样metadata为1；晋级后本夹具控制面查询端配置0.1、TEST网关仍1，页面10%来自当前查询端配置，见[当前配置收据](ui-extra/current-sampling-settings.json)。本轮不作为生产采样配置一致性或容量SLA证明。

管理员步骤、限制与错误处理见[操作说明](../../../deployment/network-policies-runbook.md)。

## 历史失败与保护

首次发布、预览方向、观测窗口、HTTPS、最小读取范围、目标参数/JSONB格式与依赖漏洞门禁等旧失败均保留，[日志Hash索引](retained-log-index.json)不把成功或操作错误冒充产品失败。07fb1fa的独立验收及其3项审查发现保存在[上一固定版本](../2026-10-14-07fb1fa/delivery-index.md)，不能替代226dfe4的新门禁。

[P3-12](public/P3-12.json)核对主工作区原内容/Git状态、4192运行身份及71表原行保留。第2包暂停owner运行容器为0。未执行main合并、推送或4192安装。

旧节点案例只验证能力声明阻断，未安装旧二进制。受控HTTPS入口不代表企业DNS/TLS/LB、HA、AMD64、生产上线或性能SLA验收。
