import {useWorkspace} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import type {Resource,Scope} from '../api/types';
import type {SnapshotView} from '../pages/snapshot-diff.mjs';
import {Details,Feedback,Pagination,Table,usePaged,useRemote} from '../ui';

export type EnvironmentRuntimeView='snapshot'|'nodes';

function CurrentSnapshot({environment}:{environment:Resource}) {
 const remote=useRemote<{deploymentSequence:number;snapshot:SnapshotView|null}>('/environments/'+environment.id+'/snapshots/current',0,false,true);
 const snapshot=remote.data?.snapshot;
 return <><Feedback {...remote}/>
  {!remote.loading&&!remote.error&&remote.data&&(snapshot===null?<p>此环境尚未下发配置快照。</p>
   :snapshot?.environmentId===environment.id?<><p>目标配置版本 {snapshot.configVersion} · 下发序列 {remote.data.deploymentSequence}</p><Details value={snapshot}/></>
   :<p role="alert">服务端返回的快照与当前环境不符。</p>)}</>;
}

function GatewayBindings({scope}:{scope:Scope}) {
 const ws=useWorkspace(),{canAct}=useSession();
 const nodes=usePaged('/environments/'+scope.environmentId+'/gateway-nodes');
 const writable=canAct('gateway.operate',scope);
 const rows=(nodes.data?.items??[]).filter((node:{environmentId:string})=>node.environmentId===scope.environmentId);
 return <><p>首次绑定由部署配置登记环境和节点名，网关注册后可在此管理节点身份。</p>
  <Feedback {...nodes}/>
  {!nodes.loading&&!nodes.error&&<>{rows.length?<Table rows={rows} columns={[[ 'nodeName','节点名称'],['enabled','身份启用'],['status','报告状态']]}
   actions={node=><button className="text-btn" onClick={()=>ws.selectScope(scope,'/nodes/'+node.id)}>{writable?'管理节点身份':'查看节点身份'}</button>}/>:<p>此环境尚无已注册的网关节点。</p>}
   <Pagination remote={nodes}/></>}</>;
}

export function EnvironmentRuntimePanel({environment,scope,view,close}:{environment:Resource;scope:Scope;view:EnvironmentRuntimeView;close:()=>void}) {
 const title=environment.name+' · '+(view==='snapshot'?'当前快照':'Gateway 绑定');
 return <section className="card details" aria-label={title}><header><h2>{title}</h2><button className="text-btn" onClick={close}>关闭</button></header>
  {view==='snapshot'?<CurrentSnapshot environment={environment}/>:<GatewayBindings scope={scope}/>}</section>;
}
