# 原40页面与真实管理控制台对应

原V1.4完整高保真原型保留在 ../prototype，下面说明新真实服务控制台的能力。已接入指核心API真实读写，不代表原页所有后续能力已经实现；后续能力不会显示模拟成功。

|原页|页面|真实入口|状态与范围|
|---|---|---|---|
|1|登录|/|已接入本地Cookie与OIDC SSO登录；保留本地管理员恢复入口，首次改密后续|
|2|企业工作台|`/dashboard`|真实只读聚合；当前项目 API、当前环境24h指标/节点/告警/最近发布；待审批仅最近50条|
|3|组织|/organizations|已接入|
|4|项目|/projects|已接入；负责人由创建者记录|
|5|环境|/environments|已接入；环境访问地址、前缀及授权内网入口已完成独立审查并安装至4192|
|6|API目录|/apis|已接入；完整标签与指标后续|
|7|六步向导|/apis/{id}/wizard|已接入逐步保存与发布Review|
|8|OpenAPI导入|/imports|已接入3 JSON原文、Operation映射；URL/YAML后续|
|9|API详情|/apis/{id}|已接入工作/运行/待发布事实；实际路由地址、无凭证示例及环境OpenAPI副本已安装至4192|
|10|版本|/apis/{id}|已接入版本Tab|
|11|版本比较与风险评审|/apis/{id}/versions/compare|真实比较、历史/导出、追加风险评审和可选发布证据已完成固定源码隔离验收及1440px UI QA；规则覆盖受限；4192软件包已升级，该比较流程本批未复验|
|12|Route|/routes|已接入环境专属路由|
|13|参数Schema|/apis/{id}|已接入参数/Schema Tab与原文编辑；完整树和例验证后续|
|14|Cluster|/clusters|已接入|
|15|Destination|/clusters/{id}|已接入权重/健康配置；立即探测后续|
|16|策略中心|/policies|已接入真实列表、类型/范围过滤、复制、引用与工作/运行修订|
|17|策略编辑|/policies/{id}|已接入四类动态字段、服务端校验、412保留输入、引用保护及审批发布|
|18|应用|/applications|已接入|
|19|应用详情|/applications/{id}|已接入|
|20|凭证|/applications/{id}|已接入凭证Tab，一次性Secret及有效期/撤销|
|21|API授权|/applications/{id}|已接入授权Tab及环境/窗口|
|22|发布与交付中心|/releases；/delivery/overview|已接入原发布及跨环境交付总览、不可变制品与TEST→PROD晋级；原4192已安装，任意Stage编排后续|
|23|发布详情|/releases/{id}|已接入审批/真实ACK/回滚/重试、生成制品及正式/恢复晋级追溯；ACK与生产业务验证完成分别呈现，已安装4192|
|24|审批中心|/approvals|已接入授权范围集中收件箱、晋级来源/制品摘要与两级生产审批；独立测试验收在制品详情处理；受限资料不当零风险|
|25|Snapshot|/snapshots|已接入脱敏管理视图和下载|
|26|网关节点|/nodes|已接入实际/目标版本及sequence|
|27|节点详情|/nodes/{id}|已接入事实事件|
|28|监控总览|/observability/metrics|已接入真实Prometheus，12项KPI/7类趋势、分组排序、节点覆盖；估计量和部分数据明确呈现|
|29|API监控|/observability/apis/{apiId}|已接入应用/状态/后端分组，日志/Trace及预填规则按独立权限开放|
|30|访问日志|/observability/logs|已接入真实Loki、签名游标、脱敏详情/CSV与只在POST内存使用的精确IP筛选；源上限显式截断|
|31|Trace|/observability/traces|已接入真实Tempo、Server/Client瀑布图、安全span标签；同Trace跨环境隔离，缺失/采样/源故障分开|
|32|告警中心|/observability/alerts|已接入PG事件、Ack/Resolve/Silence/Unsilence与流转；Unknown不当恢复，Metrics关联选择冻结环境|
|33|告警规则|/observability/alert-rules|已接入创建/编辑/启停、范围预览、只读测试与412保护；Email/Webhook/EnterpriseIm仅意向|
|34|用户|/users|已接入平台级用户治理|
|35|角色|/roles|已接入系统角色只读与自定义角色|
|36|权限矩阵|/roles|已接入角色权限选择|
|37|数据范围|/scopes|已接入精确Scope规则；结构化表单继续优化|
|38|审计|/audit|已接入安全变更视图/筛选|
|39|SSO/OIDC|/settings/sso|已接入Provider治理、显式账号绑定、修订保护与审计；本机独立Keycloak登录已验证；企业IdP与生产TLS待验收|
|40|系统设置|/settings/system|已接入平台五组17字段、保存/预览/引用/修订保护与审计；登录、账号、路由和审计CSV有实际消费者；部署参数、保留目标及通知仅配置意向|

28–33真实范围见 [观测验收](evidence/observability/verification.json)、[数据字典](observability-data-dictionary.md)与[运行手册](deployment/observability-runbook.md)。原40页高保真原型仍完整保留；四类流量策略证据见 [策略交付](evidence/policies/delivery-index.md)；OIDC SSO 与系统设置已部署至本机4192，独立Keycloak位于4194。完整 OpenAPI 兼容性覆盖和企业外部通知投递仍需对应证据；企业实际身份源、TLS/HA和容量需单独验收。当前入口、维护方式和验收边界见 [本机运行摘要](deployment/current-local-status.md)。

工作台范围与验证说明见 [企业工作台](workbench.md)。

环境访问地址固定提交验收见 [交付索引](evidence/environment-access/delivery-index.md)与[运行手册](deployment/environment-access-runbook.md)。本批已完成独立审查、原数据升级/回退演练和原4192实际安装。

## 发布与交付新增页面（2026-10-09）

|功能|真实入口|当前范围|
|---|---|---|
|交付总览|`/delivery/overview`|真实授权聚合，实际配置/待验证/失败与部分可见范围；不以受限计数表示0|
|制品与测试验收|`/delivery/artifacts`、`/delivery/artifacts/{id}`|冻结制品、PDF/TXT报告、人工证据登记、独立验收/撤销；不代执行外部测试|
|交付连接|`/delivery/policy`|同项目非生产来源→生产目标、Legacy/PromotionRequired、必需测试类型与有效期；原业务默认Legacy|
|发布晋级|`/delivery/promotions`、`/delivery/promotions/{id}`|目标映射、环境差异、预检、两级审批、真实下发、ACK及独立生产验证；人工回滚沿用原发布入口|

以上固定源码、整分支审查与唯一修复批次、2266项回归、真实双环境故障及原4192安装证据见[交付索引](evidence/release-promotion/delivery-index.md)。任意流水线/Stage编排、CI/CD接入、紧急免审发布、灰度或自动回滚仍是后续能力，界面不提供模拟成功入口。
