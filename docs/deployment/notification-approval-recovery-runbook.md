# 通知与审批的数据保留恢复桥接

本文件仅适用于本次已验证的本机兼容恢复路径。桥接源码在独立分支，不能合并为候选产品实现，也不能当作旧数据库覆盖新数据库的授权。

## 固定身份

- 原运行基线：`c1c271555e3a125d22184697c17320c94a4db07c`。
- 桥接源码：`cf41823b9a7bcac7f0ef2195b66c4efa058883ac`，独立分支 `feature/notification-recovery-bridge`。
- 桥接镜像：`sha256:e6ef85d58930c55f79a802f7502c5b050201e51088c6d3d038db56ab001389e6`。
- 精确基线到桥接补丁 SHA256：`e74f0f82cf97f6a11bbe0fdb2dadebf9ac67de9e55373059e8e3a44fb688d4ab`。
- 本次恢复演练的候选运行软件：`cf2b4708c94362faf6cb5f891a7364e0799eac5a`，镜像 `sha256:7913178628551b74657cdfe95627844a0c304bf41378b3db91f6234ef315570a`。文档及验收驱动的后续提交不是本镜像源码身份；D2 仍须固定最终候选并运行完整回归。

桥接从不可变 Git 归档构建，旧七应用构建器及全部锁文件保持基线内容。仅七个允许文件变更：设置编解码器、设置读取/保存服务、规则服务、兼容模式类、ControlPlane 必要注入/锁定路由、专用集成测试。通知 fixture 和测试上游仍由候选镜像提供，不把它们算作旧产品应用。

## 本次实际结果

D1 实际恢复门禁已通过，目标为独立 UUID 克隆的 57541，原 4192 未升级。最终桥接固定归档集成测试 **67 通过、0 失败、0 跳过**；本批 Node 门禁 **40 通过、0 失败、0 跳过**。

- 实际顺序：候选 → 原旧镜像九字段读取 503 → 只读桥接 → 候选。
- 五组设置可读；七项通知/规则写入口 423；桥接新增项目成功。
- 十一类持久卷、36 个内部备份文件的 SHA 全部回读通过。
- 业务、账号、导入辅助 YAML 来源包、通知定义及历史、SSO Provider/绑定与部署配置、DP 钥匙全部保留；原会话 Cookie 和新增本地账号登录验证通过。
- 双网关真实请求产生的旧 Worker 告警，其冻结 `policy.externalEnabled=false`；恢复候选后零追溯任务。
- 恢复后的两条产品测试 API 任务，Email 与 Webhook 各 `Accepted` 一次；有对应 SMTP TLS/AUTH/DATA250 和 HTTPS HMAC/2xx 协议回执。
- finally 后实际状态 `Ready`，全部观测来源和 notification fixture 可用。

脱敏材料与摘要见 [D1 manifest](../evidence/notification-approval/d1-cf2b470/manifest.json)、[实际恢复回执](../evidence/notification-approval/d1-cf2b470/recovery-receipt.json)和[最终就绪](../evidence/notification-approval/d1-cf2b470/ready-after-finally.json)。本轮没有执行新 OIDC 登录；不把保存 SSO 数据与 Cookie 可解密写成实时企业身份源验收。

初次冻结字段层级错误、跨组织 fixture seed 拒绝和 SMTP 回执字段映射错误的失败执行、冷备与 finally 资料全部保留。最终成功材料只来自修正后的完整执行，未将失败材料改标为成功。

## 行为与操作顺序

1. 核对目标为自有 UUID 测试克隆、独立端口及实际运行镜像。原 4192、4193、4196、4197 不能作为演练目标。读取候选 Console 静态资源逐 SHA 校验。
2. 正常停止 Worker，并等待数据库 `Sending=0`。仅在克隆里创建新的业务、账号、实际导入的辅助 YAML 附件和通知规则标记。
3. 冷备 PG、双 LKG、DP 钥匙、三观测源数据、缓存秘密及三通知卷，共十一类持久卷；备份同时包含运行状态、部署 SSO 配置、秘密文件和数据库逻辑备份。逐文件回读内部 manifest 的 SHA。备份过程必须在 finally 恢复服务。
4. 在候选 API 中启用专用测试规则，停止 Worker 后建立受保护行快照。初始禁用避免候选在冷备的自动恢复阶段抢先产生待证明的告警；不修改平台持久渠道开关来模拟恢复模式。
5. 临时 Compose 覆盖切换旧镜像，仅用于证明严格六字段旧读取器不能读取九字段配置。禁止运行旧 migrator、删除新表或恢复旧 PG 数据。
6. 切换桥接镜像，并给 ControlPlane 设置 `CompatibilityNotifications__Mode=ReadOnly`。接受精确六/九字段；九字段的两个布尔值和 TLS 枚举严格验证后，仅投影原六字段读取，数据库 JSON 不回写。未知、遗漏、重复或类型错误 fail closed。
7. 通知设置保存、规则创建/更新/启用/禁用、候选通知测试和重试路径返回 `423 notification_maintenance_mode`。正常登录、CSRF 与其他业务写入继续生效。响应标明 `X-WebAPI-Compatibility-Mode: NotificationReadOnly`；旧 UI 的相关保存会显示维护错误。
8. 由真实双网关请求和观测源驱动旧 Worker 生成新告警，核对数据库默认冻结策略 `policy.externalEnabled=false`、渠道列表为空且档案为空。不得用新 fixture 的模拟事件冒充旧 Worker 新告警。
9. 切回原候选镜像。比较受保护的原有行及本次新增标记；桥接期间新建的其他业务行允许额外存在。核对通知定义、既有投递/尝试、账号、辅助 YAML 来源包、DP 钥匙、SSO Provider/外部绑定及部署配置不变。
10. 原会话 Cookie 跨镜像继续有效；新建本地账号重新登录成功。旧 Worker 告警恢复后不补发。通过产品自带通知测试 API 新建两条任务，验证候选真实 Worker 的 SMTP STARTTLS 接受与 HTTPS HMAC 接受；不使用跨组织模拟事件或扩大历史账号 Scope。
11. 不论中途验收成功或失败，finally 都把克隆应用切回原候选并恢复全部来源与 fixture。失败尝试、备份和日志保留，不能重贴为成功证据。

## 验收与限制

单元门禁和集成测试只能证明其覆盖的行为。D1 必须另有实际恢复 receipt、完整备份 manifest 回读结果及 finally 就绪结果；任何缺失都阻止 D3 升级。SSO 数据/部署引用、DP 文件与已认证会话保留不等于完成一次新的 OIDC 登录；完整本机 Keycloak 登录归 D2/D3 单独验证。

桥接的通知与规则管理暂时只读，不发送外部通知。候选恢复后的新 SMTP/Webhook 接受仅代表自有本机服务接受；不代表企业 SMTP、企业 Webhook、企业 IM 或生产环境验收。企业通知端点接入和生产验收保留为后续工作。

最终候选五套完整回归、唯一整分支审查、原 4192 精确合并/冷备/升级及最终 ZIP 交付都须按 D2/D3 完成，不由此恢复说明替代。
