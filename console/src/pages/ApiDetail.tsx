import {ApiOverview} from './ApiOverview';
import {ApiAccessPanel} from '../environments/ApiAccessPanel';
import {useEffect,useLayoutEffect,useRef,useState} from 'react';import {apiRequest} from '../api/client';import {apiDetailPath,apiInWorkspace,isEditableVersion,type ApiDetail,type Version} from '../api/catalog';import {useWorkspace,Link,allowNavigation} from '../Shell';import {useSession} from '../auth/SessionProvider';import {Editor,Table,Feedback,type Field} from '../ui';import {apiFields} from './ApiCatalog';import {SchemaEditor} from './SchemaEditor';
import {saveCsvBlob as downloadBlob} from '../observability/browser-actions.mjs';
import {ProjectApi,type ProjectApiRemote} from './ProjectApi';
const versionMetadataFields:Field[]=[{key:'version',label:'版本号',required:true},{key:'changeType',label:'变更类型',options:['compatible','breaking','unknown'].map(value=>({value,label:value}))}];
export const versionFields:Field[]=[...versionMetadataFields,{key:'openapiDocument',label:'OpenAPI 文档 JSON',type:'textarea'},{key:'openapiSource',label:'原始文档',type:'textarea'},{key:'sourceFormat',label:'原文格式',options:[{value:'json',label:'JSON'},{value:'yaml',label:'YAML'}]}];
function versionInput(version:Version,readable:boolean){
 return {version:version.version,changeType:version.changeType,openapiDocument:readable?version.openapiDocument:undefined,
  openapiSource:readable?version.openapiSource:undefined,sourceFormat:readable?version.sourceFormat:undefined};
}
type EditTarget = {kind:'api';revision:number}|{kind:'new-version'}|{kind:'version';versionId:string;revision:number;readable:boolean;initial:ReturnType<typeof versionInput>};
type WizardVersion = {
  version?:Version;
  locked:boolean;
  onSelect:(id:string)=>void;
  onSaved:(createdId?:string)=>void;
};

export function ApiDetailPage({id}:{id:string}){
 return <ProjectApi id={id} allowDirectRead>{remote=><ApiDetailContent id={remote.data.api.id} remote={remote}/>}</ProjectApi>;
}

function VersionTimeline({versions}:{versions:Version[]}){
 const events:{key:string;title:string;dateTime:string;time:number}[]=[];
 const undated:string[]=[];
 for(const version of versions){
  const previousCount=events.length;
  for(const [label,dateTime] of [['封存版本',version.sealedAt],['创建版本',version.createdAt]] as const){
   if(!dateTime)continue;
   const time=Date.parse(dateTime);
   if(Number.isFinite(time))events.push({key:version.id+label,title:label+' '+version.version,dateTime,time});
  }
  if(events.length===previousCount)undated.push(version.version);
 }
 events.sort((a,b)=>b.time-a.time);
 return <section aria-label="版本时间线">
  <h3 className="section-title">版本时间线</h3>
  {events.length>0&&<ol className="alert-timeline">{events.map(event=><li key={event.key}>
   <time dateTime={event.dateTime}>{new Date(event.time).toLocaleString('zh-CN',{hour12:false})}</time>
   <strong>{event.title}</strong>
  </li>)}</ol>}
  {undated.length>0&&<p className="muted">版本 {undated.join('、')} 的创建和封存时间未记录。</p>}
  {!versions.length&&<p className="muted">暂无版本记录。</p>}
 </section>;
}

