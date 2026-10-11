# 最终固定源码双环境验收

源码 `a445534f82f11ae9a45c3b2904bd07108bd165a9`，镜像 `sha256:02a1c8782a00a04e6ad1c065560360efccffbee6db1a2039699c9cb8e3c14378`。这是隔离合成验收，原4192安装证据另行记录。

- Domain753、Integration971、Gateway142、Console245、runtime+delivery153、nativebackup2全部通过，零失败、零跳过；TypeScript与生产构建通过。总计2266项，Focused6为完整集成套件中的专项回归，不重复计入。
- TEST/PROD各两个真实Gateway进程，不同后端/入口；PROD实际TLS200，来源凭证到目标401。
- 独立来源验收、实际两级审批、真实发布；全ACK后仍需独立三类生产验证，受控附件关联具体事实。
- 九类故障含部分ACK、实际120秒超时、Worker/CP重启、排队撤验收/撤发布资格/改连接、共享应用确认、业务验证失败和人工审批回滚。历史事实、恢复序列和目标入口保留。
- 受控报告卷、数据库及额外交付卷冷备，恢复到全新UUID；实际下载报告摘要/事实一致，四个还原Gateway实际请求通过。
- 18张1440/1280截图逐张视觉检查；浏览器错误0，自有容器、卷及秘密文件清理0。观察包269项摘要与3个静态文件见proof。

[结构化证明](proof.json)、[独审修复](../review-fix-evidence.md)。第一次同源码尝试因Docker默认地址池耗尽而失败，保存在同级 `b11-runtime-attempt1`，不能视为完整验收成功。本次只释放自己的克隆后完整重跑，未清理其他项目。

`sourceVerified=true, isolatedAcceptance=true, localInstalled=false, productionAcceptance=false`：本证明本身不推定原实例安装或企业验收。
