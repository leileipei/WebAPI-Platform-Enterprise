import {useEffect,useState} from 'react';
import {ApiAccessPanel} from '../environments/ApiAccessPanel';
import {effectiveAuthentication,routePolicyPayload} from '../policies/advanced-policy-state.mjs';
import {routeDefaultInput} from '../settings/state.mjs';
import {RoutePolicyEditor} from '../policies/RoutePolicyEditor';
import {useWorkspace,allowNavigation} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {apiRequest,ApiError} from '../api/client';
import {Editor,Table,Feedback,useRemote,usePaged,Pagination,type Field} from '../ui';
import type {Api,Version} from '../api/catalog';
import type {Page} from '../api/types';

type RouteTarget={api:Api;version:Version;environmentId:string};
type RoutesProps={target?:RouteTarget;onChanged?:()=>void};

export function Routes({target,onChanged}:RoutesProps){
 const ws=useWorkspace(),{user}=useSession();
 const [apiId,setApi]=useState('');
 const apis=useRemote<Page<Api>>(!target&&ws.projectId?`/projects/${ws.projectId}/apis`:null,0,true);
 const selectedApi=target?.api??(apis.data?.items.find(a=>a.id===apiId)||apis.data?.items[0]);
 const path=ws.environmentId?`/environments/${ws.environmentId}/routes`:null;
 const list=usePaged(path&&target?path+'?'+new URLSearchParams({apiVersionId:target.version.id}):path);
 const identity=JSON.stringify([ws.organizationId,ws.projectId,ws.environmentId,selectedApi?.id,target?.version.id,user?.id,user?.permissions,user?.scopes]);
 return <RouteTable key={identity} api={selectedApi} target={target} list={list} onChanged={onChanged} selector={
   target?<span>配置 API　{target.api.name}</span>:<label>配置 API <select aria-label="路由所属 API" value={selectedApi?.id||''} onChange={e=>{if(allowNavigation())setApi(e.target.value);}}>
    <option value="">请选择 API</option>{apis.data?.items.map(a=><option key={a.id} value={a.id}>{a.name}</option>)}
   </select></label>
 }/>;
}

