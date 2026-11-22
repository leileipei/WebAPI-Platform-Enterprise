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

// Preserve the sub-millisecond precision of a DateTimeOffset returned by the API.
export function credentialInstantTicks(value) {
  if (typeof value !== 'string') return null;
  const parts = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})$/.exec(value);
  if (!parts) return null;
  const utcMillis = Date.parse(parts[1] + parts[3]);
  const localMillis = Date.parse(parts[1] + 'Z');
  if (!Number.isFinite(utcMillis) || !Number.isFinite(localMillis) ||
      new Date(localMillis).toISOString().slice(0,19) !== parts[1]) return null;
  return BigInt(utcMillis) * 10000n + BigInt((parts[2] || '').padEnd(7, '0'));
}

export function credentialWindowState(credential, nowMs) {
  if (credential.status === 'Revoked') return {state:'Revoked',nextChangeAt:null};
  const from = credentialInstantTicks(credential.validFrom), end = credentialInstantTicks(credential.expiresAt);
  if (credential.status !== 'Active' || from === null || end === null || end <= from)
    return {state:'Unknown',nextChangeAt:null};
  const now = BigInt(nowMs) * 10000n;
  if (now >= end) return {state:'Expired',nextChangeAt:null};
  const pending = now < from;
  const boundary = pending ? from : end;
  // The browser can first observe a fractional boundary on the following millisecond.
  const nextChangeAt = Number(boundary / 10000n) + (boundary % 10000n > 0n ? 1 : 0);
  return {state:pending ? 'Pending' : 'Active',nextChangeAt};
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
