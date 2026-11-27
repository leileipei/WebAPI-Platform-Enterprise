import {useEffect,useState} from 'react';
import {useWorkspace} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {apiRequest} from '../api/client';
import {Editor, useRemote, type Field} from '../ui';
import type {Page} from '../api/types';

type Source={id:string;routeName:string;path:string;methods:string[];revision:number;environmentId:string};
type Cluster={id:string;name:string;status:string;environmentId:string;projectId:string};
export function RouteCopy({source,close,done}:{source:Source;close:()=>void;done:()=>void}){
 const ws=useWorkspace(),{can}=useSession();
 const choices=ws.tree.environments.filter(e=>e.id!==source.environmentId&&e.projectId===ws.projectId&&
   can('route.write',{organizationId:ws.organizationId,projectId:e.projectId,environmentId:e.id},true)&&
   can('cluster.read',{organizationId:ws.organizationId,projectId:e.projectId,environmentId:e.id}));
 const [target,setTarget]=useState(choices[0]?.id??'');
 const [lifetime]=useState(()=>new AbortController());
 useEffect(()=>()=>lifetime.abort(),[lifetime]);
 const clusters=useRemote<Page<Cluster>>(choices.some(e=>e.id===target)?`/environments/${target}/clusters`:null,0,true,true);
 const available=clusters.loading||clusters.error?[]:clusters.data?.items.filter(c=>c.environmentId===target&&c.projectId===ws.projectId&&c.status==='Active')??[];
 const fields:Field[]=[{key:'environmentId',label:'目标环境',required:true},{key:'clusterId',label:'目标集群',required:true}];
 return <Editor title={'复制到环境 · '+source.routeName} fields={fields} initial={{environmentId:target,clusterId:''}} explicitSubmitOnly
  close={()=>{lifetime.abort();close();}} allowed={can('route.read',ws.scope)&&source.environmentId===ws.environmentId&&choices.some(e=>e.id===target)}
  help={'将 '+source.methods.join('、')+' '+source.path+' 及策略绑定复制为目标环境的工作路由，目标环境需单独发布。'}
  renderFields={(value,change)=><><label>目标环境<select aria-label="目标环境" required value={target} onChange={e=>{setTarget(e.target.value);change('environmentId',e.target.value);change('clusterId','');}}>
    {choices.map(e=><option key={e.id} value={e.id}>{e.name}</option>)}</select></label>
   <label>目标集群<select aria-label="目标集群" required value={value.clusterId||''} onChange={e=>change('clusterId',e.target.value)}><option value="">选择已启用的目标集群</option>
    {available.map(c=><option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
   {clusters.error&&<p role="alert">{clusters.error}</p>}{!clusters.loading&&!clusters.error&&!available.length&&<p>目标环境没有可选的启用集群。</p>}</>}
  maySave={value=>available.some(c=>c.id===value.clusterId)&&value.environmentId===target}
  save={async(value,key)=>{await apiRequest('/routes/'+source.id+'/copy',{method:'POST',body:{environmentId:target,clusterId:value.clusterId},etag:`"${source.revision}"`,idempotencyKey:key,signal:lifetime.signal});if(!lifetime.signal.aborted)done();}}/>;
}
