import {useEffect,useRef,useState} from 'react';
import {useWorkspace,navigate} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {Details} from '../ui';
import {apiRequest} from '../api/client';
import type {Version} from '../api/catalog';
import type {PolicyReference} from '../api/policies';
import type {Page} from '../api/types';
import {readAllPages} from '../api/paging.mjs';
import {advancedPolicyReview} from './advanced-policy-state.mjs';

type ReleaseRoute={id:string;environmentId:string;apiVersionId:string};
type Revision={type:string;id:string;revision:number};
type Candidate={
  routes:ReleaseRoute[];
  bindings?:{routeId:string;policyId:string}[];
  policies?:any[];
  resourceRevisions:Revision[];
};
type DraftBody={baseConfigVersion:number;versionIds:string[];resourceRevisions:Revision[]};
type VersionGroup={apiId:string;versions:Version[]};
type Options={groups:VersionGroup[];otherEnvironmentReferences:number};

export function PolicyReleaseReview({policyId}:{policyId:string}){
  const ws=useWorkspace(),{can,user}=useSession();
  const [environmentId,setEnvironment]=useState(ws.environmentId);
  const [options,setOptions]=useState<Options>();
  const [selection,setSelection]=useState<Record<string,string>>({});
  const [review,setReview]=useState<{candidate:Candidate;body:DraftBody;baseline:any[]|null}>();
  const [error,setError]=useState(''),[busy,setBusy]=useState(false);
  const attempt=useRef<{signature:string;key:string}|null>(null);
  const epoch=useRef(0),mounted=useRef(true),previous=useRef('');
  const identity=JSON.stringify([policyId,environmentId,user?.id,user?.permissions,user?.scopes]);
  const env=ws.tree.environments.find(e=>e.id===environmentId),project=ws.tree.projects.find(p=>p.id===env?.projectId);
  const scope=project?{organizationId:project.organizationId!,projectId:project.id,environmentId}:undefined;
  const allowed=!!scope&&can('release.create',scope,true);
  const selected=Object.values(selection).filter(Boolean).sort();

  if(previous.current!==identity){previous.current=identity;epoch.current++;}
  useEffect(()=>{
    mounted.current=true;
    return ()=>{mounted.current=false;epoch.current++;};
  },[]);
  useEffect(()=>{
    setReview(undefined);setOptions(undefined);setSelection({});setError('');setBusy(false);
    attempt.current=null;
  },[identity]);
  const current=(generation:number)=>mounted.current&&generation===epoch.current;

  function invalidate(){
    epoch.current++;setReview(undefined);setError('');setBusy(false);
  }

  async function prepare(){
    if(!allowed)return;
    const generation=++epoch.current;
    setBusy(true);setError('');setReview(undefined);setOptions(undefined);setSelection({});
    try{
      const [references,routes]:[Page<PolicyReference>,Page<ReleaseRoute>]=await Promise.all([
        readAllPages(apiRequest,`/policies/${policyId}/references`),
        readAllPages(apiRequest,`/environments/${environmentId}/routes`)
      ]);
      const routeVersions=new Map(routes.items.map(r=>[r.id,r.apiVersionId]));
      const affected=new Map<string,Set<string>>();
      for(const ref of references.items){
        if(ref.referenceKind!=='Working'||ref.environmentId!==environmentId||!ref.apiId||!ref.routeId)continue;
        const versionId=routeVersions.get(ref.routeId);
        if(!versionId)continue;
        const ids=affected.get(ref.apiId)??new Set<string>();
        ids.add(versionId);affected.set(ref.apiId,ids);
      }
      const groups=(await Promise.all(Array.from(affected,async ([apiId,ids])=>{
        const versions=await apiRequest<Version[]>(`/apis/${apiId}/versions`);
        return {apiId,versions:versions.filter(v=>v.apiId===apiId&&ids.has(v.id)&&v.status!=='Retired')};
      }))).filter(group=>group.versions.length>0);
      if(current(generation)){
        setOptions({groups,otherEnvironmentReferences:references.items.filter(r=>r.environmentId!==environmentId).length});
        setSelection(Object.fromEntries(groups.filter(g=>g.versions.length===1).map(g=>[g.apiId,g.versions[0].id])));
      }
    }catch(e){if(current(generation))setError((e as Error).message);}
    finally{if(current(generation))setBusy(false);}
  }

  async function readPreview(){
    if(!allowed||!selected.length)return;
    const generation=++epoch.current;
    setBusy(true);setError('');setReview(undefined);
    try{
      const environment=await apiRequest<{desiredConfigVersion?:number|null}>(`/environments/${environmentId}`);
      const baseConfigVersion=environment.desiredConfigVersion??0;
      const candidate=await apiRequest<Candidate>(`/environments/${environmentId}/releases/preview`,{
        method:'POST',body:{baseConfigVersion,versionIds:selected}
      });
      const boundRoutes=new Set(candidate.bindings?.filter(b=>b.policyId===policyId).map(b=>b.routeId));
      const boundVersions=new Set(candidate.routes.filter(r=>boundRoutes.has(r.id)).map(r=>r.apiVersionId));
      if(!selected.every(id=>boundVersions.has(id)))throw new Error('所选版本的策略绑定已变化，请重新加载受影响的 API 版本。');
      let baseline:any[]|null=baseConfigVersion?null:[];
      if(baseConfigVersion&&scope&&can('gateway.config.read',scope)){
        try{
          const snapshot=await apiRequest<{policies:any[]}>(`/environments/${environmentId}/snapshots/${baseConfigVersion}`);
          baseline=snapshot.policies;
        }catch{ /* An unreadable baseline stays unknown. */ }
      }
      // The server hashes the request, including array order. Keep equivalent retries byte-for-byte identical.
      const resourceRevisions=[...candidate.resourceRevisions].sort((a,b)=>a.type.localeCompare(b.type)||a.id.localeCompare(b.id));
      if(current(generation))setReview({candidate,baseline,body:{baseConfigVersion,versionIds:selected,resourceRevisions}});
    }catch(e){if(current(generation))setError((e as Error).message);}
    finally{if(current(generation))setBusy(false);}
  }

  async function create(){
    if(!allowed||!review||busy)return;
    const generation=++epoch.current;
    setBusy(true);setError('');
    try{
      const signature=JSON.stringify(review.body);
      if(attempt.current?.signature!==signature)attempt.current={signature,key:crypto.randomUUID()};
      const release=await apiRequest<{id:string}>(`/environments/${environmentId}/releases`,{
        method:'POST',body:review.body,idempotencyKey:attempt.current.key
      });
      if(current(generation))navigate('/releases/'+release.id);
    }catch(e){if(current(generation))setError((e as Error).message);}
    finally{if(current(generation))setBusy(false);}
  }

  return <div className="card details">
    <h3>提交发布 · 目标环境</h3>
    <p className="notice">每个 API 本次最多发布一个版本。有多个受影响版本时，请选择本次发布的版本。发布后，所选 API 的路由切换到该版本，其他 API 保留原运行配置。新限流修订会重建额度，熔断修订或后端集群变更会重建节点状态。请在发布详情中确认审批和运行结果。</p>
    <div className="filter-bar">
      <label>目标环境<select value={environmentId} onChange={e=>{invalidate();setEnvironment(e.target.value);}}>
        {ws.tree.environments.map(e=><option key={e.id} value={e.id}>{e.name}</option>)}
      </select></label>
      {allowed&&<button type="button" className="btn" disabled={busy||!env} onClick={()=>void prepare()}>加载受影响 API 版本</button>}
    </div>
    {!allowed&&<p className="notice">目标环境无 release.create 权限。</p>}
    {options&&allowed&&<>
      <div className="form-grid">{options.groups.map(({apiId,versions})=><label key={apiId}>
        API {apiId}<select value={selection[apiId]??''} onChange={e=>{
          const versionId=e.target.value;invalidate();setSelection(s=>({...s,[apiId]:versionId}));
        }}>
          <option value="">本次不发布</option>
          {versions.map(v=><option key={v.id} value={v.id}>{v.version} · {v.status} · {v.id}</option>)}
        </select>
      </label>)}</div>
      {options.groups.length===0&&<p>当前环境没有引用此策略的可发布版本。</p>}
      <p>本次已选 {selected.length} 个版本，其他环境可见引用 {options.otherEnvironmentReferences} 条。</p>
      <button type="button" className="btn" disabled={busy||!selected.length} onClick={()=>void readPreview()}>读取完整发布 Review</button>
    </>}
    {review&&allowed&&<>
      <AdvancedPolicyReview policies={review.candidate.policies||[]} baselinePolicies={review.baseline}/>
      <Details value={review.candidate}/>
      <button type="button" className="btn primary" disabled={busy} onClick={()=>void create()}>确认 Review，创建发布草稿</button>
    </>}
    {error&&<div className="error" role="alert">{error}</div>}
  </div>;
}

