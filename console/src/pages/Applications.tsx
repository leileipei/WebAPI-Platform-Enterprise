import {useEffect,useState} from 'react';
import {ApplicationOverview} from '../applications/ApplicationOverview';
import {ApplicationMetrics} from '../applications/ApplicationMetrics';
import {ApplicationAudit} from '../applications/ApplicationAudit';
import {useWorkspace,Link,allowNavigation,ScopeRetry} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {apiRequest} from '../api/client';
import type {ApplicationSummary} from '../api/types';
import {Editor,Table,Feedback,useRemote,Details,Pagination,type Field} from '../ui';
import {Credentials} from './Credentials';
import {ApplicationPermissions} from './ApplicationPermissions';

type Application={id:string;organizationId:string;projectId:string|null;code:string;name:string;owner:string;
  status:string;revision:number;summary?:ApplicationSummary};
type ApplicationResponse={items?:Application[];total?:number;pageSize?:number;application?:Application;credentials?:unknown[];runningConfigVersions?:unknown};
type Draft=Pick<Application,'organizationId'|'projectId'|'status'> & Partial<Application> & {ownership?:string};
const fields:Field[]=[{key:'code',label:'应用编码',required:true},{key:'name',label:'应用名称',required:true},
  {key:'owner',label:'负责人',required:true},{key:'status',label:'状态',options:['Active','Disabled'].map(value=>({value,label:value}))}];
const tabs=[['overview','Overview'],['credentials','凭证'],['permissions','API 授权'],['runtime','运行版本'],['metrics','Metrics'],['audit','Audit']];

