import {credentialCreateResult} from '../applications/credential-create-result.mjs';
import {FormalCredentialRotation, writeFormalRotationLocation} from '../applications/FormalCredentialRotation';
import {formalRotationScope, rotationGuid, type RotationCredential} from '../applications/formal-rotation-state.mjs';
import {useFormalRotationEligibility} from '../applications/useFormalRotationEligibility';
import {useEffect,useReducer,useRef,useState} from 'react';import {useSession} from '../auth/SessionProvider';import {useWorkspace,allowNavigation} from '../Shell';import {apiRequest,ApiError} from '../api/client';import {Editor,Feedback,Pagination,useRemote,useUnsaved,type Field} from '../ui';import type {CredentialUsagePage} from '../api/types';import {credentialActors,credentialInstantTicks,credentialWindowState,usageCountText,visibleCredentialLastUsed} from '../applications/application-state.mjs';import {maskAccessKey} from '../applications/rotation-state.mjs';import {UsageNotice,usageTime} from '../applications/ApplicationOverview';import {CredentialImpactDialog} from '../applications/CredentialImpactDialog';import {CredentialRotationWizard} from '../applications/CredentialRotationWizard';import {tabFocusTarget} from '../editor-focus.mjs';
type ImpactPending={credential:any;request:any;etag:string;key:string;resolve:()=>void;reject:(error:unknown)=>void};
function SecretOnce({secret,close}:{secret:string;close:()=>void}){
 const root=useRef<HTMLDivElement>(null);useUnsaved(true);useEffect(()=>{const previous=document.activeElement as HTMLElement;root.current?.querySelector('input')?.focus();return()=>{if(previous?.isConnected)previous.focus();};},[]);
 return <div className="overlay"><div ref={root} className="dialog" role="dialog" aria-modal="true" aria-label="一次性凭证" tabIndex={-1} onKeyDown={e=>{if(e.key==='Escape'){e.preventDefault();close();}if(e.key==='Tab'){const items=[...e.currentTarget.querySelectorAll<HTMLElement>('button,input')];const next=tabFocusTarget(items,document.activeElement,e.shiftKey,e.currentTarget);if(next){e.preventDefault();next.focus();}}}}><h2>请保存 API Key</h2><p className="notice">关闭后无法再次读取完整 Secret。不要将凭证放入链接或截图。</p><input aria-label="一次性 API Key" value={secret} readOnly onFocus={e=>e.currentTarget.select()}/><footer><button className="btn primary" onClick={close}>我已保存，关闭</button></footer></div></div>;
}
const credentialStates = {
  Pending:{label:'待生效',tone:'amber'}, Active:{label:'有效期内',tone:'green'}, Expired:{label:'已过期',tone:'red'},
  Revoked:{label:'已撤销',tone:'red'}, Unknown:{label:'状态未知',tone:'amber'}
};
function CredentialTime({value}:{value?:string|null}) {
  if (typeof value !== 'string' || credentialInstantTicks(value) === null) return <>未知</>;
  return <time dateTime={value} title={value}>{new Date(value).toLocaleString('zh-CN',{hour12:false})}</time>;
}
function local(value:string) {
  if (credentialInstantTicks(value) === null) return '';
  const date = new Date(value);
  return new Date(+date - date.getTimezoneOffset() * 60000).toISOString().slice(0,16);
}
export function Credentials({application,rows,reload}:{application:any;rows:any[];reload:()=>void}){
 const ws=useWorkspace(),{can}=useSession(),scope={organizationId:application.organizationId,projectId:application.projectId},allowed=can('credential.manage',scope,true),auditAllowed=can('audit.read',scope),[edit,setEdit]=useState<any>(),[secret,setSecret]=useState<string>(),[receiptNotice,setReceiptNotice]=useState(false),[impact,setImpact]=useState<ImpactPending>(),pending=useRef<ImpactPending|undefined>(undefined),created=useRef<((id:string)=>void)|undefined>(undefined),[rotation,setRotation]=useState<string|undefined>(()=>{const id=new URLSearchParams(location.search).get('rotationOld');return rows.some(r=>r.id===id)?id||undefined:undefined;}),[environment,setEnvironment]=useState('all'),[window,setWindow]=useState('24h'),[page,setPage]=useState(1);
 const currentHour=new Date();currentHour.setUTCMinutes(0,0,0);const from=new Date(+currentHour-(window==='7d'?7*86400000:window==='current'?0:86400000)),to=new Date(+currentHour+(window==='current'?3600000:0));
 const query=new URLSearchParams({from:from.toISOString(),to:to.toISOString(),page:String(page),pageSize:'50'});if(environment!=='all')query.set('environmentId',environment);
 const stats=useRemote<CredentialUsagePage>(can('app.read',scope)?`/applications/${application.id}/credential-usage?${query}`:null,15000),audit=useRemote<any>(auditAllowed?`/applications/${application.id}/audit?page=1&pageSize=100`:null);
 const environments=ws.tree.environments.filter(e=>ws.tree.projects.some(p=>p.id===e.projectId&&p.organizationId===application.organizationId&&(!application.projectId||p.id===application.projectId))&&can('app.read',{organizationId:application.organizationId,projectId:e.projectId,environmentId:e.id}));
 const [formalOldId,setFormalOldId]=useState(()=>rotationGuid(new URLSearchParams(location.search).get('formalRotationOld')));
 const hasFormalScope=rows.some(row=>formalRotationScope(application,row,ws.tree,can));
 const formalEligibility=useFormalRotationEligibility(hasFormalScope?application.id:null);
 const allowedFormalScope=(row:RotationCredential|undefined)=>row&&formalEligibility.credentialIds?.has(row.id)?formalRotationScope(application,row,ws.tree,can):null;
 const formalOld=rows.find(row=>row.id===formalOldId),formalScope=allowedFormalScope(formalOld);
 const formalParams=new URLSearchParams(location.search);
 const formalInitialId=rotationGuid(formalParams.get('formalRotationOld'))===formalOldId?rotationGuid(formalParams.get('formalRotationId')):null;
 const usage=stats.error?undefined:stats.data,credentialRows=usage?usage.page.items.map(item=>({key:rows.find(r=>r.id===item.credentialId),usage:item})).filter(x=>x.key):rows.slice((page-1)*50,page*50).map(key=>({key,usage:undefined}));
 const [clockRevision, refreshClock] = useReducer((revision:number) => revision + 1, 0);
 const now = Date.now();
 const displayed = credentialRows.map(row => ({...row, window:credentialWindowState(row.key, now)}));
 const nextChangeAt = displayed.reduce((next,row) => Math.min(next,row.window.nextChangeAt ?? Infinity), Infinity);
 useEffect(() => {
   const delay = Math.max(0, nextChangeAt - Date.now());
   const timer = Number.isFinite(delay) ? setTimeout(refreshClock, Math.min(delay, 2_147_483_647)) : undefined;
   document.addEventListener('visibilitychange', refreshClock);
   globalThis.addEventListener('focus', refreshClock);
   return () => {
     clearTimeout(timer);
     document.removeEventListener('visibilitychange', refreshClock);
     globalThis.removeEventListener('focus', refreshClock);
   };
 }, [nextChangeAt,clockRevision]);
 const fields:Field[]=[{key:'validFrom',label:'生效时间',type:'datetime-local',required:true},{key:'expiresAt',label:'失效时间',type:'datetime-local',required:true},...(edit?.id?[{key:'status',label:'状态',options:['Active','Revoked'].map(value=>({value,label:value}))}]:[])];
 useEffect(()=>()=>{pending.current?.reject(new Error('凭证编辑已离开，未自动重放提交。'));pending.current=undefined;},[]);
 function finish(error?:unknown){const value=pending.current;pending.current=undefined;setImpact(undefined);if(error)value?.reject(error);else value?.resolve();}
 function startCreate(receive?:((id:string)=>void)){setReceiptNotice(false);created.current=receive;setEdit({validFrom:local(new Date().toISOString()),expiresAt:local(new Date(Date.now()+30*86400000).toISOString())});}
 function startEdit(value:any){created.current=undefined;setEdit({...value,validFrom:local(value.validFrom),expiresAt:local(value.expiresAt)});}
 function closeFormal(){writeFormalRotationLocation(null);setFormalOldId(null);}
 function openOrdinary(id:string){if(formalOldId){if(!allowNavigation())return;closeFormal();}setRotation(id);}
 return <>{receiptNotice&&<div className="notice" role="status">凭证已创建，本次重试只返回记录，完整 Secret 无法再次获取。若首次响应未保存，请核对凭证列表，按现有撤销流程处理后再明确创建新凭证。</div>}<div className="filter-bar"><button className="btn primary" disabled={!allowed} onClick={()=>startCreate()}>＋ 创建凭证</button><span>Secret 仅创建成功时展示一次。表格只显示 AK 后四位。</span><button className="btn" onClick={()=>{formalEligibility.refreshEligibility();reload();stats.reload();audit.reload();}}>刷新</button></div><div className="filter-bar"><label>统计环境<select aria-label="凭证统计环境" value={environment} onChange={e=>{if(allowNavigation()){setEnvironment(e.target.value);setPage(1);}}}><option value="all">可见环境合计</option>{environments.map(e=><option key={e.id} value={e.id}>{e.name}</option>)}</select></label><label>窗口<select aria-label="凭证统计窗口" value={window} onChange={e=>{if(allowNavigation()){setWindow(e.target.value);setPage(1);}}}><option value="24h">过去 24 个完整小时</option><option value="7d">过去 7 天完整小时</option><option value="current">当前小时 · 进行中</option></select></label></div><Feedback {...stats}/>{usage&&<UsageNotice coverage={usage.coverage}/>}<div className="table-wrap"><table className="wp4-credential-table"><thead><tr><th>Access Key</th><th>工作配置状态<small className="wp4-subline">按浏览器时间</small></th><th>有效窗口</th><th>最近已知使用</th><th>窗口认证次数</th><th>可信操作人</th><th>操作</th></tr></thead><tbody>{displayed.map(({key:r,usage:item,window})=>{
 const actor=credentialActors(application.id,r.id,audit.data?.items);
 const {label,tone}=credentialStates[window.state];
 return <tr key={r.id}><td><code>{maskAccessKey(r.accessKey)}</code><small className="wp4-subline">修订 {r.revision}</small></td><td><span className={'badge ' + tone}>{label}</span></td>
 <td><CredentialTime value={r.validFrom}/><small className="wp4-subline">至 <CredentialTime value={r.expiresAt}/></small></td><td>{usageTime(visibleCredentialLastUsed(item))}</td><td>{usageCountText(item?.count,item?.coverage.state||'Unknown')}<small className="wp4-subline">{environment==='all'?'可见环境合计':'所选环境'}</small></td><td><small>创建：{auditAllowed?actor.createdBy?.slice(0,8)||'当前审计页未记录':'无审计读取权限'}</small><small className="wp4-subline">撤销：{auditAllowed?actor.revokedBy?.slice(0,8)||'当前审计页未记录':'无审计读取权限'}</small></td><td><button disabled={!allowed} className="text-btn" onClick={()=>startEdit(r)}>有效期 / 撤销</button><button disabled={!allowed||r.status==='Revoked'} className="text-btn" onClick={()=>openOrdinary(r.id)}>轮换</button>
 {allowedFormalScope(r)&&<button className="text-btn" onClick={()=>{if(allowNavigation())setFormalOldId(r.id);}}>正式轮换</button>}</td></tr>;})}</tbody></table>{!displayed.length&&<div className="empty">当前页暂无凭证</div>}</div><Pagination remote={{data:usage?.page||{total:rows.length,pageSize:50},page,setPage,loading:stats.loading}}/>{formalOld&&formalScope&&<FormalCredentialRotation key={formalOld.id} old={formalOld} credentials={rows}
 environmentName={ws.tree.environments.find(e=>e.id===formalScope.environmentId)?.name??formalOld.environmentId}
 initialRotationId={formalInitialId} onClose={closeFormal} onAccessDenied={()=>{closeFormal();formalEligibility.refreshEligibility();}} onChanged={()=>{reload();stats.reload();audit.reload();}}/>}
 {rotation&&!formalScope&&<CredentialRotationWizard key={rotation} application={application} credentials={rows} oldId={rotation} onClose={()=>setRotation(undefined)} onCreate={receive=>startCreate(receive)} onRevoke={startEdit}/>}{edit&&<Editor title={edit.id?'编辑凭证窗口':'创建凭证'} fields={fields} initial={edit} allowed={allowed} close={()=>{setEdit(undefined);created.current=undefined;}} loadLatest={edit.id?()=>apiRequest(`/credentials/${edit.id}`):undefined} save={async(value,key,fresh)=>{const body={validFrom:value.validFrom,expiresAt:value.expiresAt,status:value.status};if(edit.id){const etag=`"${fresh?.revision??edit.revision}"`;try{await apiRequest(`/credentials/${edit.id}`,{method:'PUT',body,etag,idempotencyKey:key});}catch(error){if(!(error instanceof ApiError)||error.status!==409||!['credential_impact_confirmation_required','credential_impact_changed'].includes(error.code||''))throw error;await new Promise<void>((resolve,reject)=>{const request={credential:edit,request:body,etag,key,resolve,reject};pending.current=request;setImpact(request);});}}else{const result=await apiRequest<any>(`/applications/${application.id}/credentials`,{method:'POST',body:{validFrom:value.validFrom,expiresAt:value.expiresAt},idempotencyKey:key});const outcome=credentialCreateResult(result);setSecret(outcome.apiKey??undefined);setReceiptNotice(outcome.secretUnavailable);created.current?.(outcome.credentialId);}reload();stats.reload();audit.reload();}}/>}{impact&&<CredentialImpactDialog credential={impact.credential} request={impact.request} etag={impact.etag} idempotencyKey={impact.key} allowed={allowed} onDone={()=>finish()} onCancel={()=>finish(new Error('已取消风险确认，编辑输入仍保留。'))} onFatal={finish}/>} {secret&&<SecretOnce secret={secret} close={()=>setSecret(undefined)}/>}</>;
}
