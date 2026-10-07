import {useEffect,useRef,useState} from 'react';
import type {Scope} from '../api/types';
import {ApiError} from '../api/client';
import {useSession} from '../auth/SessionProvider';
import {notificationApi,type NotificationDelivery} from './api';
import {createDeliveryState,deliveryActionState,mayRetryDelivery,notificationOutcomeLabel,silenceBoundary,type DeliveryState} from './delivery-state.mjs';
import {notificationTestLabel} from './settings-state.mjs';
export function NotificationDeliveryList({eventId,scope}:{eventId:string;scope:Scope}){
 const session=useSession(),authority=JSON.stringify([session.user?.id,session.user?.permissions,session.user?.scopes]),identity=JSON.stringify([eventId,scope,authority]);
 const[state,setState]=useState<DeliveryState>(()=>createDeliveryState(identity)),ref=useRef(state),[loading,setLoading]=useState(false),[tick,setTick]=useState(0),action=useRef<AbortController|undefined>(undefined);ref.current=state;
 const send=(event:Parameters<typeof deliveryActionState>[1])=>{ref.current=deliveryActionState(ref.current,event);setState(ref.current);};
 useEffect(()=>{action.current?.abort();send({type:session.can('alert.read',scope)?'scope':'denied',identity});return()=>{action.current?.abort();ref.current=deliveryActionState(ref.current,{type:'denied'});};},[identity]);
 useEffect(()=>{
  if(state.identity!==identity||state.invalid||!session.can('alert.read',scope))return;
  const controller=new AbortController(),epoch=state.epoch;let timer:ReturnType<typeof setTimeout>|undefined;
  async function read(){if(controller.signal.aborted)return;if(document.hidden){timer=setTimeout(()=>void read(),5000);return;}setLoading(true);
   try{const result=await notificationApi.deliveries(eventId,state.page,controller.signal);if(!controller.signal.aborted)send({type:'list',epoch,result});}
   catch(error){if(!controller.signal.aborted)send({type:'failure',epoch,status:error instanceof ApiError?error.status:0,message:(error as Error).message});}
   finally{if(!controller.signal.aborted){setLoading(false);timer=setTimeout(()=>void read(),5000);}}
  }
  void read();return()=>{controller.abort();if(timer)clearTimeout(timer);};
 },[identity,state.identity,state.invalid,state.page,state.epoch,tick]);
 useEffect(()=>{
  if(state.identity!==identity||state.invalid||!state.selected)return;
  const controller=new AbortController(),epoch=state.epoch;
  void notificationApi.attempts(state.selected.id,state.attemptPage,controller.signal).then(result=>{if(!controller.signal.aborted)send({type:'attempts',epoch,result});}).catch(error=>{if(!controller.signal.aborted)send({type:'failure',epoch,status:error instanceof ApiError?error.status:0,message:(error as Error).message});});
  return()=>controller.abort();
 },[identity,state.selected?.id,state.selected?.revision,state.attemptPage,state.epoch,state.invalid,tick]);
 async function retry(row:NotificationDelivery){if(!mayRetryDelivery(row,session.can('alert.operate',scope,true),ref.current.busy,ref.current.conflict||!!ref.current.error))return;const controller=new AbortController();action.current?.abort();action.current=controller;send({type:'begin',key:crypto.randomUUID()});const current=ref.current;
  try{const result=await notificationApi.retry(row.id,row.revision,current.key!,controller.signal);if(!controller.signal.aborted)send({type:'retried',epoch:current.epoch,row:result});}
  catch(error){if(!controller.signal.aborted)send({type:'failure',epoch:current.epoch,status:error instanceof ApiError?error.status:0,message:(error as Error).message});}
 }
 if(state.identity!==identity)return <p role="status">正在读取当前事件投递记录…</p>;
 if(state.invalid||!session.can('alert.read',scope))return <div className="notice" role="alert">读取权限或资源已失效，投递记录已清空。</div>;
 return <section className="notification-delivery-list" aria-label="外部通知投递记录"><header><div><h3>外部通知投递记录</h3><small>每目标独立任务 · 掩码目标 · 每五秒核对服务端回执</small></div><button type="button" className="btn" disabled={loading||state.busy} onClick={()=>setTick(x=>x+1)}>刷新回执</button></header>
 <p className="notice">Accepted 仅代表服务接受。静默只停止未开始或后续尝试；已开始的发送可能完成。不确定结果可能已被远端接受，重试可能重复。</p>
 {state.error&&<div className="error" role="alert">{state.error}</div>}{state.conflict&&<p className="notice">任务修订或资格已变化，请刷新回执后重新核对；原任务次数、目标及截止时间保留。</p>}
 <div className="table-wrap"><table><thead><tr><th>渠道 / 目标</th><th>状态 / 原因</th><th>实际尝试</th><th>下一次 / 到期</th><th>操作</th></tr></thead><tbody>{state.items.map(row=><tr key={row.id}><td>{row.channel}<small className="block mono">{row.maskedTarget}</small></td><td><span className={'badge '+(row.status==='Accepted'?'green':['Failed','Expired','Suppressed'].includes(row.status)?'red':'amber')}>{row.status}</span><small className="block">{row.reason||'等待实际回执'}</small></td><td>{row.attemptCount} / {row.maxAttempts}</td><td><small className="block">{row.nextAttemptAt?new Date(row.nextAttemptAt).toLocaleString():'未安排下一次'}</small><small className="block muted">到期 {new Date(row.expiresAt).toLocaleString()}</small></td><td><div className="actions"><button type="button" className="text-btn" onClick={()=>send({type:'select',row})}>查看尝试</button><button type="button" className="text-btn" disabled={!mayRetryDelivery(row,session.can('alert.operate',scope,true),state.busy,state.conflict||!!state.error)} onClick={()=>void retry(row)}>重试原任务</button></div></td></tr>)}</tbody></table>{!state.items.length&&<div className="empty" role="status">{loading?'正在查询投递事实…':'没有外部任务。旧事件不补发，未启用的外部策略不创建投递。'}</div>}</div>
 <div className="pagination"><span>共 {state.total} 条 · 每页 {state.pageSize} 条</span><div><button type="button" disabled={loading||state.busy||state.page<=1} onClick={()=>send({type:'page',page:state.page-1})}>上一页</button><span>{state.page}</span><button type="button" disabled={loading||state.busy||state.page*state.pageSize>=state.total} onClick={()=>send({type:'page',page:state.page+1})}>下一页</button></div></div>
 {state.selected&&<section className="notification-attempts"><h4>{state.selected.channel} · {state.selected.maskedTarget}</h4><p>{notificationTestLabel(state.selected)}</p><small>{silenceBoundary(state.selected.status)}{state.selected.nextAttemptAt&&` 人工重试保留原下界 ${new Date(state.selected.nextAttemptAt).toLocaleString()}，不会立即提前发送。`}</small><div className="table-wrap"><table><thead><tr><th>尝试</th><th>开始 / 结束</th><th>结果分类</th><th>安全结果码</th><th>协议码</th></tr></thead><tbody>{state.attempts.map(attempt=><tr key={attempt.attemptNo}><td>{attempt.attemptNo}</td><td><small className="block">{new Date(attempt.startedAt).toLocaleString()}</small><small className="block">{attempt.completedAt?new Date(attempt.completedAt).toLocaleString():'进行中'}</small></td><td>{notificationOutcomeLabel(attempt.outcome)}</td><td>{attempt.code||'—'}</td><td>{attempt.protocolStatus??'—'}</td></tr>)}</tbody></table>{!state.attempts.length&&<p className="empty">尚无本页的实际尝试记录</p>}</div><div className="pagination"><span>共 {state.attemptTotal} 次尝试</span><div><button type="button" disabled={state.attemptPage<=1} onClick={()=>send({type:'attemptPage',page:state.attemptPage-1})}>上一页尝试</button><span>{state.attemptPage}</span><button type="button" disabled={state.attemptPage*20>=state.attemptTotal} onClick={()=>send({type:'attemptPage',page:state.attemptPage+1})}>下一页尝试</button></div></div></section>}
 </section>;
}
