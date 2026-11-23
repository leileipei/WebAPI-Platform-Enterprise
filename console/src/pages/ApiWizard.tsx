import {useEffect,useRef,useState} from 'react';
import {Feedback,Details,Table} from '../ui';
import {useWorkspace,Link,navigate,allowNavigation} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {apiRequest} from '../api/client';
import type {Api,Version} from '../api/catalog';
import {ApiDetailContent} from './ApiDetail';
import {ProjectApi,type ProjectApiRemote} from './ProjectApi';
import {SchemaEditor} from './SchemaEditor';
import {Routes} from './Routes';
import {resolveReviewHandoff,releaseReviewIds,timedComparison} from '../comparisons/model.mjs';
import {ReleaseRiskReview} from '../comparisons/ReleaseRiskReview';

export function ApiWizard({id,search=''}:{id:string;search?:string}){
 const ws=useWorkspace(),s=useSession();
 const identity=JSON.stringify([id,search,ws.organizationId,ws.projectId,ws.environmentId,s.user?.id,s.user?.permissions,s.user?.scopes,s.error]);
 return <ProjectApi key={identity} id={id}>{remote=><Wizard id={remote.data.api.id} search={search} remote={remote}/>}</ProjectApi>;
}

function Wizard({id,search,remote}:{id:string;search:string;remote:ProjectApiRemote}){
 const ws=useWorkspace(),{can}=useSession(),query=new URLSearchParams(search),reviewId=query.get('reviewId');
 const [step,setStep]=useState(reviewId||query.get('step')==='review'?5:0);
 const [versionId,setVersion]=useState<string>(),[draft,setDraft]=useState<any>(),[handoff,setHandoff]=useState<any>();
 const [error,setError]=useState(''),[busy,setBusy]=useState(false),[handoffLoading,setHandoffLoading]=useState(!!reviewId);
 const controller=useRef(new AbortController()),createKey=useRef<string|undefined>(undefined),submitKey=useRef<string|undefined>(undefined);
 const d=remote.data;
 const version=d?.versions.find(v=>v.id===versionId&&v.apiId===id&&v.status!=='Retired');
 const environment=ws.tree.environments.find(e=>e.id===ws.environmentId);
 const inScope=environment?.projectId===d.api.projectId;
 const ready=!!version&&ws.scopeReady&&inScope&&!remote.loading&&!remote.error&&!handoffLoading&&(!reviewId||handoff?.targetVersionId===version.id);
 const mayRelease=ready&&!busy&&can('release.create',ws.scope,true);
 const steps=['基本信息','API 版本','参数定义','请求与响应','路由与后端','Review 与提交'];
 const active=()=>!controller.current.signal.aborted;
 function clearDraft(){
  setDraft(undefined);
  createKey.current=undefined;
  submitKey.current=undefined;
 }
 function selectVersion(id:string){
  setVersion(id);
  clearDraft();
  setError('');
 }
 function refreshVersion(createdId?:string){
  if(createdId&&!reviewId)selectVersion(createdId);
  else clearDraft();
  remote.reload();
 }
 useEffect(()=>{
  if(reviewId)void resolveReviewHandoff(id,reviewId,apiRequest,controller.current.signal)
   .then(result=>{if(active()){setHandoff(result);setVersion(result.targetVersionId);}})
   .catch(e=>{if(active())setError(e.message);})
   .finally(()=>{if(active())setHandoffLoading(false);});
  return()=>controller.current.abort();
 },[]);
 useEffect(()=>{
  if(remote.loading)return;
  if(!reviewId&&versionId===undefined&&d&&!remote.error){
   const first=d.versions.find(v=>v.apiId===id&&v.status!=='Retired');
   if(first)setVersion(first.id);
  }
  if(!version||!inScope||remote.error)clearDraft();
 },[d,versionId,inScope,remote.loading,remote.error,reviewId,id,version]);
 async function create(){
  if(!mayRelease||!version)return;
  setBusy(true);setError('');
  try{
   const result=await timedComparison(async signal=>{
    const trusted=reviewId?await resolveReviewHandoff(id,reviewId,apiRequest,signal):undefined;
    const riskReviewIds=releaseReviewIds(trusted,version.id);
    const env=await apiRequest<any>(`/environments/${ws.environmentId}`,{signal});
    const selection={baseConfigVersion:env.desiredConfigVersion||0,versionIds:[version.id]};
    const candidate=await apiRequest<any>(`/environments/${ws.environmentId}/releases/preview`,{method:'POST',body:selection,signal});
    const body={...selection,resourceRevisions:candidate.resourceRevisions,...(riskReviewIds?{riskReviewIds}:{})};
    const signature=JSON.stringify(body);
    if(createKey.current?.split('|')[0]!==signature)createKey.current=signature+'|'+crypto.randomUUID();
    return apiRequest<any>(`/environments/${ws.environmentId}/releases`,{method:'POST',body,idempotencyKey:createKey.current.slice(signature.length+1),signal});
   },controller.current.signal);
   if(active()){
    setDraft({id:result.id});
    try{
     const frozenCandidate=await apiRequest<{versions:{api:Api;version:Version}[];routes:{environmentId:string;apiVersionId:string}[];preconditionsCurrent?:boolean}>(
      `/releases/${result.id}/preview`,{signal:controller.current.signal}
     );
     const selected=frozenCandidate.versions[0];
     if(result.environmentId!==ws.environmentId||frozenCandidate.versions.length!==1||
       selected.api.id!==id||selected.version.id!==version.id||selected.version.apiId!==id||
       !frozenCandidate.routes.every(row=>row.environmentId===ws.environmentId&&row.apiVersionId===version.id)){
      throw Error('候选与当前 API、版本或环境不一致，请进入草稿详情核对。');
     }
     if(frozenCandidate.preconditionsCurrent===false)throw Error('工作修订已变化，请进入草稿详情重新核对。');
     if(active())setDraft({id:result.id,frozenCandidate});
    }catch(e){if(active())setError('草稿已创建（'+result.id+'），预览读取失败：'+(e as Error).message);}
   }
  }catch(e){if(active())setError((e as Error).message);}
  finally{if(active())setBusy(false);}
 }
 async function submit(){
  if(!draft?.frozenCandidate||!mayRelease)return;
  setBusy(true);
  try{
   submitKey.current??=crypto.randomUUID();
   await timedComparison(signal=>apiRequest(`/releases/${draft.id}/submit`,{method:'POST',idempotencyKey:submitKey.current,signal}),controller.current.signal);
   if(active())navigate('/releases/'+draft.id);
  }catch(e){if(active())setError((e as Error).message);}
  finally{if(active())setBusy(false);}
 }
 return <>
  <div className="breadcrumb"><Link to={'/apis/'+id}>API 详情</Link> / 配置与发布向导</div>
  <div className="page-head"><div><h1>配置与发布</h1><p>逐步维护工作草稿，Review 后创建并提交真实发布申请</p></div></div>
  <div className="wizard-steps">{steps.map((title,i)=><button className={i===step?'active':''} key={title} disabled={busy} onClick={()=>{if(allowNavigation())setStep(i);}}><span>{i+1}</span>{title}</button>)}</div>
  <div className="notice">前五步各自保存到工作区。第六步创建发布草稿，提交时冻结候选；风险评审与正式审批分别记录。</div>
  <Feedback {...remote}/>
  {d&&<>
   {step>=1&&<div className="filter-bar"><label>API 版本 <select aria-label="向导 API 版本" disabled={busy||!!reviewId||remote.loading} value={version?.id||''} onChange={e=>{
    if(!allowNavigation())return;
    selectVersion(e.target.value);
   }}>
    <option value="">请选择可用版本</option>
    {d.versions.filter(v=>v.apiId===id&&v.status!=='Retired').map(v=><option key={v.id} value={v.id}>{v.version} · {v.status}</option>)}
   </select></label>
   <button className="btn" disabled={busy} onClick={remote.reload}>刷新工作数据</button>
   {reviewId&&<button className="btn" disabled={busy||!!draft} onClick={()=>navigate(`/apis/${id}/wizard?step=review`)}>退出评审关联并重新选择版本</button>}
   </div>}
   {step===0||step===1?<ApiDetailContent id={id} remote={remote} wizard={{
    version,locked:busy||!!reviewId||remote.loading||!!remote.error,onSelect:selectVersion,onSaved:refreshVersion,
   }}/>:step===2||step===3?<div className="card">
    {version?<SchemaEditor key={version.id+step} version={version} kind={step===2?'parameters':'schemas'} detail={d} checking={remote.loading||!!remote.error} reloadVersion={refreshVersion}/>:<div className="empty">请先选择版本</div>}
   </div>:step===4?(ready&&version?
    <Routes key={JSON.stringify([id,version.id,ws.environmentId])} target={{api:d.api,version,environmentId:ws.environmentId}} onChanged={clearDraft}/>:
    <p className="notice">请选择此 API 的可用版本与所属项目下的环境。</p>
   ):<>
    <ReleaseRiskReview reviews={handoff?[{...handoff.review,reviewId:handoff.review.id,fromVersion:handoff.view.from.version,toVersion:handoff.view.to.version,coverage:handoff.view.report.coverage,counts:handoff.view.report.counts}]:undefined}/>
    {handoffLoading&&<div className="notice">正在核对评审记录与服务端目标版本…</div>}
    {reviewId&&<p className="notice">已进入评审关联模式，目标从服务端解析并固定。更换版本须退出关联并重新比较。</p>}
    <div className="card details"><h2>发布 Review</h2><p>当前环境目标配置版本：{environment?.desiredConfigVersion??0}。创建及提交时服务端会再次核对资源修订与关联评审。</p>
     {!inScope&&<p className="notice">请选择此 API 所属组织、项目下的发布环境。</p>}
     {draft&&ready?<><p>已创建草稿：<Link to={'/releases/'+draft.id}>{draft.id}</Link></p>
      <Table rows={draft.frozenCandidate?.versions.map((v:{api:Api;version:Version})=>({id:v.version.id,api:v.api.name,version:v.version.version}))||[]} columns={[['api','API'],['version','版本'],['id','候选版本 ID']]}/><Details value={draft.frozenCandidate||'预览尚未读取，可进入草稿详情继续核对。'}/>
      <button className="btn primary" disabled={!mayRelease||!draft.frozenCandidate} onClick={()=>void submit()}>提交审批</button>
     </>:<button className="btn primary" disabled={!mayRelease} onClick={()=>void create()}>创建发布草稿并预览</button>}
     {error&&<div className="error" role="alert">{error}</div>}
    </div>
   </>}
   <div className="wizard-footer"><button className="btn" disabled={step===0||busy} onClick={()=>{if(allowNavigation())setStep(s=>s-1);}}>上一步</button>
    <span>{step+1} / 6</span><button className="btn primary" disabled={step===5||busy} onClick={()=>{if(allowNavigation())setStep(s=>s+1);}}>下一步</button>
   </div>
  </>}
 </>;
}
