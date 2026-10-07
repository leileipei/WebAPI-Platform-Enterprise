export const defaultNotificationRetry={maxAttempts:5,baseDelaySeconds:30,maxDelaySeconds:900,expiresAfterMinutes:1440};
export function notificationIntent(value={}){return {inConsole:true,requestedChannels:[...(value.requestedChannels??[])],externalEnabled:value.externalEnabled??false,emailRecipients:[...(value.emailRecipients??[])],notifyRecovery:value.notifyRecovery??true,retryPolicy:{...defaultNotificationRetry,...value.retryPolicy}};}
export function validateNotificationIntent(value){
 const policy=notificationIntent(value),retry=policy.retryPolicy;
 if(!Number.isInteger(retry.maxAttempts)||retry.maxAttempts<1||retry.maxAttempts>5||!Number.isInteger(retry.baseDelaySeconds)||retry.baseDelaySeconds<1||retry.baseDelaySeconds>300||!Number.isInteger(retry.maxDelaySeconds)||retry.maxDelaySeconds<retry.baseDelaySeconds||retry.maxDelaySeconds>3600||!Number.isInteger(retry.expiresAfterMinutes)||retry.expiresAfterMinutes<5||retry.expiresAfterMinutes>1440)return '重试预算：1–5次，基础1–300秒，上限不低于基础且不超过3600秒，期限5–1440分钟。';
 if(policy.emailRecipients.length>20)return '最多20个不同邮箱，每项是独立投递目标。';
 const seen=new Set();for(const email of policy.emailRecipients){if(typeof email!=='string'||email.length>254||!/^\S+@\S+$/.test(email)||/[<>,;\r\n]/.test(email))return '请按每行一个合法邮箱填写，禁止显示名、地址拼接和头部注入。';const at=email.lastIndexOf('@'),key=email.slice(0,at)+'@'+email.slice(at+1).toLowerCase();if(seen.has(key))return '邮箱存在重复，请保留一个目标。';seen.add(key);}
 if(policy.externalEnabled&&!policy.requestedChannels.some(x=>['Email','Webhook'].includes(x)))return '外部通知至少选择 Email 或 Webhook；企业 IM 保留意向。';
 if(policy.externalEnabled&&policy.requestedChannels.includes('Email')&&!policy.emailRecipients.length)return '启用 Email 外部通知时至少填写一个获部署名单允许的邮箱。';return '';
}
export function mayRetryDelivery(row,authorized,busy=false,conflict=false,now=Date.now()){return !!row&&authorized&&!busy&&!conflict&&row.canRetry===true&&['RetryScheduled','Paused','Failed'].includes(row.status)&&row.attemptCount<row.maxAttempts&&Number.isFinite(Date.parse(row.expiresAt))&&Date.parse(row.expiresAt)>now&&!['AttemptsExhausted','RetryAfterExceedsBudget'].includes(row.reason);}
export function notificationOutcomeLabel(outcome){return ({Accepted:'服务已接受',TransientFailure:'暂时失败',PermanentFailure:'永久失败',OutcomeUnknown:'结果不确定 · 可能重复'})[outcome]??'投递进行中，尚无结束结果';}
export function silenceBoundary(status){return status==='Sending'?'已开始的投递可能完成，静默仅停止后续尝试。':status==='Paused'?'投递已暂停，解除静默后仍受原目标、次数与期限限制。':'静默只停止尚未开始及后续投递，不撤回已发送内容。';}
export function createDeliveryState(identity){return {identity,epoch:0,items:[],total:0,page:1,pageSize:20,selected:null,attempts:[],attemptTotal:0,attemptPage:1,key:null,error:'',busy:false,conflict:false,invalid:false};}
export function deliveryActionState(state,event){
 if(event.type==='scope'||event.type==='denied')return {...createDeliveryState(event.identity??state.identity),epoch:state.epoch+1,invalid:event.type==='denied'};
 if(state.invalid)return state;
 if(event.type==='page')return {...state,page:event.page,epoch:state.epoch+1,items:[],selected:null,attempts:[],attemptTotal:0,key:null,error:'',busy:false,conflict:false};
 if(event.type==='select')return {...state,epoch:state.epoch+1,selected:event.row,attempts:[],attemptTotal:0,attemptPage:1,key:null,error:'',conflict:false};
 if(event.type==='attemptPage')return {...state,attemptPage:event.page,epoch:state.epoch+1,attempts:[],key:null};
 if(event.type==='begin')return state.busy||state.conflict?state:{...state,epoch:state.epoch+1,busy:true,key:state.key??event.key,error:''};
 if(event.epoch!==state.epoch)return state;
 if(event.type==='failure')return [401,403,404].includes(event.status)?{...createDeliveryState(state.identity),epoch:state.epoch+1,invalid:true,error:'读取权限或资源已失效，已清空投递记录。'}:{...state,busy:false,error:event.message,conflict:state.conflict||[409,412].includes(event.status)};
 if(event.type==='list'){const selected=state.selected?event.result.items.find(row=>row.id===state.selected.id)??null:null;return {...state,items:event.result.items,total:event.result.total,page:event.result.page,pageSize:event.result.pageSize,selected,key:selected?.revision===state.selected?.revision?state.key:null,conflict:false,error:''};}
 if(event.type==='attempts')return {...state,attempts:event.result.items,attemptTotal:event.result.total,error:''};
 if(event.type==='retried')return {...state,epoch:state.epoch+1,items:state.items.map(row=>row.id===event.row.id?event.row:row),selected:event.row,busy:false,key:null,conflict:false,error:''};
 return state;
}