export function AdvancedPolicyReview({policies,baselinePolicies=null}:{policies:any[];baselinePolicies?:any[]|null}){const rows=advancedPolicyReview(policies,{baselinePolicies});return <section className="advanced-policy-review"><h4>高级流量策略 Review</h4><p className="notice">创建发布草稿后仍须按目标环境的审批流程提交、审批并发布。结构检查不等于签名验证或运行生效。</p>{rows.length?rows.map(r=><div key={r.id} className="details">{r.error?<p className="error">{r.error}</p>:r.type==='authentication'?<><strong>JWT · 固定公钥</strong><p>kid：{r.kids.join('、')||'无'} · 应用映射 {r.mappingCount} 项</p><p>{r.addedKids===null?'基线不可读取，密钥增删尚未确认':`新增 kid：${r.addedKids.join('、')||'无'}；移除 kid：${r.removedKids.join('、')||'无'}`}</p><p>转发 Bearer：{r.forwardBearer?'启用，需确认后端信任与令牌暴露风险':'关闭'}</p></>:['cors','ip_filter','transform'].includes(r.type)?<NetworkPolicyReviewSummary review={r}/>:r.type==='retry'?<p>重试：最多 {r.maxAttempts} 次物理尝试（包含首次），受路由总超时和安全重放条件约束。</p>:<p>缓存：TTL 上限 {r.ttlSeconds} 秒；本次发布 / 回滚将缓存冷启动，不保证业务即时一致。</p>}</div>):<p>候选配置无启用的高级流量策略。</p>}</section>;}

