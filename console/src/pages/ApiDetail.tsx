import {ApiAccessPanel} from '../environments/ApiAccessPanel';
import {useState} from 'react';import {apiRequest} from '../api/client';import {apiDetailPath,isEditableVersion,type ApiDetail,type Version} from '../api/catalog';import {useWorkspace,Link,allowNavigation} from '../Shell';import {useSession} from '../auth/SessionProvider';import {Editor,Table,Feedback,useRemote,Details,type Field} from '../ui';import {apiFields} from './ApiCatalog';import {SchemaEditor} from './SchemaEditor';
export const versionFields:Field[]=[{key:'version',label:'版本号',required:true},{key:'changeType',label:'变更类型',options:['compatible','breaking'].map(value=>({value,label:value}))},{key:'openapiDocument',label:'OpenAPI 文档 JSON',type:'textarea'},{key:'openapiSource',label:'原始文档',type:'textarea'},{key:'sourceFormat',label:'原文格式',options:[{value:'json',label:'JSON'}]}];
type EditTarget = {kind:'api';revision:number}|{kind:'new-version'}|{kind:'version';versionId:string;revision:number};
type WizardVersion = {
  remote:ReturnType<typeof useRemote<ApiDetail>>;
  version?:Version;
  locked:boolean;
  onSelect:(id:string)=>void;
  onSaved:(createdId?:string)=>void;
};

