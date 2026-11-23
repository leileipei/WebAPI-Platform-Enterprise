import {useEffect,useState} from 'react';
import {Link,useWorkspace} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {apiRequest} from '../api/client';
import {OperationStatus,type GatewayOperation} from '../gateway/OperationStatus';
import {Editor,Feedback,Pagination,Table,usePaged,useRemote,type Field} from '../ui';

type Destination = {id:string;clusterId:string;name:string;address:string;weight:number;enabled:boolean;metadata:string|null;revision:number;activeHealth?:string;passiveHealth?:string};
type Cluster = {id:string;projectId:string;environmentId:string;name:string;loadBalancingPolicy:string;healthCheckEnabled:boolean;
  healthCheckPath:string;healthCheckIntervalSec:number;status:string;revision:number;destinations:Destination[];healthyDestinationCount:number|null;routeCount:number|null;routes?:{id:string;name:string;path:string;enabled:boolean}[]};
type EditTarget = {type:'cluster';environmentId:string;row?:Cluster} | {type:'destination';clusterId:string;row?:Destination};

const clusterFields:Field[]=[{key:'name',label:'集群名称',required:true},{key:'loadBalancingPolicy',label:'负载均衡',required:true,options:['RoundRobin','Random','LeastRequests','PowerOfTwoChoices','FirstAlphabetical'].map(value=>({value,label:value}))},{key:'healthCheckEnabled',label:'启用主动健康检查',type:'checkbox'},{key:'healthCheckPath',label:'健康路径',required:true},{key:'healthCheckIntervalSec',label:'检查间隔 / 秒',type:'number',required:true},{key:'status',label:'状态',options:['Active','Disabled'].map(value=>({value,label:value}))}];
const destinationFields:Field[]=[{key:'name',label:'实例名称',required:true},{key:'address',label:'地址',required:true,help:'HTTP(S) 地址必须位于服务端允许的来源内'},{key:'weight',label:'权重 1–1000',type:'number',required:true},{key:'enabled',label:'启用',type:'checkbox'},{key:'metadata',label:'元数据 JSON',type:'textarea'}];
const newCluster = {status:'Active',loadBalancingPolicy:'RoundRobin',healthCheckEnabled:false,healthCheckPath:'/health',healthCheckIntervalSec:30};
const newDestination = {weight:1,enabled:true};

