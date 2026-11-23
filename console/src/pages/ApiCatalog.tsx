import {useEffect,useRef,useState} from 'react';
import {useWorkspace,Link,navigate} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {apiRequest} from '../api/client';
import {useRemote,usePaged,Pagination,Table,Editor,Feedback,type Field} from '../ui';
import type {Api} from '../api/catalog';

export const apiFields:Field[]=[{key:'code',label:'API 编码',required:true},{key:'name',label:'API 名称',required:true},{key:'description',label:'说明',type:'textarea'},{key:'groupId',label:'分组 ID',help:'可留空；分组在目录中管理'},{key:'ownerUserId',label:'负责人用户 ID',help:'默认当前用户'},{key:'lifecycleStatus',label:'生命周期',options:['Draft','Active','Deprecated','Retired'].map(value=>({value,label:value}))}];
export const healthNames:Record<string,string>={Healthy:'健康',Unhealthy:'异常',Unknown:'未知',Disabled:'未启用探测',NotRunning:'未运行'};
type Entry=Api&{methods?:string[];versions?:string[];ownerName?:string;tags?:string[];health?:string};
const emptyFilters={q:'',method:'',version:'',owner:'',tag:'',health:'',lifecycle:''};
export function ApiCatalog(){
 const ws=useWorkspace(),{user}=useSession();
 return <Directory key={JSON.stringify([ws.organizationId,ws.projectId,ws.environmentId,user?.id,user?.permissions,user?.scopes])}/>;
}
function Directory(){
 const ws=useWorkspace(),{can}=useSession(),[edit,setEdit]=useState(false),[groupEdit,setGroupEdit]=useState<any>();
 const scope={organizationId:ws.organizationId,projectId:ws.projectId};
 const [filters,setFilters]=useState(emptyFilters),[selected,setSelected]=useState<string[]>([]),[note,setNote]=useState('');
 const [exporting,setExporting]=useState(false), active=useRef<AbortController|undefined>(undefined);
 useEffect(()=>()=>active.current?.abort(),[]);
 const project=ws.scopeReady&&ws.projectId&&can('api.read',scope)?ws.projectId:null;
 const canRoute=!!ws.environmentId&&can('route.read',ws.scope),canTags=can('api.schema.read',scope)||can('api.version.read',scope);
 const canHealth=canRoute&&can('metrics.read',ws.scope)&&can('cluster.read',ws.scope);
 const params=new URLSearchParams({directory:'true',...(ws.environmentId&&can('api.read',ws.scope)?{environmentId:ws.environmentId}:{})});
 for(const [key,value] of Object.entries(filters))if(value.trim())params.set(key,value.trim());
 const path=project?`/projects/${project}/apis`:null;
 const remote=usePaged(path?path+'?'+params:null);
 const groups=useRemote<any[]>(project?`/projects/${project}/api-groups`:null);
 const rows:Entry[]=remote.loading||remote.error?[]:(remote.data?.items??[]);
 function setFilter(key:keyof typeof filters,value:string){
   active.current?.abort();setExporting(false);setFilters(previous=>({...previous,[key]:value}));setSelected([]);setNote('');
 }
 function choose(ids:string[],checked:boolean){
   setSelected(current=>checked?[...new Set([...current,...ids])]:current.filter(id=>!ids.includes(id)));setNote('');
 }
 async function download(){
   if(!path||!selected.length||exporting)return;
   const request=new AbortController();active.current=request;setExporting(true);setNote('');
   try {
     const exported:Entry[]=[];
     for(let start=0;start<selected.length;start+=1000){
       const batch=await apiRequest<Entry[]>(path+'/export?'+params,{method:'POST',body:{ids:selected.slice(start,start+1000)},signal:request.signal});
       exported.push(...batch);
     }
     if(request.signal.aborted)return;
     const url=URL.createObjectURL(new Blob([JSON.stringify(exported,null,2)],{type:'application/json'}));
     const link=document.createElement('a');link.href=url;link.download='api-directory.json';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
     setNote('已导出 '+exported.length+' 条 API 目录记录。');
   }catch(error){if(!request.signal.aborted)setNote((error as Error).message);}
   finally {if(!request.signal.aborted)setExporting(false);}
 }
 const pageIds=rows.map(row=>row.id);
 return <><div className="breadcrumb">API 管理 / API 目录</div><div className="page-head"><div><h1>API 目录</h1><p>管理项目中的 API 定义与版本，查看所选环境的路由方法与后端健康。</p></div>
  <div className="actions">{ws.environmentId&&can('api.create',scope,true)&&can('route.write',ws.scope,true)&&<Link to="/imports">导入 OpenAPI</Link>}
   <button className="btn" disabled={!project||!can('api.edit',scope,true)} onClick={()=>setGroupEdit({})}>管理分组</button>
   <button className="btn primary" disabled={!project||!can('api.create',scope,true)} onClick={()=>setEdit(true)}>＋ 新建 API</button></div></div>
  <div className="card"><div className="filter-bar">
   <input aria-label="搜索 API" placeholder="搜索 API 名称 / 编码" value={filters.q} onChange={e=>setFilter('q',e.target.value)}/>
   <label>方法<select aria-label="方法" disabled={!canRoute} value={filters.method} onChange={e=>setFilter('method',e.target.value)}><option value="">全部</option>{['GET','POST','PUT','PATCH','DELETE','HEAD','OPTIONS'].map(m=><option key={m}>{m}</option>)}</select></label>
   <label>版本<input value={filters.version} onChange={e=>setFilter('version',e.target.value)}/></label>
   <label>生命周期<select value={filters.lifecycle} onChange={e=>setFilter('lifecycle',e.target.value)}><option value="">全部</option>{['Draft','Active','Deprecated','Retired'].map(s=><option key={s}>{s}</option>)}</select></label>
   <label>后端健康<select aria-label="后端健康" disabled={!canHealth} value={filters.health} onChange={e=>setFilter('health',e.target.value)}><option value="">全部</option>{Object.entries(healthNames).map(([value,label])=><option key={value} value={value}>{label}</option>)}</select></label>
   <label>负责人<input value={filters.owner} onChange={e=>setFilter('owner',e.target.value)} placeholder="姓名、账号或用户 ID"/></label>
   <label>OpenAPI 标签<input disabled={!canTags} value={filters.tag} onChange={e=>setFilter('tag',e.target.value)}/></label>
   <button className="btn" onClick={remote.reload}>刷新</button></div>
   <div className="actions"><label><input type="checkbox" disabled={!rows.length||exporting} checked={!!rows.length&&pageIds.every(id=>selected.includes(id))} onChange={e=>choose(pageIds,e.target.checked)}/>选择本页</label>
   <button className="btn" disabled={!selected.length||exporting} onClick={()=>void download()}>{exporting?'导出中…':'批量导出'}（{selected.length}）</button></div>
   {note&&<p role="status">{note}</p>}<Feedback {...remote}/>
   <div className="table-wrap"><table><thead><tr>{['选择','API 名称','编码','工作方法','版本','生命周期','后端健康','负责人','标签','操作'].map(h=><th key={h}>{h}</th>)}</tr></thead>
   <tbody>{rows.map(row=><tr key={row.id}><td><input type="checkbox" aria-label={'选择 '+row.code} disabled={exporting} checked={selected.includes(row.id)} onChange={e=>choose([row.id],e.target.checked)}/></td>
    <td>{row.name}</td><td>{row.code}</td><td>{row.methods?.map(m=><span className="badge" key={m}>{m}</span>)??'—'}</td><td>{row.versions?.join('、')||'—'}</td><td>{row.lifecycleStatus}</td>
    <td>{row.health?healthNames[row.health]:'—'}</td><td>{row.ownerName||row.ownerUserId}</td><td>{row.tags?.join('、')||'—'}</td>
    <td className="row-actions"><Link to={'/apis/'+row.id}>查看详情</Link><Link to={'/apis/'+row.id+'/wizard'}>配置与发布</Link></td></tr>)}</tbody></table>
    {!remote.loading&&!rows.length&&<div className="empty">暂无记录</div>}</div><Pagination remote={remote}/></div>
  {groups.data&&<div className="card"><h3 className="section-title">API 分组</h3><Table rows={groups.data} columns={[['name','名称'],['parentId','父分组 ID'],['sortOrder','排序']]} actions={r=><button disabled={!can('api.edit',scope,true)} className="text-btn" onClick={()=>setGroupEdit(r)}>编辑</button>}/></div>}
  {edit&&<Editor title="新建 API" fields={apiFields} initial={{lifecycleStatus:'Draft'}} close={()=>setEdit(false)} save={async(v,key)=>{const r=await apiRequest<Api>(`/projects/${project}/apis`,{method:'POST',body:{...v,groupId:v.groupId||null,ownerUserId:v.ownerUserId||null},idempotencyKey:key});navigate('/apis/'+r.id,true);}}/>}
  {groupEdit&&<Editor title={groupEdit.id?'编辑分组':'新建分组'} fields={[{key:'name',label:'名称',required:true},{key:'parentId',label:'父分组',options:(groups.data||[]).filter(g=>g.id!==groupEdit.id).map(g=>({value:g.id,label:g.name}))},{key:'sortOrder',label:'排序',type:'number'}]} initial={groupEdit} close={()=>setGroupEdit(undefined)} save={async(v,key)=>{await apiRequest(groupEdit.id?`/api-groups/${groupEdit.id}`:`/projects/${project}/api-groups`,{method:groupEdit.id?'PUT':'POST',body:{...v,parentId:v.parentId||null},etag:groupEdit.id?`"${groupEdit.revision}"`:undefined,idempotencyKey:key});groups.reload();}}/>}</>;
}