function NetworkPolicyReviewSummary({review}:{review:any}){return <div><strong>{review.type==='cors'?'CORS':review.type==='ip_filter'?'IP 名单':'Transform'} · 目标节点需支持快照 {review.schemaVersion}</strong>{review.type==='cors'?<><p>来源：{review.allowedOrigins.join('、')} · credentials {review.credentials?'开启':'关闭'}</p>{review.allowedOrigins.includes('*')&&<p className="notice">通配来源，请核对浏览器公开访问范围。</p>}</>:review.type==='ip_filter'?<><p>网段：{review.ranges.join('、')}</p>{review.allNetwork&&<p className="notice">包含 /0 全网规则，核对名单模式和覆盖范围。</p>}</>:<><p>原 API 身份 / 方法 / 授权不变。路径：{review.path?JSON.stringify(review.path):'保持'}</p>{(['requestBody','responseBody'] as const).map(direction=><div key={direction}><strong>{direction==='requestBody'?'JSON 请求体':'JSON 响应体'} · {review[direction]?'启用':'关闭'}</strong>{review[direction]&&<><p>每方向 {review[direction].maxBodyBytes} 字节 · {review[direction].operations.length} 条有序操作 · 选择 {JSON.stringify(review[direction].methods??review[direction].statusCodes)}</p><pre className="code">{JSON.stringify(review[direction].operations,null,2)}</pre></>}</div>)}<p className="notice">Header、路径、Query 和两方向 JSON 规则整体冻结；映射不会自动修改原 Schema / 示例，发布前核对对外契约。</p></>}</div>;}
