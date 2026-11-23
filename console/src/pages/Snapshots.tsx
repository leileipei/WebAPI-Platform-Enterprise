import {useMemo,useRef,useState} from 'react';
import {Link,navigate,useWorkspace} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {apiRequest} from '../api/client';
import {releases} from '../api/releases';
import {useDeliveryData} from '../delivery/api';
import {Details,Feedback,Pagination,Table,usePaged,useRemote} from '../ui';
import {compareSnapshots,type SnapshotView} from './snapshot-diff.mjs';

const resourceNames:Record<string,string>={routes:'路由',clusters:'后端集群',policies:'策略',applications:'应用',schemaVersion:'快照格式'};
type RollbackSource={releaseId:string;configVersion:number;deploymentSequence:number};

function Comparison({path,environmentId,base,target}:{path:string;environmentId:string;base:number;target:number}){
  const left=useRemote<SnapshotView>(path+'/'+base,0,false,true);
  const right=useRemote<SnapshotView>(path+'/'+target,0,false,true);
  const changes=useMemo(()=>{
    if(left.loading||right.loading||left.error||right.error||left.data?.environmentId!==environmentId||right.data?.environmentId!==environmentId||
      left.data.configVersion!==base||right.data.configVersion!==target)return undefined;
    return compareSnapshots(left.data,right.data);
  },[left.loading,right.loading,left.error,right.error,left.data,right.data,environmentId,base,target]);
  return <section className="card details"><h2>版本 {base} → {target} 的配置差异</h2>
    <Feedback {...left}/><Feedback {...right}/>
    {changes?.length===0&&<p>两版脱敏管理视图的配置内容相同。原始快照的 SHA256 可在列表中核对。</p>}
    {changes?.map(row=><details key={row.kind+':'+row.id}><summary>{row.change} · {resourceNames[row.kind]} · {row.id}</summary>
      {row.before!==undefined&&<><h3>基准版本 {base}</h3><Details value={row.before}/></>}
      {row.after!==undefined&&<><h3>对比版本 {target}</h3><Details value={row.after}/></>}
    </details>)}</section>;
}

function SnapshotDirectory({environmentId,allowRollback,allowRelease}:{environmentId:string;allowRollback:boolean;allowRelease:boolean}){
  const path='/environments/'+environmentId+'/snapshots',list=usePaged(path);
  const [viewId,view]=useState<number>(),[base,setBase]=useState<number>(),[target,setTarget]=useState<number>();
  const [pending,setPending]=useState<{target:number;source:RollbackSource;key:string}>(),[busy,setBusy]=useState(false);
  const [error,setError]=useState(''),inFlight=useRef(false);
  const detail=useRemote<SnapshotView>(viewId?path+'/'+viewId:null,0,false,true);
  const recovery=useDeliveryData(path+':'+allowRollback,async signal=>allowRollback
    ?apiRequest<{source:RollbackSource|null}>(path+'/rollback-source',{signal})
    :{source:null});
  const source:RollbackSource|undefined=recovery.data?.source;
  async function createRollback(){
    if(!pending||!allowRollback||inFlight.current||source?.releaseId!==pending.source.releaseId)return;
    const alive=recovery.lease();inFlight.current=true;setBusy(true);setError('');
    try{
      const draft=await releases.act(pending.source.releaseId,'rollback',{targetConfigVersion:pending.target},pending.key);
      if(alive())navigate('/releases/'+draft.id);
    }catch(e){
      if(alive()){setError((e as Error).message);if((e as {status?:number}).status===409){setPending(undefined);recovery.reload();list.reload();}}
    }finally{inFlight.current=false;if(alive())setBusy(false);}
  }
  return <><div className="breadcrumb">发布与审批 / Config Snapshot</div>
    <div className="page-head"><div><h1>配置快照</h1><p>历史配置摘要与脱敏管理视图</p></div></div>
    <p className="notice">每个配置版本可对应多次下发。下载内容为脱敏的管理视图。</p>
    <section className="card"><Feedback {...list}/>{allowRollback&&<Feedback {...recovery}/>}
      <Table rows={list.data?.items||[]} columns={[[ 'configVersion','配置版本'],['status','快照状态'],['payloadHash','原始字节 SHA256'],['sizeBytes','字节数'],['latestDeploymentSequence','最新下发序列'],['latestDeploymentState','最新发布状态'],['createdAt','创建时间']]} actions={r=><>
        {r.latestReleaseId&&allowRelease&&<Link to={'/releases/'+r.latestReleaseId}>关联发布</Link>}
        <button className="text-btn" onClick={()=>view(r.configVersion)}>查看管理视图</button>
        <a href={'/api/v1'+path+'/'+r.configVersion+'/download'} download>下载 JSON</a>
        <button className="text-btn" disabled={base===r.configVersion} onClick={()=>{setBase(r.configVersion);if(target===r.configVersion)setTarget(undefined);}}>设为基准</button>
        <button className="text-btn" disabled={!base||base===r.configVersion||target===r.configVersion} onClick={()=>setTarget(r.configVersion)}>与基准比较</button>
        {allowRollback&&source&&source.configVersion!==r.configVersion&&<button className="text-btn" disabled={busy} onClick={()=>{setError('');setPending({target:r.configVersion,source,key:crypto.randomUUID()});}}>回滚到此版本</button>}
      </>}/><Pagination remote={list}/></section>
    {base&&<p>基准版本 {base}{target&&' · 对比版本 '+target}　<button className="text-btn" onClick={()=>{setBase(undefined);setTarget(undefined);}}>清除比较</button></p>}
    {base&&target&&<Comparison key={base+':'+target} {...{path,environmentId,base,target}}/>}
    {viewId&&<section className="card details"><h2>版本 {viewId} 的管理视图</h2><Feedback {...detail}/><button className="text-btn" onClick={()=>view(undefined)}>关闭视图</button>
      {!detail.loading&&!detail.error&&detail.data?.environmentId===environmentId&&detail.data.configVersion===viewId&&<Details value={detail.data}/>}</section>}
    {pending&&source?.releaseId===pending.source.releaseId&&<section className="card details" aria-label="创建回滚草稿"><h2>回滚到配置版本 {pending.target}</h2>
      <p>当前发布版本 {source.configVersion}，下发序列 {source.deploymentSequence}。草稿将进入发布流程，提交后按环境规则审批。</p>
      <button className="btn primary" disabled={busy} onClick={()=>void createRollback()}>创建回滚草稿</button>
      <button className="btn" disabled={busy} onClick={()=>{setPending(undefined);setError('');}}>取消</button></section>}
    {error&&<p role="alert" className="error">{error}</p>}</>;
}

export function Snapshots(){
  const ws=useWorkspace(),{user,can}=useSession();
  if(!ws.scopeReady||!ws.environmentId||!ws.scope)return <p>请选择可访问的环境。</p>;
  return <SnapshotDirectory key={JSON.stringify([ws.environmentId,user?.id,user?.permissions,user?.scopes])} environmentId={ws.environmentId}
    allowRelease={can('release.read',ws.scope)} allowRollback={can('release.rollback',ws.scope,true)&&can('gateway.config.read',ws.scope)}/>;
}
