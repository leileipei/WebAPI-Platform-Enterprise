import {useState} from 'react';
import {useWorkspace,Link} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {useRemote,Feedback} from '../ui';
import {observationQuery,type ObservationEnvelope,type MetricsDto} from '../api/observability';
import {metricDisplay} from '../observability/query-state.mjs';
import {sourceNames} from '../observability/SourceNotice';
import {healthNames} from './ApiCatalog';

type Summary={apiId:string;environmentId:string;runningState:string;configVersion:number|null;
 routePolicies?:{routeId:string;path:string;authentication:string;timeoutMs:number}[]|null;policies:{policyId:string;type:string;revision:number|null}[]|null;
 clusters:{clusterId:string;health:string}[]|null;
 releases:{id:string;releaseNo:string;status:string;releaseType:string;createdAt:string}[]|null};
export function ApiOverview({apiId}:{apiId:string}){
 const ws=useWorkspace(),session=useSession(),[end,setEnd]=useState(()=>Date.now());
 const scope={organizationId:ws.organizationId,projectId:ws.projectId,environmentId:ws.environmentId,allAccessibleEnvironments:false};
 const allowed=!!ws.scopeReady&&!!scope.environmentId&&session.can('api.read',scope);
 const canKpi=allowed&&session.can('metrics.read',scope);
 const canPolicy=allowed&&session.can('route.read',scope)&&session.can('policy.read',scope);
 const canHealth=canKpi&&session.can('route.read',scope)&&session.can('cluster.read',scope);
 const canRelease=allowed&&session.can('release.read',scope);
 const range={start:new Date(end-3600000).toISOString(),end:new Date(end).toISOString()};
 const metrics=useRemote<ObservationEnvelope<MetricsDto>>(canKpi?`/observability/apis/${apiId}/metrics?`+observationQuery(scope,range,{}):null,0,false,true);
 const overview=useRemote<Summary>(canPolicy||canRelease||canHealth?`/apis/${apiId}/overview?`+new URLSearchParams({environmentId:ws.environmentId}):null,0,false,true);
 const data=overview.data?.apiId===apiId&&overview.data.environmentId===ws.environmentId&&!overview.loading&&!overview.error?overview.data:undefined;
 const values=!metrics.loading&&!metrics.error?metrics.data:undefined;
 if(!allowed||!canKpi&&!canPolicy&&!canHealth&&!canRelease)return null;
 return <section className="card" aria-label="API 运行总览"><div className="page-head"><h2>当前环境 · API 总览</h2><button className="btn" onClick={()=>{setEnd(Date.now());overview.reload();}}>刷新总览</button></div>
  {canKpi&&<><h3>运行 KPI · 近 1 小时</h3><Feedback {...metrics}/>{values&&<><small>{sourceNames[values.sourceState]} · {new Date(values.range.end).toLocaleString()}</small>
   <dl className="release-entry-grid">{[['request_count','请求数'],['request_rps','平均 RPS'],['success_ratio','成功率'],['latency_p95_ms','P95 延迟']].map(([key,label])=>{
    const metric=values.data?.kpis.find(k=>k.metric===key);return <div key={key}><dt>{label}</dt><dd>{metric?metricDisplay(metric):'—'}</dd></div>;
   })}</dl></>}</>}
  <Feedback {...overview}/>
  {data&&<>{(canPolicy||canHealth)&&<p className="muted">{data.runningState==='Confirmed'?'节点已确认运行配置 v'+data.configVersion:'当前运行配置尚未确认'}</p>}
   {canPolicy&&data.policies&&<><h3>运行策略</h3>{!!data.routePolicies?.length&&<ul>{data.routePolicies.map(r=><li key={r.routeId}><code>{r.path}</code> · {r.authentication} · 超时 {r.timeoutMs} ms</li>)}</ul>}{data.policies.length?<ul>{data.policies.map(p=><li key={p.policyId+':'+p.revision}><Link to={'/policies/'+p.policyId}>{p.type} · {p.policyId}</Link>{p.revision!=null?' · 修订 '+p.revision:''}</li>)}</ul>:<p>{data.runningState==='Confirmed'?'当前快照没有记录其他显式策略绑定。':'待节点确认后显示运行策略。'}</p>}</>}
   {canHealth&&data.clusters&&<><h3>后端集群健康</h3>{data.clusters.length?<ul>{data.clusters.map(c=><li key={c.clusterId}><Link to={'/clusters/'+c.clusterId}>{c.clusterId}</Link> · {healthNames[c.health]??'未知'}</li>)}</ul>:<p>{data.runningState==='Confirmed'?'此 API 在当前快照中没有后端集群。':'待节点确认后显示所属集群。'}</p>}</>}
   {canRelease&&data.releases&&<><h3>最近发布</h3>{data.releases.length?<ul>{data.releases.map(r=><li key={r.id}><Link to={'/releases/'+r.id}>{r.releaseNo||r.id}</Link> · {r.status} · {new Date(r.createdAt).toLocaleString()}</li>)}</ul>:<p>此 API 在当前环境没有发布记录。</p>}</>}
  </>}
 </section>;
}