function VersionOpenApi({version}:{version:Version}){
 const active=useRef<AbortController|null>(null);
 const [busy,setBusy]=useState(false),[revalidating,setRevalidating]=useState(false),[error,setError]=useState('');
 useLayoutEffect(()=>{
  function invalidate(){active.current?.abort();setRevalidating(true);}
  window.addEventListener('permission-refresh',invalidate);
  window.addEventListener('session-expired',invalidate);
  return ()=>{
   active.current?.abort();
   window.removeEventListener('permission-refresh',invalidate);
   window.removeEventListener('session-expired',invalidate);
  };
 },[]);
 async function download(representation:'source'|'json'){
  if(active.current||revalidating)return;
  const controller=new AbortController();
  active.current=controller;
  setBusy(true);setError('');
  try{
   const response=await apiRequest<Response>(`/versions/${encodeURIComponent(version.id)}/openapi?representation=${representation}`,{
    responseType:'response',signal:controller.signal,isCurrentRead:()=>!controller.signal.aborted
   });
   const type=response.headers.get('Content-Type')?.split(';')[0].trim().toLowerCase();
   if(type!=='application/json'&&!(representation==='source'&&type==='application/yaml'))throw Error('版本文档返回了意外的格式。');
   const blob=await response.blob();
   if(controller.signal.aborted)return;
   const suffix=representation==='json'?'normalized.json':type==='application/yaml'?'source.yaml':'source.json';
   downloadBlob(blob,`openapi-${version.id}.${suffix}`);
  }catch(cause){
   if(!controller.signal.aborted)setError((cause as Error).message);
  }finally{
   active.current=null;
   if(!controller.signal.aborted)setBusy(false);
  }
 }
 const document=version.openapiSource||version.openapiDocument;
 return <section aria-label="版本 OpenAPI 文档" aria-busy={busy}>
  {document?<><div className="filter-bar">
   {version.openapiSource&&<button className="btn" disabled={busy||revalidating} onClick={()=>void download('source')}>下载版本原文</button>}
   {version.openapiDocument&&<button className="btn" disabled={busy||revalidating} onClick={()=>void download('json')}>下载规范化 JSON</button>}
   {busy&&!revalidating&&<span role="status">正在下载版本文档…</span>}
  </div><pre className="code">{document}</pre></>:<p className="empty">尚未保存 OpenAPI 文档</p>}
  {error&&<p className="error" role="alert">{error}</p>}
 </section>;
}

export function ApiDetailContent({id,remote,wizard}:{id:string;remote:ProjectApiRemote;wizard?:WizardVersion}){
 const ws=useWorkspace(),{can}=useSession();
 const [tab,setTab]=useState('versions'),[selected,setSelected]=useState('');
 const [edit,setEdit]=useState<EditTarget>(),[retiring,setRetiring]=useState<Version>();
 const [lifetime]=useState(()=>new AbortController());
 useEffect(()=>()=>lifetime.abort(),[lifetime]);
 const d=remote.data;
 const v=wizard?wizard.version:(d?.versions.find(version=>version.id===selected)??d?.versions[0]);
 const scope=d?{organizationId:d.api.organizationId,projectId:d.api.projectId}:undefined;
 const detailCurrent=ws.scopeReady&&apiInWorkspace(d.api,id,ws)&&!remote.loading&&!remote.error;
 const detailReadable=detailCurrent||(!!remote.readOnly&&!remote.loading&&!remote.error);
 const mayWriteVersions=!!d?.canWriteVersions&&detailCurrent&&can('api.version.write',scope,true);
 const editedVersion=edit?.kind==='version'?d?.versions.find(row=>row.id===edit.versionId&&row.apiId===id):undefined;
 const mayEditApi=detailCurrent&&can('api.edit',scope,true);
 const editAllowed=edit?.kind==='api'?mayEditApi:mayWriteVersions&&(edit?.kind==='new-version'||isEditableVersion(editedVersion));
 function readsContract(detail?:ApiDetail){
   if(!detail||(!remote.readOnly&&!apiInWorkspace(detail.api,id,ws)))return false;
   const owner={organizationId:detail.api.organizationId,projectId:detail.api.projectId};
   return !!((detail.canReadVersions&&can('api.version.read',owner))||(detail.canReadSchemas&&can('api.schema.read',owner)));
 }
 const readable=readsContract(d);
 const editorCurrent=edit?.kind!=='version'||(edit.readable===readable&&!!editedVersion);
 useEffect(()=>{if(!editorCurrent)setEdit(undefined);},[editorCurrent]);

 const pendingReleaseId=ws.environmentId&&ws.scope&&detailCurrent&&can('release.read',ws.scope)
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
   if(lifetime.signal.aborted)return;
   if(wizard)wizard.onSaved(createdId);
   else remote.reload();
 }

