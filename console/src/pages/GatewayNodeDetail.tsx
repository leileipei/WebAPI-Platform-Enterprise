import {useEffect,useState} from 'react';
import {Link,useWorkspace} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {apiRequest} from '../api/client';
import {Details,Editor,Feedback,Pagination,Table,usePaged,useRemote,type Field} from '../ui';

type GatewayNode = {id:string;environmentId:string;nodeName:string;enabled:boolean;eTag:string};
const fields:Field[]=[{key:'enabled',label:'启用节点身份',type:'checkbox',
  help:'停用后，该身份无法再次注册、发送心跳或确认配置。停止运行中的代理进程需通过运维操作。'}];

function hasIdentityRevision(node?:GatewayNode) {
  return typeof node?.enabled==='boolean' && typeof node.eTag==='string' && node.eTag.trim().length>0;
}

export function GatewayNodeDetail({id}:{id:string}) {
  const ws=useWorkspace(),{can}=useSession();
  const remote=useRemote<GatewayNode>('/gateway-nodes/'+id,5000,false,true);
  const requested=typeof remote.data?.id==='string' && remote.data.id.toLowerCase()===id.toLowerCase() ? remote.data : undefined;
  const environment=ws.tree.environments.find(env=>env.id===requested?.environmentId);
  const project=environment && ws.tree.projects.find(item=>item.id===environment.projectId);
  const scope=environment && project?.organizationId && ws.tree.organizations.some(org=>org.id===project.organizationId)
    ? {organizationId:project.organizationId,projectId:project.id,environmentId:environment.id} : undefined;
  const node=scope && can('gateway.read',scope) ? requested : undefined;
  const events=usePaged(node?'/gateway-nodes/'+node.id+'/events':null,5000);
  const [edit,setEdit]=useState<GatewayNode>();
  const writable=!!scope && ws.scopeReady && !remote.loading && !remote.error && hasIdentityRevision(node) && can('gateway.operate',scope,true);
  const canSave=writable && !!edit && node?.id===edit.id && node.environmentId===edit.environmentId;

  useEffect(()=>{
    if(remote.errorStatus===403 || remote.errorStatus===404)setEdit(undefined);
  },[remote.errorStatus]);

  async function loadLatest() {
    if(!edit || !canSave)throw Error('当前无法核实此节点的编辑权限。');
    const latest=await apiRequest<GatewayNode>('/gateway-nodes/'+edit.id);
    if(latest?.id!==edit.id || latest.environmentId!==edit.environmentId || !hasIdentityRevision(latest))
      throw Error('无法核实该节点的最新身份状态和修订。');
    return latest;
  }

  async function save(value:Record<string,any>,key:string,fresh?:{eTag?:string}) {
    if(!edit || !canSave)throw Error('当前无法核实此节点的编辑权限。');
    await apiRequest('/gateway-nodes/'+edit.id+'/enabled',{method:'PUT',body:{enabled:value.enabled},
      etag:fresh?.eTag??edit.eTag,idempotencyKey:key});
    remote.reload();
  }

  return <>
    <div className="breadcrumb"><Link to="/nodes">网关节点</Link> / 节点详情</div>
    <div className="page-head"><div><h1>{node?.nodeName||'节点详情'}</h1><p>身份停用与运行进程分别管理</p></div>
      <button className="btn" disabled={!writable} onClick={()=>node && setEdit(node)}>编辑身份状态</button>
    </div>
    <Feedback {...remote}/>{node && <div className="card details"><Details value={node}/></div>}
    <div className="card"><h3 className="section-title">真实注册 / 心跳 / ACK 事件</h3>
      <Feedback {...events}/><Table rows={node?events.data?.items||[]:[]} columns={[['createdAt','时间'],['eventType','类型'],['message','说明'],['detail','安全诊断内容']]}/>
      <Pagination remote={events}/>
    </div>
    {edit && <Editor title="节点身份状态" fields={fields} initial={{enabled:edit.enabled}} allowed={canSave}
      help="保存后立即更新该节点的身份状态" close={()=>setEdit(undefined)} loadLatest={loadLatest} save={save}/>}
  </>;
}