export function Applications({id}:{id?:string}) {
  const ws=useWorkspace(),{can}=useSession();
  const [paging,setPaging]=useState({org:ws.organizationId,page:1}),[edit,setEdit]=useState<Draft>(),[tab,setTab]=useState('credentials');
  const page=paging.org===ws.organizationId?paging.page:1;
  const setPage=(value:number)=>setPaging({org:ws.organizationId,page:value});
  const path=id?'/applications/'+id:'/applications?'+new URLSearchParams({organizationId:ws.organizationId||'',page:String(page),pageSize:'50'});
  const remote=useRemote<ApplicationResponse>(ws.organizationId?path:null);
  const record=remote.data?.application;
  const requested=id && record?.id?.toLowerCase()===id.toLowerCase()?record:undefined;
  const app=requested && requested.organizationId===ws.organizationId?requested:undefined;
  const owner=requested && ws.tree.organizations.find(org=>org.id===requested.organizationId);
  const rows=(remote.data?.items??[]).filter(row=>row.organizationId===ws.organizationId);
  const orgScope={organizationId:ws.organizationId};
  const writableProjects=ws.tree.projects.filter(project=>project.organizationId===ws.organizationId
    && can('app.write',{...orgScope,projectId:project.id},true));
  const shared=can('app.write',orgScope,true);
  const selectedProject=writableProjects.find(p=>p.id===ws.projectId)??writableProjects[0];
  const ownership:Field={key:'ownership',label:'应用归属',required:true,options:[
    ...(shared?[{value:'organization',label:'组织共享应用'}]:[]),
    ...writableProjects.map(p=>({value:p.id,label:p.name}))
  ]};
  const allowed=ws.scopeReady && !!ws.organizationId && (id?!!app&&can('app.write',
    {organizationId:app.organizationId,projectId:app.projectId},true):shared||writableProjects.length>0);
  const editorAllowed=!!edit&&allowed&&edit.organizationId===ws.organizationId&&(edit.id
    ?edit.id===app?.id&&(edit.projectId??null)===(app?.projectId??null):!id);
  const validOwner=(value:string)=>value==='organization'?shared:writableProjects.some(p=>p.id===value);
  useEffect(()=>{if(!id&&remote.data?.total!==undefined&&page>Math.max(1,Math.ceil(remote.data.total/50)))setPage(1);},[id,page,remote.data]);
  return <>
    <div className="breadcrumb"><Link to="/applications">应用接入</Link>{id&&' / 应用详情'}</div>
    <div className="page-head"><div><h1>{id?app?.name||'应用详情':'应用管理'}</h1>
      <p>管理消费方身份、凭证窗口与 API 授权</p></div>
      <button className="btn primary" disabled={!allowed} onClick={()=>setEdit(app??{
        organizationId:ws.organizationId,projectId:selectedProject?.id||null,ownership:selectedProject?.id??'organization',status:'Active'
      })}>{id?'编辑应用':'＋ 新建应用'}</button>
    </div>
    <div className="notice">应用、凭证及授权保存为工作配置。网关认证采用已发布 Snapshot 的应用状态、有效期与授权集合。</div>
    <Feedback {...remote}/>
    {id && remote.data && !app && ws.scopeReady && <div className="notice" role="status">
      {owner && requested && can('app.read',{organizationId:owner.id,projectId:requested.projectId})
        ? <>此应用属于{owner.name}。 <button className="text-btn" onClick={()=>ws.selectScope({organizationId:owner.id})}>切换到{owner.name}查看应用</button></>
        : <>当前组织无法核实此应用的归属。 <Link to="/applications">返回当前组织的应用列表</Link></>}
    </div>}
    {app && <>
      <div className="tabs">{tabs.map(([key,title])=><button key={key} className={tab===key?'active':''}
        onClick={()=>{if(allowNavigation())setTab(key);}}>{title}</button>)}</div>
      <div className="card">{tab==='overview'?<ApplicationOverview application={app}/>:
        tab==='metrics'?<ApplicationMetrics application={app}/>:tab==='audit'?<ApplicationAudit application={app}/>:
        tab==='credentials'?<Credentials application={app} rows={remote.data?.credentials??[]} reload={remote.reload}/>:
        tab==='permissions'?<ApplicationPermissions key={app.id} application={app} reload={remote.reload}/>:
        <Details value={remote.data?.runningConfigVersions}/>}</div>
    </>}
    {!id && <div className="card">
      <Table rows={rows.map(r=>({...r,apiCount:r.summary?.visibleActiveApiCount??'—',
        credentialCount:r.summary?`${r.summary.availableCredentials} / ${r.summary.totalCredentials}`:'—'}))}
        columns={[['name','应用名称'],['code','编码'],['owner','负责人'],['status','状态'],['apiCount','有效授权 API'],['credentialCount','可用 / 总凭证'],['revision','修订']]}
        actions={r=><Link to={'/applications/'+r.id}>详情与接入</Link>}/>
      <Pagination remote={{...remote,page,setPage,data:remote.data}}/>
    </div>}
    {edit && <Editor title={edit.id?'编辑应用':'新建应用'} fields={edit.id?fields:[ownership,...fields]} maySave={v=>!!edit.id||validOwner(v.ownership)} initial={edit} allowed={editorAllowed}
      help={ws.scopeReady?undefined:<ScopeRetry/>}
      close={()=>setEdit(undefined)} loadLatest={edit.id?async()=>{
        const latest=await apiRequest<ApplicationResponse>('/applications/'+edit.id);
        if(latest.application?.id!==edit.id || latest.application?.organizationId!==edit.organizationId ||
           (latest.application?.projectId??null)!==(edit.projectId??null))throw Error('无法核实该修订所属的应用。');
        return latest;
      }:undefined} save={async(value,key,fresh)=>{
        if(!editorAllowed||!edit.id&&!validOwner(value.ownership))throw Error('当前组织无法编辑此应用。');
        await apiRequest(edit.id?'/applications/'+edit.id:'/applications',{
          method:edit.id?'PUT':'POST',body:{code:value.code,name:value.name,owner:value.owner,status:value.status,organizationId:edit.organizationId,projectId:edit.id?edit.projectId??null:value.ownership==='organization'?null:value.ownership},
          etag:edit.id?`"${fresh?.revision??edit.revision}"`:undefined,idempotencyKey:key
        });
        remote.reload();
      }}/>}
  </>;
}
