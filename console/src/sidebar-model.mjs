// Resource detail pages inherit the group of their list entry.
export const sidebarGroups = [
  {id:'workbench', title:'工作台', items:[['/dashboard','企业工作台']]},
  {id:'organization', title:'组织与项目', items:[['/organizations','组织管理'],['/projects','项目管理'],['/environments','环境管理']]},
  {id:'api', title:'API 管理', items:[['/apis','API 目录'],['/imports','OpenAPI 导入'],['/routes','Route 管理']]},
  {id:'traffic', title:'流量与后端', items:[['/clusters','Cluster 列表']]},
  {id:'policy', title:'策略中心', items:[['/policies','策略中心']]},
  {id:'applications', title:'应用与凭证', items:[['/applications','应用列表']]},
  {id:'release', title:'发布与交付中心', items:[['/releases','环境发布'],['/delivery/artifacts','发布制品与验收'],['/delivery/policy','交付连接配置'],['/approvals','审批中心'],['/snapshots','配置快照']]},
  {id:'gateway', title:'Gateway', items:[['/nodes','Gateway 节点']]},
  {id:'observability', title:'可观测性', matchPaths:['/observability/apis'], items:[['/observability/metrics','监控总览'],['/observability/logs','访问日志'],['/observability/traces','Trace 查询'],['/observability/alerts','告警中心'],['/observability/alert-rules','告警规则']]},
  {id:'governance', title:'平台治理', items:[['/users','用户管理'],['/roles','角色管理'],['/permissions','权限字典'],['/scopes','数据范围'],['/audit','审计日志'],['/settings/sso','SSO 配置'],['/settings/system','系统设置'],['/coverage','能力与页面覆盖']]}
];

export function isSidebarItemActive(path, url) {
  const currentPath = path === '/' ? '/organizations' : path;
  return currentPath === url || currentPath.startsWith(url + '/');
}

export function sidebarGroupForPath(path) {
  return sidebarGroups.find(group => [...group.items.map(([url]) => url), ...(group.matchPaths || [])]
    .some(url => isSidebarItemActive(path, url)))?.id ?? null;
}

export function toggleSidebarGroup(expanded, selected) {
  return expanded === selected ? null : selected;
}
