import {useEffect,useRef,useState,type FormEvent} from 'react';
import {ApiError} from '../api/client';
import {useSession} from '../auth/SessionProvider';
import {tabFocusTarget,recoveryFocusTarget} from '../editor-focus.mjs';
import {useUnsaved} from '../ui';
import {approvalApi} from './api';
import {approvalOperation,approvalOperationMatches,type ApprovalDialog,type ApprovalDialogEvent,type ApprovalSubject} from './inbox-state.mjs';

type Props={
 dialog:ApprovalDialog;
 routeReleaseId?:string;
 currentDetail?:ApprovalSubject;
 dispatch:(event:ApprovalDialogEvent)=>void;
 onLostRead:(error:ApiError)=>void;
 onResult:()=>void;
 onClose:()=>void;
};

export function ApprovalActionDialog({dialog,routeReleaseId,currentDetail,dispatch,onLostRead,onResult,onClose}:Props){
 const {item,action,comment,key,error,conflict}=dialog,session=useSession();
 const opened=useRef(approvalOperation(item));
 // Preserve the opened URL for reads and writes. The API returns its canonical release ID in the snapshot.
 const endpointReleaseId=useRef(routeReleaseId??item.id).current;
 const controller=useRef<AbortController|null>(null),submitting=useRef(false);
 const [verified,setVerified]=useState<ApprovalSubject>(),[stale,setStale]=useState(false);
 const [busy,setBusy]=useState(false),[discard,setDiscard]=useState(false),[loading,setLoading]=useState(true);
 const root=useRef<HTMLFormElement>(null),fromDetail=currentDetail!==undefined;
 const detailChanged=fromDetail&&!approvalOperationMatches(opened.current,currentDetail);
 const canSubmit=!busy&&!loading&&!discard&&!conflict&&!stale&&!detailChanged&&!session.error&&!!key&&
   approvalOperationMatches(opened.current,verified)&&(action!=='reject'||!!comment.trim());

 useUnsaved(!!comment||busy,()=>dispatch({type:'action-error',status:0,message:'仍有待处理的审批，请先完成提交或关闭弹窗。'}));
 useEffect(()=>{
  const previous=document.activeElement as HTMLElement;
  root.current?.querySelector<HTMLTextAreaElement>('textarea')?.focus();
  return()=>{if(previous?.isConnected)previous.focus();};
 },[]);
 useEffect(()=>{
  const form=root.current;
  if(!form)return;
  const trap=form.querySelector<HTMLElement>('[role=alertdialog]')||form;
  const items=[...trap.querySelectorAll<HTMLElement>('textarea:not(:disabled),button:not(:disabled)')].filter(item=>item.getClientRects().length>0);
  const next=discard?items[0]:recoveryFocusTarget(items,document.activeElement,form);
  next?.focus();
 },[discard,busy,canSubmit]);
 useEffect(()=>{if(detailChanged)setStale(true);},[detailChanged]);

 function observe(result:ApprovalSubject){
  const expected=opened.current;
  // Inbox rows omit the hash. Only their first detail read for the original release and step may supply it.
  if(!fromDetail&&expected.candidateHash===null&&expected.releaseId===result.id&&expected.stepOrder===result.approvalEligibility?.currentStepOrder){
   expected.candidateHash=result.candidateHash??null;
  }
  if(!approvalOperationMatches(expected,result))setStale(true);
  setVerified(result);
 }
 useEffect(()=>{
  const abort=new AbortController();
  controller.current=abort;
  let reading=false,readSucceeded=false;
  const read=async()=>{
   if(document.hidden||reading)return;
   reading=true;
   try{
    const result=await approvalApi.read(endpointReleaseId,abort.signal);
    if(!abort.signal.aborted){observe(result);readSucceeded=true;}
   }catch(e){
    if(abort.signal.aborted)return;
    readSucceeded=false;
    setVerified(undefined);
    if(e instanceof ApiError&&[401,403,404].includes(e.status))onLostRead(e);
    else dispatch({type:'action-error',status:e instanceof ApiError?e.status:0,message:(e as Error).message});
   }finally{reading=false;if(!abort.signal.aborted)setLoading(false);}
  };
  void read();
  // Release detail already polls the same resource while this dialog is open.
  const timer=setInterval(()=>{if(!fromDetail||!readSucceeded)void read();},5000);
  document.addEventListener('visibilitychange',read);
  return()=>{abort.abort();clearInterval(timer);document.removeEventListener('visibilitychange',read);};
 },[endpointReleaseId,fromDetail]);

 async function submit(e:FormEvent){
  e.preventDefault();
  const signal=controller.current?.signal,expected=opened.current;
  if(!canSubmit||submitting.current||!signal||signal.aborted||expected.stepOrder===null||expected.candidateHash===null)return;
  submitting.current=true;
  setBusy(true);
  try{
   await approvalApi.act(endpointReleaseId,action,comment,key,signal,{stepOrder:expected.stepOrder,candidateHash:expected.candidateHash});
   if(!signal.aborted)onResult();
  }catch(e){
   if(signal.aborted)return;
   const status=e instanceof ApiError?e.status:0;
   dispatch({type:'action-error',status,message:(e as Error).message});
   if([403,404].includes(status)){
    try{const result=await approvalApi.read(endpointReleaseId,signal);if(!signal.aborted)observe(result);}
    catch(read){if(!signal.aborted&&read instanceof ApiError&&[401,403,404].includes(read.status))onLostRead(read);}
   }
  }finally{submitting.current=false;if(!signal.aborted)setBusy(false);}
 }
 const title=action==='approve'?'批准当前步骤':'拒绝审批申请',risk=item.risk;
 return <div className="overlay"><form ref={root} className="dialog approval-dialog" role="dialog" tabIndex={-1} aria-modal="true" aria-label={title} onSubmit={submit} onKeyDown={e=>{
  if(e.key==='Escape'){e.preventDefault();if(!busy){if(discard)setDiscard(false);else if(comment)setDiscard(true);else onClose();}}
  if(e.key==='Tab'){
   const trap=root.current?.querySelector<HTMLElement>('[role=alertdialog]')||root.current;
   if(!trap)return;
   const items=[...trap.querySelectorAll<HTMLElement>('textarea:not(:disabled),button:not(:disabled),a[href]')].filter(item=>item.getClientRects().length>0);
   const next=tabFocusTarget(items,document.activeElement,e.shiftKey,trap);
   if(next){e.preventDefault();next.focus();}
  }
 }}>
  <header><h2>{title}</h2><button type="button" className="text-btn" disabled={busy} onClick={()=>comment?setDiscard(true):onClose()}>关闭</button></header>
  <div className="approval-summary">
   <strong>{item.releaseNo}</strong>
   {item.organization&&item.project&&item.environment&&<span>{item.organization.name} / {item.project.name} / {item.environment.name}</span>}
   <span>申请人 {item.applicantDisplayName||item.requestedBy} · 本次审批步骤 {opened.current.stepOrder??'—'}</span>
   <span>冻结候选 <code title={opened.current.candidateHash??undefined}>{opened.current.candidateHash?.slice(0,16)||'读取中…'}</code></span>
   {risk&&<span>{risk.visibility==='Restricted'?'风险资料受限':risk.counts?`破坏性 ${risk.counts.breaking} · 未知 ${risk.counts.unknown}`:'未关联风险评审'}</span>}
  </div>
  <label>审批意见{action==='reject'?' *':''}<textarea rows={5} maxLength={10000} value={comment} required={action==='reject'} disabled={busy||discard}
   onChange={e=>dispatch({type:'comment',comment:e.target.value,key:crypto.randomUUID()})}/><small>{comment.length} / 10000</small></label>
  {loading&&<p role="status">正在重新核对服务端资格…</p>}
  {error&&<div className="error" role="alert">{error}</div>}
  {(conflict||stale||detailChanged)&&<div className="notice">审批步骤、冻结候选或办理资格已变化。批注已保留，请关闭后重新核对。</div>}
  {discard&&<div className="notice" role="alertdialog" aria-label="放弃未提交批注"><p>关闭将放弃未提交批注。</p><button type="button" className="btn" onClick={()=>setDiscard(false)}>继续编辑</button><button type="button" className="btn danger" onClick={onClose}>确认放弃</button></div>}
  <footer><button className={'btn '+(action==='approve'?'primary':'danger')} disabled={!canSubmit}>{busy?'提交中…':action==='approve'?'确认批准':'确认拒绝'}</button><span>按冻结规则占用当前步骤的一个席位</span></footer>
 </form></div>;
}
