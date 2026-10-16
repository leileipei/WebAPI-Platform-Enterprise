# 本机交付索引

本索引对应2026-10-05的本机演示交付状态，最新复核见`docs/evidence/local-runtime/handover-verification.json`。三个交付层分别保留，页面原型、可运行源码与当前运行数据不能互相替代。

| 交付层 | 文件或入口 | 用途 |
|---|---|---|
| 原40页高保真原型 | 工程上级目录`WebAPI_Enterprise_V2_40页高保真原型_方案1_V1.4.zip` | 完整页面、导航和演示交互 |
| 独立运行源码修正版 | `deliverables/WebAPI_Enterprise_独立本机运行环境源码及验收_V1.1.zip` | 已封存源码、静态前端、部署工具和历史验收；原包保留 |
| 本机演示验收补充包 | `deliverables/WebAPI_Enterprise_本机演示联调验收补充包_20261005.zip` | 本轮更新的说明、当前实例证据与真实页面截图 |
| 当前运行实例 | [管理控制台4192](http://127.0.0.1:4192/nodes) | 实际数据库、独立审核账号、双网关、示例上游和三源；保持运行 |
| 当前数据备份 | 尚未创建 | 应按维护窗口执行冷备份；持久卷保留不是备份证明 |

## 操作阅读顺序

1. [演示使用说明](local-demo-guide.md)：登录、独立审核员及订单API调用。
2. [长期运行手册](local-runtime-runbook.md)：构建、启停、绑定、状态和隔离备份恢复。
3. [演示恢复清单](local-demo-recovery-checklist.md)：精确卷名，以及备份工具不会自动复制的三份演示明文凭据。
4. [40页能力覆盖](../console-coverage.md)：已接真实接口与后续能力的边界。
5. [当前实例证据](../evidence/local-runtime/demo-activation.json)和[交付前复核](../evidence/local-runtime/handover-verification.json)：双ACK、真实HTTP、三源、告警Recovered及保全检查。

补充包只含公开说明和脱敏证据，不包含产品源码、Docker镜像、数据库数据、备份、运行目录或密码/API Key。原V1.1源码包及其manifest不被覆盖；补充包使用独立的逐文件SHA清单。当前工程中的文档更新单独保留，没有借打包操作自动提交、推送或合并。

当前默认一小时指标窗口因历史不足仍为Partial，空值按实际覆盖显示。短窗口指标已验证，不被当成整小时完整覆盖。双网关和三源持续运行，并不等同于企业生产TLS/HA、容量、真实上游、外部通知或长期稳定性验收。
