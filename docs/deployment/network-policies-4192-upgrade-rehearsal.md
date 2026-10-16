# 4192 网络策略升级演练结果

2026-10-14。本轮已授权的冷备、独立旧数据升级及回退演练通过，候选固定版本为 `226dfe4`。原 Console 4192 仍运行 `9d58329`，原 Keycloak 保持既有 owner 和配置，没有切换原软件或迁移原数据库。第 2 包保持暂停。

本结果承接[升级准备审阅包](network-policies-4192-upgrade-review.md)。准备包描述的是执行前事实，本结果记录已执行的门禁。汇总文件与摘要见[核验清单](../evidence/network-policies/4192-upgrade-rehearsal-20261014/manifest.json)。

## 固定版本和执行边界

| 项目 | 本轮实际结果 |
| --- | --- |
| 原实例源码 | `9d5832952ed91fb99defc2cf3eae81cd8cee3afa` |
| 候选源码 | `226dfe450865cd322d81eb5747bec791a71f7519` |
| 候选镜像 | `sha256:4223703e79dfc88fe13631a32ac7bedf0c0a6da81370dd0610b908a7e6b9a8aa` |
| GitHub 已集成 main | `a30ce5bf8291539eac2640ea02839b403ccd68c8`；本轮无新增源码提交或推送 |
| 升级克隆 | 独立 owner `e96ed9c3-bf2e-4cf4-a402-955f8634f796`；Console 62867，Gateway 62868／62869 |
| 第二个冷恢复实例 | 全新 owner、空卷及随机端口；验证后已停止，卷和证据保留 |
| 原服务和工作区 | 原 14 个平台容器、2 个 IdP 容器运行，维护入口 Ready；main 既有文件、状态及原配置摘要保持 |

## 备份和旧数据迁移

新平台冷备的 39 文件与 IdP 冷备的 12 文件全部回读 SHA256 一致，另保存 173 文件的原维护工具归档。备份时短暂停服，完成后先恢复原服务，再进行隔离演练。[备份证据](../evidence/network-policies/4192-upgrade-rehearsal-20261014/backup.json)。

第一次恢复使用原 `9d58329`，原 71 张表的 1005 条受保护记录及 4 份发布候选保留，原本地账号和两个 Gateway 的 `/orders` 返回 200。独立恢复的 Keycloak 使用自己的 issuer 和回调地址；复用原 SSO 用户身份，角色、范围和功能权限未增加。[旧版恢复](../evidence/network-policies/4192-upgrade-rehearsal-20261014/restore-old.json)、[SSO 证据](../evidence/network-policies/4192-upgrade-rehearsal-20261014/sso.json)。

候选迁移新增 `release_frozen_contracts`，严格回填 4 条冻结契约，0 个不一致；原候选字节 Hash 保持。迁移前基线含克隆 SSO 设置及审计，因此升级阶段保护了 1028 条原记录，并精确允许新增一条 `20261012004548_ReleaseFrozenContracts` 迁移历史。8 个应用的镜像、281 个应用文件摘要和 3 个前端资源摘要一致，候选 Ready，双网关原 API 及实际 OIDC code 交换通过。[升级与迁移证据](../evidence/network-policies/4192-upgrade-rehearsal-20261014/upgrade.json)。

SSO 本轮采用真实 HTTP/OIDC 流程验证；原 1440px UI 验收保留在第 3 包交付中，本轮未新增浏览器 SameSite 验收。原报告卷实际为 0 文件，字节一致仅证明空卷恢复，不能表述为非空报告样本验收。

## 策略发布和执行

在原业务所在的克隆环境创建独立测试 API、应用、凭证和回显后端，原 API 版本继续纳入发布。三类策略经过既有独立两级审批，发布为快照 2.3，配置版本 5、发布序列 5；两个当前 Gateway 实例均 ACK。

| 门禁 | 结果 |
| --- | --- |
| 请求和响应 JSON 预览 | 无真实转发；请求和响应方向分别验证 |
| JSON 字段映射 | 新增、替换、删除、复制、移动，以及响应字段新增通过；9007199254740993 原数值文本保留 |
| Header、路径和 Query | 请求及响应 Header 为 qa；路径改为 /__network/echo，删除 omit 并添加 qa=1 |
| CORS | 双网关允许来源 200、预检 204、拒绝来源 403 |
| IP | 新一轮独立审批发布 DenyList，双网关均 403 |
| 原调用 | 原 /orders 两个网关持续 200，API Key 未转发上游 |
| 三源观测 | 同一请求的日志、链路与指标均 Available；实际采样配置为 1，仅用于隔离演练 |
| 历史业务回滚 | 恢复配置版本 4、快照 2.0，发布序列提高到 7，两个实例 ACK；原载荷逐字节一致，新测试路由 404，原 API 200 |