return <><div className="breadcrumb"><Link to="/apis">API 目录</Link> / 详情</div><div className="page-head"><div><h1>{d?.api.name||'API 详情'}</h1><p>{d?.api.code}　工作修订 {d?.workingRevision??'—'}</p></div><div className="actions">{can('metrics.read',ws.scope)&&<Link to={'/observability/apis/'+id}>运行监控</Link>}<button className="btn" disabled={!mayEditApi} onClick={()=>d&&setEdit({kind:'api',revision:d.workingRevision})}>编辑 API</button>{!remote.readOnly&&d&&['api.read','api.version.read','api.schema.read'].every(code=>can(code,scope))&&<Link to={'/apis/'+id+'/versions/compare'}>版本比较与评审</Link>}{!remote.readOnly&&<Link to={'/apis/'+id+'/wizard'}>配置与发布向导 →</Link>}</div></div><Feedback {...remote}/>{d&&<><div className="metric-grid"><div className="card metric"><small>工作修订</small><strong>{d.workingRevision}</strong></div><div className="card metric"><small>当前环境节点一致配置版本</small><strong>{d.runningConfigVersion[ws.environmentId]??'不一致 / 未运行'}</strong></div><div className="card metric"><small>API 版本数量</small><strong>{d.versions.length}</strong></div><div className="card metric"><small>待处理发布</small>{pendingReleaseId?<Link to={'/releases/'+pendingReleaseId}>查看发布</Link>:<strong>—</strong>}</div></div>{detailCurrent&&!wizard&&<ApiOverview key={JSON.stringify([id,ws.organizationId,ws.projectId,ws.environmentId])} apiId={id}/>}<ApiAccessPanel environmentId={ws.environmentId} apiId={id} versionId={v?.id}/><div className="tabs">{[['versions','版本'],['parameters','参数'],['schemas','Schema'],['openapi','OpenAPI']].map(([key,title])=><button key={key} className={tab===key?'active':''} onClick={()=>{if(allowNavigation())setTab(key);}}>{title}</button>)}</div><div className="card">
 {tab==='versions'?<>
   <div className="filter-bar"><button className="btn primary" disabled={!mayWriteVersions} onClick={()=>setEdit({kind:'new-version'})}>＋ 新建版本</button></div>
   <Table rows={d.versions} columns={[['version','版本号'],['status','状态'],['changeType','变更类型'],['revision','修订'],['sealedAt','封存时间']]} actions={row=><>
    <button className="text-btn" disabled={!canSelect(row)} onClick={()=>{if(chooseVersion(row))setTab('parameters');}}>定义</button>
    {detailReadable&&readable&&row.apiId===id&&<button className="text-btn" disabled={!canSelect(row)} onClick={()=>{if(chooseVersion(row))setTab('openapi');}}>查看 OpenAPI</button>}
    {!wizard&&<button className="text-btn" disabled={!mayWriteVersions||['Publishing','Retired'].includes(row.status)} onClick={()=>setRetiring(row)}>废弃版本</button>}
    <button className="text-btn" disabled={!mayWriteVersions||!canSelect(row)||!isEditableVersion(row)}
     onClick={()=>{if(chooseVersion(row))setEdit({kind:'version',versionId:row.id,revision:row.revision,readable,initial:versionInput(row,readable)});}}>编辑</button>
   </>}/>
   <VersionTimeline versions={d.versions}/>
 </>:v?<>
   {!wizard&&<div className="filter-bar"><label>API 版本 <select value={v.id} onChange={e=>{if(allowNavigation())setSelected(e.target.value);}}>
    {d.versions.map(x=><option key={x.id} value={x.id}>{x.version} · {x.status}</option>)}
   </select></label></div>}
   {tab==='openapi'?(detailReadable&&(readable&&v.apiId===id?<VersionOpenApi key={v.id+'-'+v.revision} version={v}/>:<p className="notice">缺少此 API 的契约读取权限。</p>)):
    <SchemaEditor key={v.id+tab} version={v} kind={tab as 'parameters'|'schemas'} detail={d} checking={!detailReadable} readOnly={remote.readOnly} reloadVersion={reloadVersion}/>}
 </>:<div className="empty">请先创建 API 版本</div>}
 </div></>}
 {retiring&&<Editor title={'废弃版本 '+retiring.version} fields={[]} explicitSubmitOnly allowed={mayWriteVersions}
  help="此版本将停止接受新的路由和发布。现有已发布配置仍按原快照运行。" close={()=>setRetiring(undefined)}
  save={async(_,key)=>{await apiRequest('/versions/'+retiring.id+'/retire',{method:'POST',body:{},etag:`"${retiring.revision}"`,idempotencyKey:key,signal:lifetime.signal});reloadVersion();}}/>}
 {edit&&d&&editorCurrent&&<Editor title={edit.kind==='api'?'编辑 API':edit.kind==='new-version'?'新建版本':'编辑版本'} allowed={editAllowed}
  fields={edit.kind==='api'?apiFields:edit.kind==='version'&&!detailCurrent?versionMetadataFields:versionFields}
  initial={edit.kind==='api'?d.api:edit.kind==='version'?edit.initial:{changeType:'compatible',sourceFormat:'json'}}
  close={()=>setEdit(undefined)}
  loadLatest={edit.kind!=='new-version'?async()=>{
   if(!editAllowed||lifetime.signal.aborted)throw new Error('当前工作区无法编辑此 API。');
   const latest=await apiRequest<ApiDetail>(apiDetailPath(id,ws.environmentId),{signal:lifetime.signal});
   remote.reload();
   if(!apiInWorkspace(latest.api,id,ws))throw new Error('API 归属已变化，请刷新后重新打开。');
   if(edit.kind==='api')return {...latest.api,workingRevision:latest.workingRevision};
   const current=latest.versions.find(row=>row.id===edit.versionId&&row.apiId===id);
   if(!current)throw new Error('此版本已不可读取。');
   return {...versionInput(current,readsContract(latest)),revision:current.revision,status:current.status};
  }:undefined}
  save={async(value,key,fresh)=>{
   if(!editAllowed||lifetime.signal.aborted)throw new Error('当前工作区无法编辑此 API。');
   const path=edit.kind==='api'?`/apis/${id}`:edit.kind==='version'?`/versions/${edit.versionId}`:`/apis/${id}/versions`;
   const initial=edit.kind==='version'?edit.initial:undefined;
   const document=value.openapiDocument&&value.openapiDocument!==initial?.openapiDocument?value.openapiDocument:undefined;
   const source=value.openapiSource&&(value.openapiSource!==initial?.openapiSource||value.sourceFormat!==initial?.sourceFormat)?value.openapiSource:undefined;
   const body=edit.kind==='api'?{...value,groupId:value.groupId||null}:
    {version:value.version,changeType:value.changeType,openapiDocument:document,openapiSource:source,sourceFormat:source?value.sourceFormat||undefined:undefined};
   const saved=await apiRequest<Version|undefined>(path,{
    method:edit.kind==='new-version'?'POST':'PUT',
    body,
    etag:edit.kind==='new-version'?undefined:`"${fresh?.revision??edit.revision}"`,idempotencyKey:key,signal:lifetime.signal
   });
   const createdId=edit.kind==='new-version'&&saved?.apiId===id&&saved.status!=='Retired'?saved.id:undefined;
   reloadVersion(createdId);
  }}/>}
 </>;
}
