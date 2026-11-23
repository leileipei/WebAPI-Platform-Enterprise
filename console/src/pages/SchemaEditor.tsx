import {useEffect,useState} from 'react';import {apiRequest} from '../api/client';import {Table,useRemote,Feedback} from '../ui';import {SchemaWorkbench} from '../contracts/SchemaWorkbench';
import {apiDetailPath,apiInWorkspace,isEditableVersion,type ApiDetail,type Version} from '../api/catalog';
import {useSession} from '../auth/SessionProvider';
import {useWorkspace} from '../Shell';
export function SchemaEditor({version,kind,detail,checking,reloadVersion}:{version:Version;kind:'parameters'|'schemas';detail:ApiDetail;checking:boolean;reloadVersion:()=>void}){
 const ws=useWorkspace(),{can}=useSession(),scope={organizationId:detail.api.organizationId,projectId:detail.api.projectId};
 const readable=apiInWorkspace(detail.api,version.apiId,ws)&&detail.canReadSchemas&&can('api.schema.read',scope);
 const [lifetime]=useState(()=>new AbortController());
 useEffect(()=>()=>lifetime.abort(),[lifetime]);
 const path=`/versions/${version.id}/${kind}`;
 const remote=useRemote<any[]>(readable?path:null,0,false,true,version.revision),[edit,setEdit]=useState<any>(),[error,setError]=useState(''),[tab,setTab]=useState('all');
 const rows=remote.data??[],readonly=!detail.canWriteSchemas||!can('api.schema.write',scope,true)||!isEditableVersion(version);
 const blocked=!ws.scopeReady||checking||remote.loading||!!remote.error||!remote.data;
 const denied=remote.errorStatus===403||remote.errorStatus===404;
 useEffect(()=>{setTab('all');setEdit(undefined);setError('');},[version.id,kind,readable,denied]);
 if(!readable||denied)return <p className="notice">缺少此 API 的参数和 Schema 读取权限。</p>;
 const group=(row:any)=>`${row.schemaType} · ${row.statusCode??'default'} · ${row.contentType}`;
 const tabs=kind==='schemas'?[...new Set(rows.map(group))]:[],visible=tab==='all'?rows:rows.filter(row=>group(row)===tab);
 async function saveRows(next:any[],key:string=crypto.randomUUID(),revision=version.revision){
  if(!readable||readonly||blocked||lifetime.signal.aborted)throw new Error('资源、权限或读取状态已变化，请刷新后继续。');
  const body=next.map(row=>kind==='parameters'?{id:row.id??null,location:row.location,name:row.name,dataType:row.dataType,required:!!row.required,schema:row.schema??null,description:row.description??null,exampleJson:row.exampleJson??null}:{id:row.id??null,schemaType:row.schemaType,name:row.name,statusCode:row.statusCode??null,contentType:row.contentType,schemaJson:row.schemaJson,exampleJson:row.exampleJson??null});
  await apiRequest(path,{method:'PUT',body,etag:`"${revision}"`,idempotencyKey:key,signal:lifetime.signal});
  if(!lifetime.signal.aborted){remote.reload();reloadVersion();}
 }
 async function loadLatest(){
  if(!readable||blocked||lifetime.signal.aborted)throw new Error('当前工作区无法读取此定义。');
  const latest=await apiRequest<ApiDetail>(apiDetailPath(version.apiId),{signal:lifetime.signal});
  if(!apiInWorkspace(latest.api,version.apiId,ws)||!latest.canReadSchemas)throw new Error('当前工作区无法核实此定义的归属或读取权限。');
  const current=latest.versions.find(v=>v.apiId===version.apiId&&v.id===version.id);
  if(!current)throw new Error('此版本或定义已不可读取。');
  return {...current,canWriteSchemas:latest.canWriteSchemas,items:await apiRequest<any[]>(path,{signal:lifetime.signal})};
 }
 return <><div className="filter-bar"><span>{kind==='parameters'?'参数定义':'请求 / 响应 / 组件 Schema'}</span><button type="button" className="btn primary" disabled={readonly||blocked} onClick={()=>setEdit({kind,...(kind==='parameters'?{location:'query',dataType:'string',required:false,schema:'{"type":"string"}'}:{schemaType:'response',contentType:'application/json',schemaJson:'{"type":"object","properties":{}}'})})}>＋ 新增</button></div>{!isEditableVersion(version)&&<div className="notice">此版本仅供查看和临时验证，请创建新版本后修改。</div>}<Feedback {...remote}/>{error&&<div className="error" role="alert">{error}</div>}{tabs.length>0&&<div className="schema-tabs" role="group" aria-label="按用途、响应状态码与媒体类型查看"><button type="button" className="btn" aria-pressed={tab==='all'} onClick={()=>setTab('all')}>全部</button>{tabs.map(value=><button type="button" className="btn" aria-pressed={tab===value} key={value} onClick={()=>setTab(value)}>{value}</button>)}</div>}<Table rows={visible} columns={kind==='parameters'?[['location','位置'],['name','名称'],['dataType','类型'],['required','必填'],['description','说明']]:[['schemaType','用途'],['name','名称'],['statusCode','状态码'],['contentType','内容类型'],['schemaHash','Schema 摘要']]} actions={row=><><button type="button" className="text-btn" disabled={blocked} onClick={()=>setEdit({...row,kind})}>{readonly?'查看 / 验证':'结构 / 原文 / 验证'}</button><button type="button" className="text-btn" disabled={readonly||blocked} onClick={async()=>{try{await saveRows(rows.filter(x=>x.id!==row.id));}catch(e){setError((e as Error).message);}}}>移除工作定义</button></>}/>{edit&&<SchemaWorkbench version={version} definition={edit} readOnly={readonly} unavailable={blocked} close={()=>setEdit(undefined)} loadLatest={loadLatest} save={async(value,key,fresh)=>{
  const collection=[...(fresh?.items??rows)];if(value.id){const index=collection.findIndex(row=>row.id===value.id);if(index<0)throw new Error('定义已被移除，草稿已保留。请重新读取最新版本。');collection[index]=value;}else collection.push(value);await saveRows(collection,key,fresh?.revision??version.revision);
 }}/>}</>;
}
