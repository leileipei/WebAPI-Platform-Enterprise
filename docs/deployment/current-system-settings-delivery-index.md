# 系统设置升级后的本机交付索引

当前事实以本批final-check和升级结果为准。原文档中“本轮未升级4192”是系统设置源码验收阶段的历史状态，已由此次用户A授权部署更新；旧源码及证据包保留历史记录，不覆盖。

- 当前控制台：[系统设置](http://127.0.0.1:4192/settings/system)、[审计](http://127.0.0.1:4192/audit)、[节点](http://127.0.0.1:4192/nodes)。原admin/原密码；双网关4196/4197。
- [本次升级结果](system-settings-upgrade-result.md)：固定提交、21文件/七卷冷备份、旧恢复、新迁移、旧软件回退演练、等价审批与当前v3/seq3双ACK、页面检查与待处理原生确认框。
- [升级方案与回退边界](system-settings-upgrade-review.md)。
- [本次实际证据](../evidence/settings-upgrade/verification.json)、[最终检查](../evidence/settings-upgrade/final-check.json)、[浏览器检查](../evidence/settings-upgrade/ui/qa.json)。
- 系统设置源码与验收：`deliverables/WebAPI_Enterprise_系统设置源码及验收_20261005.zip`（原包保留）。
- 系统设置本机升级补充：`deliverables/WebAPI_Enterprise_系统设置本机升级补充_20261005.zip`及外置manifest，包含公开说明和证据，不包含源码、Docker镜像、数据库或备份/凭据。

原40页原型、核心/观测/独立运行及流量策略各自保持交付边界。第39页SSO与通知发送等后续能力不能因本次第40页部署而被视为完成。近1小时指标覆盖仍按实际采集缺口显示，不以当前健康冒充完整覆盖。

## Chrome最终浏览器复验（最新）

设备解锁后，10项交互补验全部通过，11张截图保存并查看，审计CSV真实浏览器下载75条/10列/17452字节。当前服务复核仍Ready，双ACK v3/seq3，平台设置0行、原数据和凭据保全。此前文档和封存升级包中的“鼠标复验待处理”为历史状态；以[最终浏览器报告](../evidence/settings-upgrade/ui/chrome/final-report.md)及[final-qa](../evidence/settings-upgrade/ui/chrome/final-qa.json)为最新结果。

独立交付：`deliverables/WebAPI_Enterprise_系统设置浏览器复验补充_20261005.zip`及manifest；原源码与升级包未覆盖。本轮未新增提交、部署、发布或设置保存。Chrome验收页已保留；未声称原内置浏览器旧测试确认框已经自动清除。
