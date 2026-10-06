const scopeKey=s=>JSON.stringify([s.organizationId,s.projectId||null,s.environmentId||null]);
const grantKey=g=>scopeKey(g.scope)+':'+g.accessMode;
export const payload=rows=>({scopes:rows.map(({scope,accessMode})=>({scope:{...scope},accessMode}))});
export const toDraft=grants=>grants.map((g,i)=>({key:'existing-'+i,level:g.scope.environmentId?'environment':g.scope.projectId?'project':'organization',scope:{...g.scope},accessMode:g.accessMode,original:{...g.scope}}));
export const newRule=key=>({key,level:'environment',scope:{organizationId:'',projectId:null,environmentId:null},accessMode:'read'});
export function updateRule(row,field,value){
 const next={...row,scope:{...row.scope}};
 if(field==='accessMode')next.accessMode=value;
 else if(field==='level'){next.level=value;if(value==='organization'){next.scope.projectId=null;next.scope.environmentId=null;}else if(value==='project')next.scope.environmentId=null;}
 else{next.scope[field]=value||null;if(field==='organizationId'){next.scope.projectId=null;next.scope.environmentId=null;}if(field==='projectId')next.scope.environmentId=null;}
 return next;
}
export function validate(rows,tree){
 const seen=new Set();
 return rows.flatMap((r,i)=>{
  const errors=[],s=r.scope,prefix=`规则 ${i+1}：`,o=tree.organizations.find(x=>x.id===s.organizationId),p=tree.projects.find(x=>x.id===s.projectId),e=tree.environments.find(x=>x.id===s.environmentId);
  const ruleKey=grantKey(r);if(seen.has(ruleKey))errors.push(prefix+'与前面的规则完全重复，请移除重复项。');seen.add(ruleKey);
  const unchanged=r.original&&scopeKey(s)===scopeKey(r.original);
  if(!['read','read_write'].includes(r.accessMode))errors.push(prefix+'请选择只读或读写权限。');
  if(!['organization','project','environment'].includes(r.level))errors.push(prefix+'请选择范围层级。');
  if(!s.organizationId)errors.push(prefix+'请选择组织。');
  if(r.level!=='organization'&&!s.projectId)errors.push(prefix+'请选择项目；尚未选定时不会扩大到整个组织。');
  if(r.level==='environment'&&!s.environmentId)errors.push(prefix+'请选择环境；尚未选定时不会扩大到整个项目。');
  if((r.level==='organization'&&(s.projectId||s.environmentId))||(r.level==='project'&&s.environmentId))errors.push(prefix+'范围层级与资源选择不一致。');
  if(!unchanged&&((s.organizationId&&!o)||(s.projectId&&!p)||(s.environmentId&&!e)))errors.push(prefix+'新范围必须从可读取的资源中选择；已有不可见范围可原样保留。');
  if(p&&p.organizationId!==s.organizationId)errors.push(prefix+'项目不属于所选组织。');
  if(e&&(e.projectId!==s.projectId))errors.push(prefix+'环境不属于所选项目或组织。');
  return errors;
 });
}
export function summarize(before,after){
 const old=[...before],next=[...after];
 for(let i=old.length-1;i>=0;i--){const j=next.findIndex(g=>grantKey(g)===grantKey(old[i]));if(j>=0){old.splice(i,1);next.splice(j,1);}}
 let changed=0;for(let i=old.length-1;i>=0;i--){const j=next.findIndex(g=>scopeKey(g.scope)===scopeKey(old[i].scope));if(j>=0){changed++;old.splice(i,1);next.splice(j,1);}}
 return {added:next.length,removed:old.length,changed};
}
export async function loadScopeSnapshot(readUser,readGrants){
 for(let attempt=0;attempt<3;attempt++){
  const first=await readUser();if(!first?.revision)throw Error('用户或修订已失效，请刷新列表后重新打开。');
  const grants=await readGrants(),last=await readUser();
  if(first.revision===last?.revision)return {revision:first.revision,displayName:last.displayName,grants};
 }
 throw Error('用户修订持续变化，未采用可能过时的数据。请稍后重新读取。');
}
