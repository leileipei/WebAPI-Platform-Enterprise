import {useEffect,useRef,useState,type FormEvent} from 'react';
import {ApiError} from '../api/client';
import {useSession} from '../auth/SessionProvider';
import {tabFocusTarget,recoveryFocusTarget} from '../editor-focus.mjs';
import {useUnsaved} from '../ui';
import {notificationApi,type NotificationChannel} from './api';
import {createNotificationTestState,notificationTestState,notificationTestLabel,type NotificationTestState} from './settings-state.mjs';
export function NotificationTestDialog({channel,revision,onClose,onDenied,onStale}:{channel:NotificationChannel;revision:number;onClose:()=>void;onDenied:()=>void;onStale:()=>void}){
 const session=useSession(),authority=JSON.stringify([session.user?.id,session.user?.permissions,session.user?.scopes]);
 const[state,setState]=useState<NotificationTestState>(()=>createNotificationTestState(channel,revision,authority)),ref=useRef(state),root=useRef<HTMLFormElement>(null),controller=useRef(new AbortController()),[discard,setDiscard]=useState(false);ref.current=state;
 const callbacks=useRef({onClose,onDenied,onStale});callbacks.current={onClose,onDenied,onStale};
 const send=(event:Parameters<typeof notificationTestState>[1])=>{ref.current=notificationTestState(ref.current,event);setState(ref.current);};
 const pendingDraft=!!state.email&&!state.receipt;
 useUnsaved(pendingDraft,()=>send({type:'failure',epoch:ref.current.epoch,status:0,message:'仍有未提交的测试目标，请先发送或关闭弹窗。'}));
 useEffect(()=>{if(controller.current.signal.aborted)controller.current=new AbortController();const previous=document.activeElement as HTMLElement;root.current?.querySelector<HTMLElement>('input,button')?.focus();return()=>{controller.current.abort();ref.current=notificationTestState(ref.current,{type:'close'});if(previous?.isConnected)previous.focus();};},[]);
 useEffect(()=>{if(authority!==ref.current.authority||!session.can('system.manage')){send({type:'denied'});controller.current.abort();callbacks.current.onClose();}},[authority]);
 useEffect(()=>{if(revision!==ref.current.revision){send({type:'revision',revision});controller.current.abort();callbacks.current.onClose();}},[revision]);
 useEffect(()=>{const form=root.current;if(!form)return;const trap=form.querySelector<HTMLElement>('[role=alertdialog]')||form,items=[...trap.querySelectorAll<HTMLElement>('input:not(:disabled),button:not(:disabled)')].filter(item=>item.getClientRects().length>0);(discard?items[0]:recoveryFocusTarget(items,document.activeElement,form))?.focus();},[discard,state.busy,state.conflict,state.invalid,session.error]);
 useEffect(()=>{
  const receipt=state.receipt;if(!receipt||!['Queued','Sending','RetryScheduled','Paused'].includes(receipt.status))return;
  const polling=new AbortController(),epoch=state.epoch,savedRevision=state.revision;let timer:ReturnType<typeof setTimeout>|undefined;
  async function read(){if(polling.signal.aborted)return;if(document.hidden){timer=setTimeout(()=>void read(),5000);return;}
   try{const value=await notificationApi.readTest(receipt!.id,polling.signal);if(polling.signal.aborted)return;send({type:'receipt',epoch,revision:savedRevision,receipt:value});if(!['Queued','Sending','RetryScheduled','Paused'].includes(value.status))return;}
   catch(error){if(polling.signal.aborted)return;const status=error instanceof ApiError?error.status:0;send({type:'failure',epoch,status,message:(error as Error).message});if([401,403,404].includes(status)){controller.current.abort();callbacks.current.onDenied();return;}}
   timer=setTimeout(()=>void read(),5000);
  }
  timer=setTimeout(()=>void read(),1000);return()=>{polling.abort();if(timer)clearTimeout(timer);};
 },[state.receipt?.id,state.receipt?.status,state.epoch,state.revision]);
 async function submit(event:FormEvent){event.preventDefault();const current=ref.current;if(current.busy||current.invalid||current.conflict||current.receipt||session.error||!session.can('system.manage'))return;send({type:'begin',key:crypto.randomUUID()});const started=ref.current;
  try{const receipt=await notificationApi.createTest(channel,started.email,started.revision,started.key!,controller.current.signal);if(!controller.current.signal.aborted)send({type:'receipt',epoch:started.epoch,revision:started.revision,receipt});}
  catch(error){if(controller.current.signal.aborted)return;const status=error instanceof ApiError?error.status:0;send({type:'failure',epoch:started.epoch,status,message:(error as Error).message});if([401,403,404].includes(status))callbacks.current.onDenied();if(status===412)callbacks.current.onStale();}
 }
 function close(){if(state.busy)return;if(pendingDraft)setDiscard(true);else callbacks.current.onClose();}
 if(state.invalid||authority!==state.authority)return null;
 return <div className="overlay"><form ref={root} className="dialog notification-test-dialog" tabIndex={-1} role="dialog" aria-modal="true" aria-label={channel==='Email'?'发送 Email 测试':'发送 Webhook 测试'} onSubmit={submit} onKeyDown={event=>{if(event.key==='Escape'){event.preventDefault();if(discard)setDiscard(false);else close();}if(event.key==='Tab'){const trap=event.currentTarget.querySelector<HTMLElement>('[role=alertdialog]')||event.currentTarget,items=[...trap.querySelectorAll<HTMLElement>('input:not(:disabled),button:not(:disabled)')].filter(item=>item.getClientRects().length>0);const next=tabFocusTarget(items,document.activeElement,event.shiftKey,trap);if(next){event.preventDefault();next.focus();}}}}>
 <header><h2>{channel==='Email'?'发送 Email 测试':'发送 Webhook 测试'}</h2><button type="button" className="text-btn" disabled={state.busy} onClick={close}>关闭</button></header>
 <div className="notification-test-summary"><span className="badge blue">已保存修订 {revision}</span><span>单次尝试 · 五分钟期限 · 每用户每渠道每分钟一次</span></div>
 <p className="notice">测试会创建实际投递任务。自动开关关闭时仍可测试；关闭弹窗只停止查看，不撤回已排队或开始的投递。</p>
 <fieldset disabled={state.busy||!!state.receipt||state.conflict||discard||!!session.error}>{channel==='Email'?<label>测试收件邮箱<input type="email" required maxLength={254} autoComplete="off" value={state.email} onChange={event=>send({type:'email',email:event.target.value})}/><small>仅一个已获部署名单允许的邮箱，不接受显示名、Cc 或 Bcc。</small></label>:<p>使用已保存的 Webhook 目标与固定秘密版本；目标地址不能在测试中替换。</p>}</fieldset>
 {state.error&&<div className="error" role="alert">{state.error}</div>}{state.conflict&&<div className="notice">配置已被修改，测试目标已保留。请关闭弹窗，读取最新配置并重新核对修订。</div>}
 {state.receipt&&<section className="notification-receipt" role="status"><h3>{notificationTestLabel(state.receipt)}</h3><dl><dt>任务 ID</dt><dd>{state.receipt.id}</dd><dt>渠道 / 目标</dt><dd>{state.receipt.channel} / {state.receipt.maskedTarget}</dd><dt>实际尝试</dt><dd>{state.receipt.attemptCount} / {state.receipt.maxAttempts}</dd><dt>结果码</dt><dd>{state.receipt.reason||'等待 Worker'}</dd><dt>到期</dt><dd>{new Date(state.receipt.expiresAt).toLocaleString()}</dd></dl><p>SMTP / HTTP 接受仅代表服务已接受，不能证明阅读或收件方业务完成。断连或超时可能已被接受，请先核对收件端。</p></section>}
 {discard&&<div className="notice" role="alertdialog" aria-label="放弃测试目标"><p>关闭将放弃尚未提交的测试邮箱。</p><button type="button" className="btn" onClick={()=>setDiscard(false)}>继续编辑</button><button type="button" className="btn danger" onClick={onClose}>确认放弃</button></div>}
 <footer><button className="btn primary" disabled={state.busy||!!state.receipt||state.conflict||discard||!!session.error}>{state.busy?'创建测试任务…':'确认发送测试'}</button><span>仅使用已保存配置 · 不改变自动启用状态</span></footer>
 </form></div>;
}
