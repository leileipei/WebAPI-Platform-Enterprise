# 原40页面与真实管理控制台对应

原V1.4完整高保真原型保留在 ../prototype，下面说明新真实服务控制台的能力。已接入指核心API真实读写，不代表原页所有后续能力已经实现；后续能力不会显示模拟成功。

|原页|页面|真实入口|状态与范围|
|---|---|---|---|
|1|登录|/|已接入本地Cookie登录；SSO/首次改密后续|
|2|企业工作台|/coverage|后续指标工作台；已有发布/节点事实在对应页面|
|3|组织|/organizations|已接入|
|4|项目|/projects|已接入；负责人由创建者记录|
|5|环境|/environments|已接入|
|6|API目录|/apis|已接入；完整标签与指标后续|
|7|六步向导|/apis/{id}/wizard|已接入逐步保存与发布Review|
|8|OpenAPI导入|/imports|已接入3 JSON原文、Operation映射；URL/YAML后续|
|9|API详情|/apis/{id}|已接入工作/运行/待发布事实|
|10|版本|/apis/{id}|已接入版本Tab|
|11|Breaking Change|/releases/{id}|后续完整引擎；详情仅基础资源比较|
|12|Route|/routes|已接入环境专属路由|
|13|参数Schema|/apis/{id}|已接入参数/Schema Tab与原文编辑；完整树和例验证后续|
|14|Cluster|/clusters|已接入|
|15|Destination|/clusters/{id}|已接入权重/健康配置；立即探测后续|
|16|策略中心|/coverage|后续；Route首期认证/超时可配置|
|17|策略编辑|/coverage|后续动态扩展策略|
|18|应用|/applications|已接入|
|19|应用详情|/applications/{id}|已接入|
|20|凭证|/applications/{id}|已接入凭证Tab，一次性Secret及有效期/撤销|
|21|API授权|/applications/{id}|已接入授权Tab及环境/窗口|
|22|发布中心|/releases|已接入核心发布|
|23|发布详情|/releases/{id}|已接入审批/真实ACK/回滚/重试|
|24|审批中心|/approvals|已接入当前环境待审批；跨环境集中收件箱后续|
|25|Snapshot|/snapshots|已接入脱敏管理视图和下载|
|26|网关节点|/nodes|已接入实际/目标版本及sequence|
|27|节点详情|/nodes/{id}|已接入事实事件|
|28|监控总览|/coverage|后续外部指标接入|
|29|API监控|/coverage|后续|
|30|访问日志|/coverage|后续|
|31|Trace|/coverage|后续；审计可按Trace筛选|
|32|告警中心|/coverage|后续|
|33|告警规则|/coverage|后续|
|34|用户|/users|已接入平台级用户治理|
|35|角色|/roles|已接入系统角色只读与自定义角色|
|36|权限矩阵|/roles|已接入角色权限选择|
|37|数据范围|/scopes|已接入精确Scope规则；结构化表单继续优化|
|38|审计|/audit|已接入安全变更视图/筛选|
|39|SSO/OIDC|/coverage|后续企业身份接入|
|40|系统设置|/coverage|后续持久设置；运行配置使用显式部署配置|
