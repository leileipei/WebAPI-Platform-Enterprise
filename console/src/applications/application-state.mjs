// Counts cross the wire as canonical decimal strings. Never round them through a JS number.
export function usageCountText(count,state){
 if(state==='Unknown')return '尚无可用采集（来源未知）';
 if(state==='Unavailable')return '采集不可用';
 if(!['Available','NoData','Partial'].includes(state))return '采集状态未知';
 if(count===null||count===undefined)return state==='Partial'?'采集不完整，尚无可用计数':'尚无可用计数';
 if(typeof count!=='string'||!/^(?:0|[1-9][0-9]*)$/.test(count))return '计数不合法，当前不可用';
 if(state==='NoData')return count==='0'?'0':'采集状态不一致，当前不可用';
 return state==='Partial'?`至少 ${count}（已采集，数据不完整）`:count;
}
export function visibleCredentialLastUsed(row){
 const value=row?.visibleLastUsedAt;
 return typeof value==='string'&&Number.isFinite(Date.parse(value))?value:null;
}
// Authorship is a recorded action by a known actor, never application ownership or a guess.
export function credentialActors(applicationId,credentialId,audits){
 const result={createdBy:null,revokedBy:null};
 const fields=(text)=>{try{const items=JSON.parse(text??'[]');return Array.isArray(items)?items.find(x=>x?.Type==='ApplicationCredential'&&x.Fields?.Id===credentialId&&x.Fields?.ApplicationId===applicationId)?.Fields:undefined;}catch{return undefined;}};
 for(const event of Array.isArray(audits)?audits:[]){
  if(typeof event?.userId!=='string'||!event.userId)continue;
  const after=fields(event.afterJson),before=fields(event.beforeJson);
  if(!result.createdBy&&event.action==='credential.create'&&after)result.createdBy=event.userId;
  if(!result.revokedBy&&event.action==='credential.update'&&after?.Status==='Revoked'&&before?.Status!=='Revoked'&&before)result.revokedBy=event.userId;
 }
 return result;
}
