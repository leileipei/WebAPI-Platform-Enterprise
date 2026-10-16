# 核心闭环验收

验收对象是已确认首期真实核心，原 40 页高保真原型仍完整保留。各页真实接入及后续能力以 console-coverage.md 为准；后续模块不显示模拟成功。

## 当前证据

|验收层|证据|实际覆盖|
|---|---|---|
|管理 API / PostgreSQL|完整集成回归 90 项，0失败0跳过|权限∩Scope∩有效状态、Cookie/CSRF、事务审计、ETag/幂等、导入/凭证、冻结审批、Outbox、真实原字节回滚|
|浏览器，1440×1024|evidence/m4-console/verification.json 和对应截图|真实登录与重登、只读直达403、撤Scope清缓存、412保留输入/显式采用最新修订、非法导入保留原文、封存禁编辑、工作路由不改运行流量、一次性Secret关闭不回显|
|审批与节点浏览器|evidence/m4-console/release-flow.json|两个独立Cookie审批会话，两真实Gateway确认后成功，独立回滚重新审批；1/2确认保持Publishing，超时Failed保留真实部分状态|
|真实容器跨服务|evidence/core-loop/scenarios.json|A→B 两网关业务响应；历史回滚A且序列更高；已进入A的请求在切换时仍完成A，新请求B|
|容器故障注入|evidence/core-loop/faults.json|通知丢失走poll；ACK丢失重试；LKG不可写保持旧流量/独立恢复；实际Key到期与跨API403；坏hash/外环境拒绝；CP/Redis停机下真实进程重启LKG；并发发布/回滚仅一者成功|

`complete:true` 只表示该证据文件所列检查通过。源码构建、数据库迁移、单元/集成、浏览器及真实容器分层记录，彼此不替代。最终汇总见 evidence/core-loop/verification.json；若文件缺失或 complete=false，该项尚未完成。

当前全分支审查发现的3项Important已完成代码修正；9项前端自动化与完整后端回归通过。最终真实浏览器复验已完成，见 evidence/core-loop/final-review.md 与 evidence/m4-console/final-review-qa.json；包括无需手动刷新撤权清详情、第二页用户412恢复及导入/后退输入保护。

## 交付与未验收范围

交付包含源码、32张核心源表与补充发布/身份/幂等表、EF迁移、集中版本和锁文件、固定镜像摘要、Compose、管理前端、协议与运维说明及脱敏证据。打包排除任何密码/API Key/Secret、数据库卷、.runtime、.secrets 与依赖缓存。

在本机 Docker Linux ARM64 验证；AMD64 编译与运行尚未验证，企业实网/TLS、真实数据库恢复、容量/SLA、HA、灾备、外部SSO与企业适配未验收。没有 5k RPS 或生产上线结论。

本文件上表记录首期核心验收。后续真实观测与告警阶段已经完成，28–33页接入范围与证据见 [40页映射](console-coverage.md) 和 [观测验收](evidence/observability/verification.json)。完整企业工作台、系统设置、标签运营、完整 Breaking Change 分析、复杂策略编辑器、SSO/首次改密、URL/YAML OpenAPI 导入仍为后续范围。原高保真交互演示的完整页面数量不等于全部业务后端已经上线。

## 独立本机运行与演示验收补充

[独立运行环境验收](evidence/local-runtime/verification.json)记录随机隔离项目的完整发布、三源、故障、重启与冷恢复演练；[长期演示验收](evidence/local-runtime/demo-activation.json)记录用户选择 A 后实际持久项目的创建、独立审批、双网关当前实例ACK、真实请求、短窗口三源数据和站内告警 Open → Ack → Resolved/Recovered。两份证据各自保留时间与实际范围。

长期项目为`webapi-enterprise-local`，管理入口4192，双网关4196/4197。本机Linux ARM64与合成演示数据的实际验证不替代AMD64、企业上游、生产TLS/HA、容量、外部通知或企业数据恢复验收。当前演示库尚未执行新的冷备份；[恢复清单](deployment/local-demo-recovery-checklist.md)是操作准备，不是本实例已有备份的证明。

本批独立版本流量策略的真实验收、回归、UI QA 与明确边界见 [策略交付索引](evidence/policies/delivery-index.md)。原 4192 数据与镜像保持不变。

## 2026-10-06 本机升级补充（当前状态）

上述首期、早期演示和独立策略段落为历史阶段记录。后续已完成流量策略、系统设置、OIDC SSO、独立Keycloak与网关重启修复的本机验收。2026-10-06已对原平台7卷和IdP4卷冷备份，并在新owner空卷完成独立恢复、新镜像Ready和旧镜像回退演练。原4192正常重启后双网关Ready、原账号/SSO/业务v4/seq4保持；详见[升级验收结果](../.worktrees/gateway-restart-readiness/docs/deployment/gateway-readiness-upgrade-result.md)。这不替代生产、企业实网、AMD64或容量/HA验收。

## 第3包：CORS、IP 与双向JSON Transform（2026-10-14）

固定源码为 `226dfe450865cd322d81eb5747bec791a71f7519`，镜像为 `sha256:4223703e79dfc88fe13631a32ac7bedf0c0a6da81370dd0610b908a7e6b9a8aa`。本轮在全新独立数据库及TEST/PROD各两个Gateway中完成正式场景；没有恢复4192数据，也没有更新原4192安装。

|验收层|实际结果|可核对证据|
|---|---|---|
|固定五组回归|3359通过、0失败、0跳过：Runtime234、Console297、Domain1065、Integration1409、Gateway354|[固定日志及Hash](evidence/network-policies/2026-10-14-226dfe4/acceptance-input.json)|
|真实场景|管理权限/ETag/预览无写入、三认证、CORS预检、IP三模式、Header/路径/Query、双向JSON/压缩/限额/原子失败、缓存/重试/配额、冻结修订、双实例ACK、2.0–2.3历史回滚及跨环境独立验证|[P3-01至P3-12门禁结果](evidence/network-policies/2026-10-14-226dfe4/acceptance-result.json)|
|1440px页面操作|15项实际操作及截图；只读账号无配置写入入口，仍可按policy.read做不持久化的JSON预览|[页面证据](evidence/network-policies/2026-10-14-226dfe4/public/P3-11.json)|
|真实观测|正式较短覆盖窗口的Metrics/Logs/Trace均Available，日志与根Trace关联；页面1小时窗口如实显示Partial并展示已收到事实|[观测证据](evidence/network-policies/2026-10-14-226dfe4/public/P3-10.json)|
|正向SDK E2E|1项通过、0失败、0跳过；读取真实严格证据及全部公开材料Hash|[SDK结果](evidence/network-policies/2026-10-14-226dfe4/sdk-e2e-result.json)|
|原环境保护|主工作区文件/hash/status、原4192身份及71表原数据保留；第2包暂停owner运行容器为0|[保护证据](evidence/network-policies/2026-10-14-226dfe4/public/P3-12.json)|

逐项覆盖、历史失败和最终独立审查状态见[本包交付索引](evidence/network-policies/2026-10-14-226dfe4/delivery-index.md)。旧节点案例只验证能力声明阻断，未安装旧二进制。HTTPS生产入口使用本轮受控、证书验证开启的独立夹具；企业DNS/TLS/LB、AMD64、HA、SLA和正式生产上线仍需对应验收。第2包继续暂停；main合并、远端推送、4192升级尚未执行。

第3包最终状态：原3项Important均已在226dfe4完成TDD修复及全套新固定验收；本次授权提交交付文档并合并、推送GitHub，实际集成状态以Git记录为准；4192未升级。