export function Clusters({id}:{id?:string}) {
  const ws=useWorkspace(), {can}=useSession();
  const detail=useRemote<Cluster>(id && can('cluster.read') && (ws.environmentId || ws.scopeReady) ? '/clusters/'+id : null,0,false,true);
  const list=usePaged(!id && ws.environmentId ? '/environments/'+ws.environmentId+'/clusters' : null);
  const remote=id?detail:list;
  const [edit,setEdit]=useState<EditTarget>();
  const [copy,setCopy]=useState<Cluster>(),[probe,setProbe]=useState<{clusterId:string;operation:GatewayOperation}>(),[probing,setProbing]=useState(false),[actionError,setActionError]=useState('');
  useEffect(()=>{setCopy(undefined);setProbe(undefined);setActionError('');},[id,ws.environmentId]);
  const healthLabel=(state?:string)=>({Healthy:'健康',Unhealthy:'异常',Unknown:'未知',Disabled:'未启用'}[state??'Unknown']??'未知');

  function inWorkspace(row:Cluster) {
    return !!row.id && !!ws.environmentId && row.environmentId===ws.environmentId && row.projectId===ws.projectId;
  }
  const readable=!!ws.scope && can('cluster.read',ws.scope);
  const requested=id && detail.data?.id?.toLowerCase()===id.toLowerCase() ? detail.data : undefined;
  const matchingWorkspace=requested && readable && inWorkspace(requested);
  const readOnlyDetail=!!requested && ws.scopeReady && !detail.loading && !detail.error &&
    !ws.tree.environments.some(environment=>environment.id===requested.environmentId) && can('cluster.read');
  const cluster=matchingWorkspace || readOnlyDetail ? requested : undefined;
  const listRows:Cluster[]=readable ? (list.data?.items??[]).filter(inWorkspace) : [];
  const destinations=cluster?.destinations?.filter(row=>!!row.id && row.clusterId===cluster.id)??[];
  const writable=ws.scopeReady && !!ws.environmentId && can('cluster.write',ws.scope,true);
  const rowsWritable=writable && !remote.loading && !remote.error;
  const detailWritable=rowsWritable && !!matchingWorkspace;

  const ownerEnvironment=ws.tree.environments.find(env=>env.id===requested?.environmentId && env.projectId===requested?.projectId);
  const ownerProject=ownerEnvironment && ws.tree.projects.find(project=>project.id===ownerEnvironment.projectId);
  const ownerScope=ownerEnvironment && ownerProject?.organizationId && ws.tree.organizations.some(org=>org.id===ownerProject.organizationId)
    ? {organizationId:ownerProject.organizationId,projectId:ownerProject.id,environmentId:ownerEnvironment.id} : undefined;

  function maySave(target:EditTarget) {
    if (!writable) return false;
    const rowId=target.row?.id;
    if (target.type==='cluster') {
      if (target.environmentId!==ws.environmentId) return false;
      if (!rowId) return !id;
      return rowsWritable && (id ? rowId===cluster?.id : listRows.some(row=>row.id===rowId));
    }
    return detailWritable && target.clusterId===cluster?.id && (!rowId || destinations.some(row=>row.id===rowId));
  }

  async function loadLatest(target:EditTarget) {
    if (!target.row || !maySave(target)) throw Error('当前范围无法编辑此对象。');
    const clusterId=target.type==='cluster'?target.row.id:target.clusterId;
    const latest=await apiRequest<Cluster>('/clusters/'+clusterId);
    if (latest.id!==clusterId || !inWorkspace(latest)) throw Error('无法在当前环境核实集群归属。');
    if (target.type==='cluster') return latest;
    const destination=latest.destinations?.find(row=>row.id===target.row?.id && row.clusterId===clusterId);
    if (!destination) throw Error('当前集群中找不到该实例。');
    return destination;
  }

  async function save(target:EditTarget,value:Record<string,any>,key:string,revision?:number) {
    if (!maySave(target)) throw Error('当前范围无法编辑此对象。');
    const path=target.type==='cluster'
      ? (target.row?'/clusters/'+target.row.id:'/environments/'+target.environmentId+'/clusters')
      : (target.row?'/destinations/'+target.row.id:'/clusters/'+target.clusterId+'/destinations');
    await apiRequest(path,{method:target.row?'PUT':'POST',
      body:target.type==='destination'?{...value,metadata:value.metadata||null}:value,
      etag:target.row?'"'+(revision??target.row.revision)+'"':undefined,idempotencyKey:key});
    remote.reload();
  }

  async function probeDestination(row:Destination){
    if(!cluster || !detailWritable || probing)return;
    setProbing(true);setActionError('');
    try{
      const operation=await apiRequest<GatewayOperation>('/clusters/'+cluster.id+'/destinations/'+row.id+'/probe',{method:'POST',etag:'"'+row.revision+'"'});
      setProbe({clusterId:cluster.id,operation});
    }catch(e){setActionError(e instanceof Error?e.message:'无法立即探测');}finally{setProbing(false);}
  }

  return <>
    <div className="breadcrumb"><Link to="/clusters">后端集群</Link>{id && ' / 实例配置'}</div>
    <div className="page-head"><div><h1>{id?cluster?.name||'集群详情':'后端集群'}</h1>
      <p>环境专属的后端连接与健康检查工作配置</p></div>
      <div className="actions"><button className="btn" onClick={remote.reload}>刷新</button>{id ? <>
        <button className="btn" disabled={!detailWritable} onClick={()=>cluster && setEdit({type:'cluster',environmentId:cluster.environmentId,row:cluster})}>编辑集群</button>
        <button className="btn primary" disabled={!detailWritable} onClick={()=>cluster && setEdit({type:'destination',clusterId:cluster.id})}>＋ 新增实例</button>
      </> : <button className="btn primary" disabled={!writable} onClick={()=>setEdit({type:'cluster',environmentId:ws.environmentId})}>＋ 新建集群</button>}</div>
    </div>
    <div className="notice">工作配置保存后仍需发布。正在运行的后端地址来自已激活 Snapshot。</div>
    {cluster && readOnlyDetail && <p className="notice" role="status">此详情由服务端授权读取。环境未在工作区目录显示，当前仅可查看。</p>}
    {id && detail.data && !cluster && !detail.loading && !detail.error && ws.scopeReady && <div className="notice" role="status">
      {ownerScope && can('cluster.read',ownerScope)
        ? <>此集群属于{ownerEnvironment?.name}。 <button className="text-btn" onClick={()=>ws.selectScope(ownerScope)}>切换到{ownerEnvironment?.name}查看集群</button></>
        : <>当前范围无法核实此集群的归属。 <Link to="/clusters">返回当前环境的集群列表</Link></>}
    </div>}
    <div className="card"><Feedback {...remote}/>
      {id ? <Table rows={destinations.map(row=>({...row,activeText:healthLabel(row.activeHealth),passiveText:healthLabel(row.passiveHealth)}))} columns={[['name','实例'],['address','地址'],['weight','权重'],['activeText','主动健康'],['passiveText','被动健康'],['enabled','启用'],['revision','修订']]}
        actions={row=><><button className="text-btn" disabled={!detailWritable||!row.enabled||probing||cluster?.status!=='Active'} onClick={()=>probeDestination(row)}>立即探测</button>
          <button className="text-btn" disabled={!detailWritable} onClick={()=>cluster && setEdit({type:'destination',clusterId:cluster.id,row})}>编辑</button></>}/>
      : <><Table rows={listRows.map(row=>({...row,healthy:row.healthyDestinationCount==null?'未知':row.healthyDestinationCount}))} columns={[['name','集群名称'],['loadBalancingPolicy','负载均衡'],['healthy','健康实例数'],['routeCount','引用路由数'],['healthCheckEnabled','健康检查'],['status','状态'],['revision','修订']]}
        actions={row=><><Link to={'/clusters/'+row.id}>查看实例</Link>
          <button className="text-btn" disabled={!rowsWritable} onClick={()=>setEdit({type:'cluster',environmentId:row.environmentId,row})}>编辑</button>
          <button className="text-btn" disabled={!rowsWritable} onClick={()=>setCopy(row)}>复制</button></>}/>
        <Pagination remote={list}/></>}
    </div>
    {cluster?.routes && <div className="card"><h3>引用路由</h3><Table rows={cluster.routes} columns={[['name','路由名称'],['path','路径'],['enabled','启用']]}/></div>}
    {cluster && actionError && <p className="error" role="alert">{actionError}</p>}
    {cluster && probe?.clusterId===cluster.id && <OperationStatus key={probe.operation.id} initial={probe.operation} path={'/clusters/'+cluster.id+'/probes/'+probe.operation.id}/>}
    {copy && <Editor title="复制集群" fields={[{key:'name',label:'新集群名称',required:true}]} initial={{name:copy.name+' 副本'}}
      allowed={rowsWritable && !id && listRows.some(r=>r.id===copy.id)} close={()=>setCopy(undefined)}
      help="复制工作配置和实例，保存后可编辑并发布" loadLatest={()=>loadLatest({type:'cluster',environmentId:copy.environmentId,row:copy})}
      save={async(value,key,fresh)=>{
        if(!rowsWritable || id || !inWorkspace(copy))throw Error('当前范围无法复制此集群。');
        await apiRequest('/clusters/'+copy.id+'/copy',{method:'POST',body:{name:value.name},etag:'"'+(fresh?.revision??copy.revision)+'"',idempotencyKey:key});list.reload();
      }}/>}
    {edit && <Editor title={edit.type==='cluster'?'集群配置':'Destination 配置'} fields={edit.type==='cluster'?clusterFields:destinationFields}
      initial={edit.row??(edit.type==='cluster'?newCluster:newDestination)} allowed={maySave(edit)} close={()=>setEdit(undefined)}
      loadLatest={edit.row?()=>loadLatest(edit):undefined} save={(value,key,fresh)=>save(edit,value,key,fresh?.revision)}/>}
  </>;
}
