import {useState} from 'react';
import type {NotificationIntent,NotificationRetryPolicy} from '../api/alerts';
import type {NotificationLimits} from './api';
import {notificationIntent} from './delivery-state.mjs';
export function RuleNotificationEditor({value,onChange,readOnly=false,limits,limitsError=''}:{value:NotificationIntent;onChange:(value:NotificationIntent)=>void;readOnly?:boolean;limits?:NotificationLimits;limitsError?:string}){
 const policy=notificationIntent(value),[emailDraft,setEmailDraft]=useState(()=>policy.emailRecipients.join('\n'));
 const budgets:[keyof NotificationRetryPolicy,string,number,number][]=[['maxAttempts','最大实际尝试次数',1,5],['baseDelaySeconds','基础退避 / 秒',1,300],['maxDelaySeconds','最大退避 / 秒',policy.retryPolicy.baseDelaySeconds,3600],['expiresAfterMinutes','任务期限 / 分钟',5,1440]];
 const availability=(channel:'Email'|'Webhook')=>{if(!limits)return '正在核对渠道状态';const configured=channel==='Email'?limits.emailConfigured:limits.webhookConfigured,enabled=channel==='Email'?limits.emailEnabled:limits.webhookEnabled;return !configured?'渠道未配置':enabled?'自动渠道已启用':'渠道未启用 · 新事件保留抑制回执';};
 return <section className="rule-notification-policy" aria-label="规则外部通知策略"><header><strong>通知策略</strong><span className="badge blue">控制台告警始终启用</span></header><p>原渠道意向保留，外部通知须明确开启。仅影响后续新告警 occurrence，历史事件的目标与预算保持冻结。</p>
 <fieldset disabled={readOnly}><label className="checkline"><input type="checkbox" checked={policy.externalEnabled} onChange={e=>onChange({...policy,externalEnabled:e.target.checked})}/>启用本规则的外部通知</label><div className="rule-notification-channels">{['Email','Webhook','EnterpriseIm'].map(channel=><label className="checkline" key={channel}><input type="checkbox" checked={policy.requestedChannels.includes(channel)} onChange={e=>onChange({...policy,requestedChannels:e.target.checked?[...policy.requestedChannels,channel]:policy.requestedChannels.filter(x=>x!==channel)})}/><span>{channel==='EnterpriseIm'?'企业 IM · 待接入':channel}<small className="block">{channel==='EnterpriseIm'?'仅保留意向，不作为有效外部渠道':availability(channel as 'Email'|'Webhook')}</small></span></label>)}</div>
 {policy.requestedChannels.includes('Email')&&<label className="rule-recipients">Email 收件人 · {policy.emailRecipients.length} / {limits?.maxRecipients??20}<textarea rows={4} value={emailDraft} onChange={e=>{setEmailDraft(e.target.value);onChange({...policy,emailRecipients:e.target.value.split(/\r?\n/).map(x=>x.trim()).filter(Boolean)});}} placeholder="每行一个邮箱，如 owner@example.com"/><small>每项是独立投递目标，不群发。邮箱及域名须命中部署名单；保存时由服务端校验，不接受显示名、Cc / Bcc。</small></label>}
 <label className="checkline"><input type="checkbox" checked={policy.notifyRecovery} onChange={e=>onChange({...policy,notifyRecovery:e.target.checked})}/>发送配对的解决通知</label><small>仅对可能已收到触发消息的原目标配对；人工解决表示人工关闭，静默时解决保留抑制回执。</small>
 <div className="rule-notification-budgets">{budgets.map(([key,title,min,max])=><label key={key}>{title}<input type="number" min={min} max={max} step={1} required value={policy.retryPolicy[key]} onChange={e=>onChange({...policy,retryPolicy:{...policy.retryPolicy,[key]:Number(e.target.value)}})}/><small>{min}–{max}{key==='maxAttempts'?'，包含首次发送':''}</small></label>)}</div></fieldset>
 {limitsError&&<div className="error" role="alert">渠道状态读取失败：{limitsError}</div>}
 <p className="notice">次数和截止时间不会因人工重试重置；Webhook Retry-After 不会被缩短。仅修改通知策略不重置告警持续计时。“只读测试”只验证指标条件，不发送通知。</p>
 </section>;
}
