import {EnvironmentRuntimePanel,type EnvironmentRuntimeView} from '../environments/EnvironmentRuntimePanel';
import {ManagementDirectory,type ManagementEntry} from '../governance/ManagementDirectory';
import {UserRolesEditor, type RoleDraft, type RoleOption, type AssignmentUser} from '../governance/UserRolesEditor';
import {PasswordResetDialog} from '../auth/PasswordResetDialog';import {canResetLocalPassword} from '../auth/password-state.mjs';import {EnvironmentAccessEditor} from '../environments/EnvironmentAccessEditor';
import {RoleDirectory} from '../governance/RoleDirectory';
import {ScopeEditor} from '../governance/ScopeEditor';import {SsoUserEditor} from './SsoUserEditor';import {saveCsvBlob} from '../observability/browser-actions.mjs';import {useState,useRef,useEffect} from 'react';import {apiRequest} from '../api/client';import {useSession} from '../auth/SessionProvider';import {useWorkspace} from '../Shell';import {Editor,Table,Feedback,useRemote,type Field,Details} from '../ui';import type {Page,Resource,Scope} from '../api/types';
const titles:Record<string,string>={organizations:'组织管理',projects:'项目管理',environments:'环境管理',users:'用户管理',scopes:'数据权限',audit:'审计日志'};
const status:Field={key:'status',label:'状态',options:[{value:'Active',label:'启用'},{value:'Disabled',label:'停用'}]};
const resourceFields:Field[]=[{key:'code',label:'编码',required:true},{key:'name',label:'名称',required:true},status];
type Edit={organizationEditor?:{id?:string;userId:string};title:string;fields:Field[];initial:Record<string,any>;path:string;method:string;revision?:number;transform?:(v:any)=>any};
export function Governance({kind}:{kind:string}){
 return kind==='roles'||kind==='permissions' ? <RoleDirectory kind={kind}/> : <ResourceGovernance kind={kind}/>;
}
function ResourceGovernance({kind}:{kind:string}){const ws=useWorkspace(),session=useSession(),{can,canAct,refresh}=session;const[exporting,setExporting]=useState(false),[exportNote,setExportNote]=useState(''),exportEpoch=useRef(0);
 useEffect(()=>{const invalidate=()=>{exportEpoch.current++;setExporting(false);setExportNote('');};window.addEventListener('session-expired',invalidate);window.addEventListener('permission-refresh',invalidate);return()=>{exportEpoch.current++;window.removeEventListener('session-expired',invalidate);window.removeEventListener('permission-refresh',invalidate);};},[JSON.stringify([session.user?.id,session.user?.permissions,session.user?.scopes])]);
 const[management,setManagement]=useState<{entry:ManagementEntry;authority:string}>();
 const[passwordReset,setPasswordReset]=useState<any>(null);const[passwordNote,setPasswordNote]=useState('');
 const[environmentEdit,setEnvironmentEdit]=useState<any>(),[environmentNote,setEnvironmentNote]=useState('');
 const[scopeEdit,setScopeEdit]=useState<any>(null);const[ssoEdit,setSsoEdit]=useState<{user?:any}|null>(null);const[edit,setEdit]=useState<Edit>(),[details,setDetails]=useState<any>(),[query,setQuery]=useState(''),[page,setPage]=useState(1),[filter,setFilter]=useState({traceId:'',resourceId:''});const users=useRemote<Page<any>>(kind==='scopes'?`/users?page=${page}`:null),roles=useRemote<RoleOption[]>(kind==='users'&&can('role.manage')?'/roles':null);
 const path=kind==='organizations'?'/organizations':kind==='projects'?(ws.organizationId?`/organizations/${ws.organizationId}/projects`:null):kind==='environments'?(ws.projectId?`/projects/${ws.projectId}/environments`:null):kind==='scopes'?null:kind==='audit'?`/audit-logs?traceId=${encodeURIComponent(filter.traceId)}&resourceId=${encodeURIComponent(filter.resourceId)}&page=${page}`:`/${kind}`;
 const remote=useRemote<any>(path&&(path.includes('?')?path:path+`?page=${page}`),0,false,kind==='users');const raw=kind==='scopes'?users.data?.items:remote.data?.items||remote.data;const rows=(Array.isArray(raw)?raw:[]).filter(r=>!query||[r.name,r.code,r.username,r.displayName,r.action,r.module,r.description].some(v=>String(v||'').toLowerCase().includes(query.toLowerCase())));
 async function exportCsv(){const epoch=exportEpoch.current;setExporting(true);setExportNote('');try{const response=await apiRequest<Response>(`/audit-logs/export?traceId=${encodeURIComponent(filter.traceId)}&resourceId=${encodeURIComponent(filter.resourceId)}`,{responseType:'response'});const blob=await response.blob();if(exportEpoch.current!==epoch)return;saveCsvBlob(blob,`audit-${new Date().toISOString().slice(0,10)}.csv`);setExportNote(`已导出 ${response.headers.get('X-Export-Row-Count')||0} 条（授权范围内）${response.headers.get('X-Export-Truncated')==='true'?'；已达10000条或8MiB上限，请缩小筛选后分批导出':''}`);}catch(e){if(exportEpoch.current===epoch)setExportNote((e as Error).message);}finally{if(exportEpoch.current===epoch)setExporting(false);}}
 const columns:[string,string][]=kind==='users'||kind==='scopes'?[['username','用户名'],['authSource','认证来源'],['displayName','姓名'],['email','邮箱'],['status','状态'],...(kind==='users'?[['roleSummary','角色'],['scopeSummary','数据范围'],['mustChangePassword','须改密']] as [string,string][]:[]),['revision','修订']]:kind==='audit'?[['createdAt','时间'],['action','操作'],['resourceType','资源类型'],['resourceId','资源 ID'],['traceId','Trace ID']]:[['name','名称'],['code','编码'],['status','状态'],...(kind==='projects'?[['ownerSummary','负责人'],['environmentCount','环境数'],['apiCount','API 数'],['applicationCount','项目应用数']] as [string,string][]:[]),...(kind==='environments'?[['isProduction','生产环境'],['desiredConfigVersion','目标版本'],['deploymentSequence','发布序列']] as [string,string][]:[]),['revision','修订']];
 const baseScope=kind==='organizations'?undefined:{organizationId:ws.organizationId,...(kind==='environments'?{projectId:ws.projectId}:{})};
 const permission=kind==='users'?'user.manage':kind==='scopes'?'scope.manage':`${kind.slice(0,-1)}.write`;
 // GET and POST /users require the same platform grant. Session permissions also include organization roles.
 const canCreate=kind==='organizations'?canAct('organization.write',null):kind==='users'
   ? can(permission)&&!remote.loading&&!remote.error&&Array.isArray(remote.data?.items)
   : can(permission,baseScope,true)&&(!['projects','environments'].includes(kind)||!!(kind==='projects'?ws.organizationId:ws.projectId));
 const organizationEditor=edit?.organizationEditor;
 const organizationEditorAllowed=!organizationEditor||(organizationEditor.userId===session.user?.id&&canAct('organization.write',organizationEditor.id?{organizationId:organizationEditor.id}:null));
 useEffect(()=>{if(!organizationEditorAllowed)setEdit(undefined);},[organizationEditorAllowed]);
 const creatingLocalUser=kind==='users'&&edit?.method==='POST';
 useEffect(()=>{
   if(creatingLocalUser&&(remote.errorStatus===403||remote.errorStatus===404))setEdit(undefined);
 },[creatingLocalUser,remote.errorStatus]);
 function open(row?:any){if(kind==='environments'){setEnvironmentEdit(row||{projectId:ws.projectId});return;}const initial=row?{...row}:{status:'Active',isProduction:false,sortOrder:0};let fields=resourceFields,path=row?`/${kind}/${row.id}`:kind==='projects'?`/organizations/${ws.organizationId}/projects`:kind==='environments'?`/projects/${ws.projectId}/environments`:`/${kind}`;if(kind==='users'){fields=[...(row?[]:[{key:'username',label:'用户名',required:true},{key:'password',label:'初始密码',type:'password',required:true,help:'遵循当前平台密码策略，不会回显'}]),{key:'displayName',label:'显示名称',required:true},{key:'email',label:'邮箱',type:'email'},...(row?[status]:[])];}if(kind==='environments')fields=[...fields,{key:'isProduction',label:'生产环境',type:'checkbox'},{key:'sortOrder',label:'排序',type:'number'},{key:'releasePolicyId',label:'审批模板 ID',help:'生产发布必须配置审批模板；可在发布中心管理'}];setEdit({organizationEditor:kind==='organizations'&&session.user?{id:row?.id,userId:session.user.id}:undefined,title:(row?'编辑':'新建')+titles[kind],fields,initial,path,method:row?'PUT':'POST',revision:row?.revision,transform:v=>({...v,...(kind==='environments'?{releasePolicyId:v.releasePolicyId||null}:{}),...(kind==='users'?{email:v.email||null}:{})})});}
 const [roleDraft,setRoleDraft]=useState<RoleDraft>(),[roleNote,setRoleNote]=useState('');
 const [roleReload,setRoleReload]=useState<{users?:Date;roles?:Date}>();
 function reloadRoleSources(){
   if(kind==='users')setRoleReload({users:remote.updated,roles:roles.updated});
   remote.reload();
   roles.reload();
 }
 const selectedScope=[[ws.organizationId,ws.tree.organizations],[ws.projectId,ws.tree.projects],[ws.environmentId,ws.tree.environments]] as const;
 const roleContext=JSON.stringify([session.user?.id,session.user?.permissions,session.user?.scopes,...selectedScope.map(([id,items])=>[id,items.find(item=>item.id===id)?.status])]);
 const roleCatalogReady=kind==='users'&&can('user.manage')&&can('role.manage')&&!roles.loading&&!roles.error
   &&Array.isArray(roles.data)&&roles.data.length>0&&roles.data.every(role=>typeof role?.id==='string'&&role.id.length>0)
   &&!remote.loading&&!remote.error
   &&(!roleReload||(remote.updated!==roleReload.users&&roles.updated!==roleReload.roles));
 function mayEditRoles(row:AssignmentUser) {
   return roleCatalogReady&&Array.isArray(remote.data?.items)&&remote.data.items.includes(row)
     &&Number.isSafeInteger(row.revision)&&row.revision>0&&Array.isArray(row.roleIds)
     &&row.roleIds.every(id=>typeof id==='string'&&id.length>0);
 }
 const currentDraft=roleDraft&&roleDraft.context===roleContext&&roleDraft.catalog===roles.data&&mayEditRoles(roleDraft.user)?roleDraft:undefined;
 useEffect(()=>{
   if(roleDraft&&!currentDraft){
     setRoleDraft(undefined);
     setRoleNote('用户、角色目录或授权范围已更新，请重新打开分配角色并核对。');
   }
 },[roleDraft,currentDraft]);
 function editRoles(user:AssignmentUser){
   if(!roles.data||!mayEditRoles(user))return;
   setRoleNote('');
   setRoleDraft({user,catalog:roles.data,context:roleContext});
 }

 const managementAuthority=JSON.stringify([roleContext,kind,ws.organizationId,ws.projectId,session.error,session.user?.rolePermissions,session.user?.isPlatformAdministrator]);
 const runtimeAuthority=JSON.stringify([managementAuthority,ws.environmentId]);
 const [runtime,setRuntime]=useState<{id:string;view:EnvironmentRuntimeView;authority:string}>();
 useEffect(()=>setRuntime(undefined),[runtimeAuthority]);
 function environmentScope(row:Resource):Scope|undefined {
   if(!ws.scopeReady||row.projectId!==ws.projectId)return undefined;
   const project=ws.tree.projects.find(item=>item.id===row.projectId&&item.organizationId===ws.organizationId);
   if(!project?.organizationId||!ws.tree.environments.some(item=>item.id===row.id&&item.projectId===project.id))return undefined;
   return {organizationId:project.organizationId,projectId:project.id,environmentId:row.id};
 }
 const runtimeRow:Resource|undefined=kind==='environments'&&runtime?.authority===runtimeAuthority&&!remote.error?rows.find(row=>row.id===runtime.id):undefined;
 const runtimeScope=runtimeRow&&environmentScope(runtimeRow);
 const mayReadRuntime=(view:EnvironmentRuntimeView,scope:Scope|undefined)=>!!scope&&canAct(view==='snapshot'?'gateway.config.read':'gateway.read',scope);
 function runtimeActions(row:Resource) {
   const scope=environmentScope(row);
   return <>{(['snapshot','nodes'] as const).map(view=><button key={view} className="text-btn" disabled={!mayReadRuntime(view,scope)}
     onClick={()=>setRuntime({id:row.id,view,authority:runtimeAuthority})}>{view==='snapshot'?'查看当前快照':'Gateway 绑定'}</button>)}</>;
 }
 const showManagement=(selected:ManagementEntry)=>setManagement({entry:selected,authority:managementAuthority});
 const mayAdminister=(org:string)=>canAct('user.manage',null)&&canAct('scope.manage',null)&&canAct('organization.write',{organizationId:org});
 const tableRows=rows.map(row=>{
   if(kind==='users')return {...row,roleSummary:(row.roleIds||[]).map((id:string)=>roles.data?.find(r=>r.id===id)?.name||id).join('、')||'无',
     scopeSummary:Number.isInteger(row.scopeCount)?row.scopeCount+' 条 · 可写 '+row.writableScopeCount:'权限受限'};
   if(kind==='projects')return {...row,ownerSummary:row.ownerDisplayName||row.ownerUserId||'未指定'};
   return row;
 });
 const actions=(display:any)=>{const row=rows.find(item=>item.id===display.id)??display;return kind==='audit'?<button className="text-btn" onClick={()=>setDetails(row)}>查看变更</button>:kind==='scopes'?<button className="text-btn" disabled={!can('scope.manage')} onClick={()=>setScopeEdit(row)}>配置 Scope</button>:<><button className="text-btn" disabled={!(kind==='organizations'?canAct(permission,{organizationId:row.id}):can(permission,kind==='users'?undefined:{organizationId:ws.organizationId,projectId:kind==='projects'?row.id:kind==='environments'?row.projectId:undefined,environmentId:kind==='environments'?row.id:undefined},true))||row.isSystem} onClick={()=>open(row)}>编辑</button>{kind==='environments'&&runtimeActions(row)}{kind==='users'&&canResetLocalPassword(session.user,row)&&<button className="text-btn" onClick={()=>setPasswordReset(row)}>重置密码</button>}{kind==='users'&&row.authSource==='sso'&&<button className="text-btn" disabled={!can('user.manage')||!can('system.sso.manage')} onClick={()=>setSsoEdit({user:row})}>SSO 绑定</button>}{kind==='users'&&<button className="text-btn" disabled={!mayEditRoles(row)} onClick={()=>editRoles(row)}>分配角色</button>}{kind==='organizations'&&<><button className="text-btn" disabled={!can('organization.read',{organizationId:row.id})} onClick={()=>showManagement({kind:'detail',row})}>组织详情</button>{mayAdminister(row.id)&&<button className="text-btn" onClick={()=>showManagement({kind:'administrators',row})}>管理员设置</button>}</>}{kind==='projects'&&<>{canAct('project.write',{organizationId:row.organizationId,projectId:row.id})&&<button className="text-btn" onClick={()=>showManagement({kind:'owner',row})}>设置负责人</button>}<button className="text-btn" onClick={()=>ws.selectScope({organizationId:row.organizationId,projectId:row.id},'/dashboard')}>进入项目</button></>}</>;};
 return <><div className="breadcrumb">管理控制台 / 资源治理</div><div className="page-head"><div><h1>{titles[kind]}</h1><p>{kind==='scopes'?'角色权限与数据范围共同决定访问能力':kind==='audit'?'查看服务端记录的操作与资源变更':'统一维护企业资源与访问边界'}</p></div>{kind==='audit'&&<button className="btn" disabled={exporting||!remote.data?.exportAllowed||!can('audit.read')} onClick={()=>void exportCsv()}>{exporting?'导出中…':'导出 CSV'}</button>}{kind==='users'&&<button className="btn" disabled={!can('user.manage')||!can('system.sso.manage')} onClick={()=>setSsoEdit({})}>＋ SSO 用户</button>}{!['audit','scopes'].includes(kind)&&<button className="btn primary" disabled={!canCreate} title={kind==='organizations'&&!canCreate?'新建组织需要平台级组织写权限':undefined} onClick={()=>open()}>＋ 新建</button>}</div>{kind==='audit'&&<div className="notice" role="status">{remote.data?.exportAllowed===false?'平台已关闭审计导出；日志查询仍可用':exportNote||'CSV 仅包含安全摘要列，最多10000条 / 8MiB；遵循审计权限与数据范围'}</div>}{kind==='users'&&roleNote&&<div className="notice" role="status">{roleNote}</div>}{kind==='users'&&passwordNote&&<div className="notice" role="status">{passwordNote}</div>}<div className="card"><div className="filter-bar"><input aria-label="搜索当前页" placeholder="搜索当前页名称 / 编码" value={query} onChange={e=>setQuery(e.target.value)}/><button className="btn" onClick={()=>{reloadRoleSources();users.reload();ws.refresh();}}>刷新</button></div>{kind==='audit'&&<form className="filter-bar" onSubmit={e=>{e.preventDefault();const f=new FormData(e.currentTarget);setFilter({traceId:String(f.get('traceId')),resourceId:String(f.get('resourceId'))});setPage(1);}}><input name="traceId" placeholder="Trace ID 精确筛选"/><input name="resourceId" placeholder="资源 ID 精确筛选"/><button className="btn">应用筛选</button></form>}{kind==='environments'&&environmentNote&&<p className="notice" role="status">{environmentNote}</p>}<Feedback {...(kind==='scopes'?users:remote)}/>{kind==='users'&&(can('role.manage')?<><Feedback {...roles}/>{!roles.loading&&!roles.error&&roles.data?.length===0&&<p className="notice">角色目录为空，暂无可分配的角色。</p>}</>:<p className="notice">查看角色目录需要角色管理权限。</p>)}<Table rows={tableRows} columns={columns} actions={actions}/><div className="pagination"><span>共 {(kind==='scopes'?users:remote).data?.total??0} 条 · 每页 {(kind==='scopes'?users:remote).data?.pageSize||50} 条</span><div><button disabled={page===1} onClick={()=>setPage(p=>p-1)}>上一页</button><span>{page}</span><button disabled={page*((kind==='scopes'?users:remote).data?.pageSize||50)>=((kind==='scopes'?users:remote).data?.total||0)} onClick={()=>setPage(p=>p+1)}>下一页</button></div></div></div>{runtime&&runtimeRow&&runtimeScope&&mayReadRuntime(runtime.view,runtimeScope)&&<EnvironmentRuntimePanel
   key={JSON.stringify([runtimeAuthority,runtimeRow.id,runtime.view,runtimeRow.desiredConfigVersion])} environment={runtimeRow} scope={runtimeScope} view={runtime.view} close={()=>setRuntime(undefined)}/>}
 {management&&management.authority===managementAuthority&&<ManagementDirectory key={management.entry.kind+management.entry.row.id} entry={management.entry} onClose={()=>setManagement(undefined)} onSaved={()=>{setManagement(undefined);remote.reload();ws.refresh();void refresh();}}/>}{details&&<div className="card details"><button className="text-btn" onClick={()=>setDetails(undefined)}>关闭详情</button><Details value={details}/></div>}{passwordReset&&<PasswordResetDialog user={passwordReset} onClose={()=>setPasswordReset(null)} onSaved={()=>{setPasswordReset(null);setPasswordNote('密码已重置；旧会话已失效，用户下次登录必须改密。');remote.reload();}}/>}{scopeEdit&&<ScopeEditor user={scopeEdit} onClose={()=>setScopeEdit(null)} onSaved={()=>{setScopeEdit(null);users.reload();ws.refresh();void refresh();}}/>}{ssoEdit&&<SsoUserEditor user={ssoEdit.user} onCancel={()=>setSsoEdit(null)} onSaved={()=>{setSsoEdit(null);remote.reload();users.reload();void refresh();}}/>}{environmentEdit&&<EnvironmentAccessEditor environment={environmentEdit} onClose={()=>setEnvironmentEdit(undefined)} onSaved={()=>{setEnvironmentEdit(undefined);setEnvironmentNote('地址已配置，连通性未验证');remote.reload();ws.refresh();}}/>}{currentDraft&&<UserRolesEditor draft={currentDraft} close={()=>setRoleDraft(undefined)} onSaved={()=>{
   setRoleNote('用户角色已保存。');reloadRoleSources();ws.refresh();
 }} onConflict={()=>{
   setRoleNote('用户修订已变更，请重新打开分配角色，核对最新授权后再保存。');reloadRoleSources();
 }}/>}
 {edit&&organizationEditorAllowed&&<Editor {...edit} drawer={!!organizationEditor} allowed={organizationEditorAllowed&&(!creatingLocalUser||canCreate)} help="保存后更新管理资源；角色与数据范围按服务端权限规则生效。" save={async(value,key,fresh)=>{await apiRequest(edit.path,{method:edit.method,body:edit.transform?edit.transform(value):value,etag:(fresh?.revision??edit.revision)?`"${fresh?.revision??edit.revision}"`:undefined,idempotencyKey:key});reloadRoleSources();users.reload();ws.refresh();await refresh();}} loadLatest={edit.method==='PUT'?()=>apiRequest(edit.path):undefined} close={()=>setEdit(undefined)}/>}</>;
}