[策略门禁](../evidence/network-policies/4192-upgrade-rehearsal-20261014/policies.json)、[双网关执行](../evidence/network-policies/4192-upgrade-rehearsal-20261014/policy-execution.json)、[双向预览](../evidence/network-policies/4192-upgrade-rehearsal-20261014/policy-previews.json)、[三源观测](../evidence/network-policies/4192-upgrade-rehearsal-20261014/observation.json)。本轮是原数据升级兼容性演练；第 3 包完整 TEST／PROD、历史四种快照及 1440px QA 仍以[固定交付](../evidence/network-policies/2026-10-14-226dfe4/delivery-index.md)为准。

## 软件回退和整库恢复

新策略写入前，实际 desired 和两个节点 LKG 均为 2.0，网络策略记录为 0。此条件下，在已迁移数据库上切换旧 `9d58329` 成功，原账号、SSO、双网关及文件摘要通过；没有回滚数据库。再切回 `226dfe4` 后同样 Ready，1043 条回退前基线记录保留。[有条件软件回退](../evidence/network-policies/4192-upgrade-rehearsal-20261014/software-fallback.json)。

业务回滚后，三个新网络策略工作记录仍保留。先前旧软件演练只覆盖 0 条此类记录，不能扩大成升级后可无条件降级。当前审阅规则阻断直接旧软件切换；采用候选兼容修复前进，或经明确数据处置授权恢复整套冷备。[回退条件](../evidence/network-policies/4192-upgrade-rehearsal-20261014/recovery-conditions.json)。

第二个全新 owner 完整恢复原冷备并运行旧镜像。原表与原候选字节、双 Gateway 200、11 份秘密／演示凭证文件均核对通过。报告、Cookie 密钥、缓存 HMAC、通知秘密和通知夹具秘密五类卷在应用启动前逐文件比较一致，文件数依次为 0、1、1、3、5；原 IdP 的五份秘密文件与独立恢复副本摘要一致。恢复实例验证后停止，原实例和升级克隆均未被覆盖。[整库冷恢复](../evidence/network-policies/4192-upgrade-rehearsal-20261014/cold-restore.json)。

## 保留的失败记录

本轮未改产品源码。以下操作或测量失败保留在私有目录，摘要与操作助手来源见[演练溯源](../evidence/network-policies/4192-upgrade-rehearsal-20261014/rehearsal-provenance.json)。

| 初次失败 | 原因和后续验证 |
| --- | --- |
| SSO 登录返回 200 而非重定向 | HTTP 助手输入含末尾换行，实际 IdP 密码定义会去末尾换行；仅规范化私有输入，实际秘密未更换，重新登录通过 |
| 迁移保护拒绝 | 迁移成功，但旧保护基线不允许新增 EF 历史行；独立核对仅新增指定迁移及派生表后，以精确行摘要补充允许项，未豁免原行保护 |
| 软件回退前 SQL 检查失败 | 使用了错误列名 policy_type；核对真实列 type 后复验，首次失败时没有切换服务 |
| 后端夹具检查失败 | 与克隆容器切换并发，检查到了已移除的实例；等待软件切换验收结束后串行重做 |
| Destination 白名单拒绝 | prepareContext 重建了手写覆盖；改用既有 configureRuntime 持久化克隆专用上游，原白名单保留 |
| 链路 Available 等待超时 | 请求带未导出的外部父 Span，实际链路正确返回 Partial；按既有方法清空 traceparent 重新测量，三源 Available，门禁未降低 |

## 原实例切换审阅

原实例最终 Ready、原库保护及 main 本地修改通过，原软件仍为 9d58329，第 2 包运行容器为 0。[最终保护](../evidence/network-policies/4192-upgrade-rehearsal-20261014/protection.json)。

下一阶段的具体对象是原 owner `6d7e3fda-88ea-4315-a8ef-df9e7cd995c8`，保持 4192／4196／4197、账号、权限、IdP 和持久卷，升级到本页列出的固定候选镜像。执行前再次核对保护基线与漂移并生成紧邻切换的冷备；使用固定维护工具和 preparePinnedPipelineContext 保留基础设施 pin，串行协调 release、runtime 与维护清单，迁移时 initialized=true，不重新 bootstrap。

原切换完成标志仍是候选应用与静态资源摘要一致、原账号与 SSO、双节点当前实例、原授权调用、三源与告警、原秘密及报告保护、维护入口 Ready。遇到迁移或启动失败，按上面的实际回退条件处理；整库回退涉及切换后新增业务事实，需明确处置授权。本轮授权仅含冷备与隔离演练，原 4192 停服升级和维护清单切换仍需另行授权。
