export function notificationSettingsInput(dto){const values=structuredClone(dto.values);return {...values,smtpEnabled:values.smtpEnabled??false,webhookEnabled:values.webhookEnabled??false,smtpSecurity:values.smtpSecurity??'StartTlsRequired',smtpSecretRef:{operation:'Keep'},webhookSecretRef:{operation:'Keep'}};}
export function notificationTestLabel(receipt){return ({Queued:'已排队，等待实际回执',Sending:'投递进行中，等待实际回执',RetryScheduled:'已安排下一次尝试',Paused:'已暂停，查看原因后恢复',Accepted:'服务已接受，仍需收件方确认',Failed:'投递失败，查看结果码',Suppressed:'已抑制，未继续投递',Expired:'任务已到期，停止投递'})[receipt?.status]??'尚未发起测试';}
export function mayTestNotification(state){return !!state.authorized&&!state.busy&&!state.dirty&&!state.conflict&&!!state.etag&&Number(state.loaded?.revision)>0;}
export function createNotificationTestState(channel,revision,authority){return {channel,revision,authority,email:'',epoch:0,receipt:null,key:null,busy:false,conflict:false,invalid:false,error:''};}
function clear(state){return {...createNotificationTestState(state.channel,state.revision,state.authority),epoch:state.epoch+1,invalid:true};}
export function notificationTestState(state,event){
 if(['close','denied'].includes(event.type))return clear(state);
 if(event.type==='revision')return {...state,revision:event.revision,epoch:state.epoch+1,receipt:null,key:null,busy:false,conflict:false};
 if(state.invalid)return state;
 if(event.type==='email')return {...state,email:event.email,epoch:state.epoch+1,receipt:null,key:null,error:''};
 if(event.type==='begin')return state.busy||state.conflict?state:{...state,epoch:state.epoch+1,busy:true,error:'',key:state.key??event.key};
 if(event.epoch!==state.epoch)return state;
 if(event.type==='receipt')return event.revision!==state.revision?state:{...state,receipt:event.receipt,busy:false,error:''};
 if(event.type==='failure')return [401,403,404].includes(event.status)?clear(state):{...state,busy:false,conflict:state.conflict||event.status===412,error:event.message};
 return state;
}