function RouteTable({api,target,selector,list,onChanged}:RoutesProps&{api?:Api;selector:React.ReactNode;list:ReturnType<typeof usePaged>}){
 const ws=useWorkspace(),{can}=useSession();
 const [edit,setEdit]=useState<any>(),[binding,setBinding]=useState<any>();
 const [lifetime]=useState(()=>new AbortController());
 useEffect(()=>()=>lifetime.abort(),[lifetime]);
 const versions=useRemote<Version[]>(api&&!target?`/apis/${api.id}/versions`:null);
 const choices=target?[target.version]:versions.data||[];
 const clusters=useRemote<Page<any>>(ws.environmentId?`/environments/${ws.environmentId}/clusters`:null,0,true);
 const allowed=!!ws.environmentId&&can('route.write',ws.scope,true)&&(!target||can('api.version.read',{organizationId:target.api.organizationId,projectId:target.api.projectId}));
 const defaults=useRemote<{timeoutMs:number;source:string}>(allowed?`/environments/${ws.environmentId}/route-defaults`:null);
 const matches=(row:any)=>!target||(row.apiVersionId===target.version.id&&row.environmentId===target.environmentId);
 const rows=(list.data?.items||[]).filter(matches).map((row:any)=>({...row,authenticationLabel:effectiveAuthentication(row)}));
 const fields:Field[]=[
  {key:'routeName',label:'路由名称',required:true},
  ...(!target?[{key:'apiVersionId',label:'API 版本',required:true,options:choices.map(v=>({value:v.id,label:v.version+' · '+v.status}))}]:[]),
  {key:'path',label:'请求路径',required:true,help:'支持整段 {id} 与末尾 {*rest}，静态路由优先'},
  {key:'methodsText',label:'HTTP 方法',required:true,help:'多个方法以逗号分隔'},
  {key:'clusterId',label:'后端集群',required:true,options:clusters.data?.items.map(c=>({value:c.id,label:c.name}))||[]},
  {key:'priority',label:'优先级',type:'number',required:true},
  {key:'timeoutMs',label:'基础超时 / 毫秒',type:'number',required:true,help:(edit?.id?'编辑保留原路由基础值':`新路由来源：${defaults.data?.source==='Saved'?'已保存平台默认':'兼容默认'}`)+'；显式值优先，启用的超时策略仍可覆盖'},
  {key:'enabled',label:'启用路由',type:'checkbox'},
  ...(can('policy.write',ws.scope,true)?[{key:'authenticationSelection',label:'认证模式（显式替换）',options:[
    ...(edit?.effectiveAuthenticationMode==='JWT'?[{value:'JWT',label:'JWT · 保留当前绑定'}]:[]),
    {value:'ApiKey',label:'API Key'},{value:'Anonymous',label:'匿名'}
  ],help:'JWT 由认证策略绑定配置。选择其他模式将显式替换认证，须审批发布。'}]:[])
 ];
 function changed(){
  if(lifetime.signal.aborted)return;
  list.reload();
  onChanged?.();
 }
 async function latestRoute(id:string){
  const row=await apiRequest<any>(`/routes/${id}`,{signal:lifetime.signal});
  if(!matches(row))throw Error('路由所属环境或 API 版本已变化，请关闭并刷新列表。');
  return row;
 }
 return <>
  <div className="breadcrumb">API 管理 / 路由配置</div>
  <div className="page-head"><div><h1>路由配置</h1><p>API 版本保持全局定义，路由单独关联当前环境</p></div>
   <button className="btn primary" disabled={!allowed||!defaults.data||!choices.length||!clusters.data?.items.length} onClick={()=>setEdit({
    apiVersionId:choices[0].id,clusterId:clusters.data!.items[0].id,methodsText:'GET',priority:100,
    timeoutMs:routeDefaultInput({timeoutMs:30000,touched:false},defaults.data!),enabled:true,requireApiKey:true,authenticationSelection:'ApiKey'
   })}>＋ 新建路由</button>
  </div>
  <div className="notice">此处修改仅更新工作区。新建 / 编辑路由不会直接切换生产请求，必须通过审批发布。</div>
  <div className="card">
   <div className="filter-bar">{selector}<button className="btn" onClick={list.reload}>刷新</button></div>
   <Feedback {...defaults}/><Feedback {...list}/>
   <Table rows={rows} columns={[['routeName','路由'],['path','路径'],['methods','方法'],['authenticationLabel','有效认证'],['apiVersionId','版本 ID'],['priority','优先级'],['enabled','启用'],['revision','修订']]} actions={row=><>
    <button className="text-btn" disabled={!allowed||!defaults.data||!choices.some(v=>v.id===row.apiVersionId)} title="请先选择该路由所属 API" onClick={()=>setEdit({...row,methodsText:row.methods.join(','),authenticationSelection:effectiveAuthentication(row)})}>编辑</button>
    <button className="text-btn" disabled={!allowed||!can('policy.write',ws.scope,true)} onClick={()=>setBinding(row)}>策略绑定</button>
   </>}/>
   <Pagination remote={list}/>
  </div>
  {api&&<ApiAccessPanel environmentId={ws.environmentId} apiId={api.id} versionId={target?.version.id} view="working"/>}
  {binding&&<RoutePolicyEditor route={binding} verifyTarget={target?async revision=>{
    const latest=await latestRoute(binding.id);
    if(latest.revision!==revision)throw new ApiError(412,'路由修订已变化，请核对最新绑定。');
   }:undefined} signal={lifetime.signal} close={()=>setBinding(undefined)} saved={()=>{setBinding(undefined);changed();}}/>}
  {edit&&<Editor explicitSubmitOnly allowed={allowed} title={edit.id?'编辑路由':'新建路由'} fields={fields} initial={edit}
   close={()=>setEdit(undefined)} loadLatest={edit.id?()=>latestRoute(edit.id):undefined} save={async(value,key,fresh)=>{
    if(!allowed||lifetime.signal.aborted)throw Error('当前范围没有路由写权限');
    const body=routePolicyPayload(value,edit.id?edit:undefined);
    if(target)body.apiVersionId=target.version.id;
    await apiRequest(edit.id?`/routes/${edit.id}`:`/environments/${ws.environmentId}/routes`,{
     method:edit.id?'PUT':'POST',body:{...body,id:edit.id||null},etag:edit.id?`"${fresh?.revision??edit.revision}"`:undefined,idempotencyKey:key,signal:lifetime.signal
    });
    changed();
   }}/>}
 </>;
}
