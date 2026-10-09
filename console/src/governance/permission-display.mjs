const modules={organization:'组织管理',project:'项目管理',environment:'环境管理',api:'API 管理',route:'路由配置',cluster:'后端服务',policy:'策略管理',app:'应用管理',credential:'应用凭证',release:'发布管理',approval:'发布审批',gateway:'网关管理',audit:'审计日志',user:'用户管理',role:'角色管理',scope:'数据范围',system:'系统设置',metrics:'指标监控',log:'请求日志',trace:'链路追踪',alert:'告警管理'};
// Exact catalog codes only: extension permissions retain their server-provided meaning.
const definitions={
 'organization.read':['查看组织','read'],'organization.write':['维护组织','configure'],
 'project.read':['查看项目','read'],'project.write':['维护项目','configure'],
 'environment.read':['查看环境','read'],'environment.write':['维护环境','configure'],
 'api.read':['查看 API','read'],'api.create':['创建 API','configure'],'api.edit':['编辑 API','configure'],
 'api.version.read':['查看 API 版本','read'],'api.version.write':['维护 API 版本','configure'],
 'api.schema.read':['查看 API Schema','read'],'api.schema.write':['维护 API Schema','configure'],
 'route.read':['查看路由','read'],'route.write':['维护路由','configure'],
 'cluster.read':['查看后端服务','read'],'cluster.write':['维护后端服务','configure'],
 'policy.read':['查看策略','read'],'policy.write':['维护策略','configure'],
 'app.read':['查看应用','read'],'app.write':['维护应用','configure'],
 'credential.manage':['管理应用凭证','manage'],'app.permission.manage':['管理应用 API 授权','manage'],
 'release.read':['查看发布','read'],'release.create':['创建发布','configure'],
 'release.test.record':['登记来源测试','configure'],'release.test.accept':['独立测试验收','release'],'release.verify':['登记生产验证','release'],
 'release.publish':['执行发布','release'],'release.rollback':['执行回滚','release'],
 'approval.act':['处理发布审批','release'],'api.approve':['审批 API','release'],
 'gateway.read':['查看网关','read'],'gateway.operate':['执行网关运维','manage'],'gateway.config.read':['查看网关配置','read'],
 'audit.read':['查看审计日志','read'],'user.manage':['管理用户','manage'],'role.manage':['管理角色','manage'],
 'scope.manage':['管理数据范围','manage'],'system.manage':['管理系统设置','manage'],
 'metrics.read':['查看指标','read'],'log.read':['查看请求日志','read'],'trace.read':['查看链路追踪','read'],
 'alert.read':['查看告警','read'],'alert.operate':['处理告警','manage'],'alert.rule.manage':['管理告警规则','manage'],
 'system.sso.manage':['管理 SSO 身份源','manage']
};
export function describePermission(p){
 const definition=definitions[p.code];
 return {...p,label:p.name&&p.name!==p.code?p.name:definition?.[0]||p.code,moduleLabel:modules[p.module]||p.module||'未分组',column:definition?.[1]||'other'};
}
export function permissionGroups(items,query=''){
 const groups=new Map(),needle=query.trim().toLocaleLowerCase();
 for(const source of items){const p=describePermission(source);if(needle&&![p.code,p.label,p.module,p.moduleLabel,p.description].some(s=>String(s||'').toLocaleLowerCase().includes(needle)))continue;
  const key=p.module||'';if(!groups.has(key))groups.set(key,{key,label:p.moduleLabel,items:[]});groups.get(key).items.push(p);
 }
 return [...groups.values()];
}
export function mayConfigurePermissions(role,items,status={}){
 return !role.isSystem&&!status.loading&&!status.error&&Array.isArray(items)&&items.length>0&&Array.isArray(role.permissions)&&role.permissions.every(code=>items.some(p=>p.code===code));
}