export function ApiDetailPage({id,wizard}:{id:string;wizard?:WizardVersion}){
 const ws=useWorkspace(),{can}=useSession();
 const standalone=useRemote<ApiDetail>(wizard?null:apiDetailPath(id,ws.environmentId),0,false,true);
 const remote=wizard?.remote??standalone;
 const [tab,setTab]=useState('versions'),[selected,setSelected]=useState('');
 const [edit,setEdit]=useState<EditTarget>();
 const d=remote.data;
 const v=wizard?wizard.version:(d?.versions.find(version=>version.id===selected)??d?.versions[0]);
 const scope=d?{organizationId:d.api.organizationId,projectId:d.api.projectId}:undefined;
 const detailCurrent=d?.api.id===id&&!remote.loading&&!remote.error;
 const mayWriteVersions=!!d?.canWriteVersions&&detailCurrent&&can('api.version.write',scope,true);
 const editedVersion=edit?.kind==='version'?d?.versions.find(row=>row.id===edit.versionId&&row.apiId===id):undefined;
 const mayEditApi=detailCurrent&&can('api.edit',scope,true);
 const editAllowed=edit?.kind==='api'?mayEditApi:mayWriteVersions&&(edit?.kind==='new-version'||isEditableVersion(editedVersion));

 const pendingReleaseId=ws.environmentId&&ws.scope&&d?.api.id===id&&d.api.projectId===ws.projectId&&
   d.api.organizationId===ws.organizationId&&!remote.loading&&!remote.error&&can('release.read',ws.scope)
   ?d.pendingReleaseId:null;

 function canSelect(version:Version){
   return !wizard||(version.apiId===id&&version.status!=='Retired'&&(!wizard.locked||wizard.version?.id===version.id));
 }
 function chooseVersion(version:Version){
   if(!canSelect(version)||!allowNavigation())return false;
   if(wizard){
     if(wizard.version?.id!==version.id)wizard.onSelect(version.id);
   }else setSelected(version.id);
   return true;
 }
 function reloadVersion(createdId?:string){
   if(wizard)wizard.onSaved(createdId);
   else remote.reload();
 }

return <><div className="breadcrumb"><Link to="/apis">API 目录</Link> / 详情</div><div className="page-head"><div><h1>{d?.api.name||'API 详情'}</h1><p>{d?.api.code}　工作修订 {d?.workingRevision??'—'}</p></div><div className="actions">{can('metrics.read',ws.scope)&&<Link to={'/observability/apis/'+id}>运行监控</Link>}<button className="btn" disabled={!mayEditApi} onClick={()=>d&&setEdit({kind:'api',revision:d.workingRevision})}>编辑 API</button>{d&&['api.read','api.version.read','api.schema.read'].every(code=>can(code,scope))&&<Link to={'/apis/'+id+'/versions/compare'}>版本比较与评审</Link>}<Link to={'/apis/'+id+'/wizard'}>配置与发布向导 →</Link></div></div><Feedback {...remote}/>{d&&<><div className="metric-grid"><div className="card metric"><small>工作修订</small><strong>{d.workingRevision}</strong></div><div className="card metric"><small>当前环境节点一致配置版本</small><strong>{d.runningConfigVersion[ws.environmentId]??'不一致 / 未运行'}</strong></div><div className="card metric"><small>API 版本数量</small><strong>{d.versions.length}</strong></div><div className="card metric"><small>待处理发布</small>{pendingReleaseId?<Link to={'/releases/'+pendingReleaseId}>查看发布</Link>:<strong>—</strong>}</div></div><ApiAccessPanel environmentId={ws.environmentId} apiId={id} versionId={v?.id}/><div className="tabs">{[['versions','版本'],['parameters','参数'],['schemas','Schema'],['openapi','OpenAPI']].map(([key,title])=><button key={key} className={tab===key?'active':''} onClick={()=>{if(allowNavigation())setTab(key);}}>{title}</button>)}</div><div className="card">
 {tab==='versions'?<>
   <div className="filter-bar"><button className="btn primary" disabled={!mayWriteVersions} onClick={()=>setEdit({kind:'new-version'})}>＋ 新建版本</button></div>
   <Table rows={d.versions} columns={[['version','版本号'],['status','状态'],['changeType','变更类型'],['revision','修订'],['sealedAt','封存时间']]} actions={row=><>
    <button className="text-btn" disabled={!canSelect(row)} onClick={()=>{if(chooseVersion(row))setTab('parameters');}}>定义</button>
    <button className="text-btn" disabled={!mayWriteVersions||!canSelect(row)||!isEditableVersion(row)}
     onClick={()=>{if(chooseVersion(row))setEdit({kind:'version',versionId:row.id,revision:row.revision});}}>编辑</button>
   </>}/>
 </>:v?<>
   {!wizard&&<div className="filter-bar"><label>API 版本 <select value={v.id} onChange={e=>{if(allowNavigation())setSelected(e.target.value);}}>
    {d.versions.map(x=><option key={x.id} value={x.id}>{x.version} · {x.status}</option>)}
   </select></label></div>}
   {tab==='openapi'?<Details value={v.openapiSource||v.openapiDocument||'尚未保存 OpenAPI 文档'}/>:
    <SchemaEditor key={v.id+tab} version={v} kind={tab as 'parameters'|'schemas'} detail={d} checking={!detailCurrent} reloadVersion={reloadVersion}/>}
 </>:<div className="empty">请先创建 API 版本</div>}
 </div></>}
 {edit&&d&&<Editor title={edit.kind==='api'?'编辑 API':edit.kind==='new-version'?'新建版本':'编辑版本'} allowed={editAllowed}
  fields={edit.kind==='api'?apiFields:versionFields}
  initial={edit.kind==='api'?d.api:editedVersion??{changeType:'compatible',sourceFormat:'json'}}
  close={()=>setEdit(undefined)}
  loadLatest={edit.kind==='api'?()=>apiRequest(`/apis/${id}`):editedVersion?async()=>{
   const latest=await apiRequest<Version>(`/versions/${editedVersion.id}`);
   remote.reload();
   return latest;
  }:undefined}
  save={async(value,key,fresh)=>{
   const path=edit.kind==='api'?`/apis/${id}`:edit.kind==='version'?`/versions/${edit.versionId}`:`/apis/${id}/versions`;
   const saved=await apiRequest<Version|undefined>(path,{
    method:edit.kind==='new-version'?'POST':'PUT',
    body:{...value,groupId:value.groupId||null,openapiDocument:value.openapiDocument||null,openapiSource:value.openapiSource||null,sourceFormat:value.sourceFormat||null},
    etag:edit.kind==='new-version'?undefined:`"${fresh?.revision??edit.revision}"`,idempotencyKey:key
   });
   const createdId=edit.kind==='new-version'&&saved?.apiId===id&&saved.status!=='Retired'?saved.id:undefined;
   reloadVersion(createdId);
  }}/>}
 </>;
}
