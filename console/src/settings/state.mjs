import {notificationSettingsInput} from '../notifications/settings-state.mjs';
import {ssoAuthorityKey} from '../sso/state.mjs';
export function createSettingsState(){return {group:'security',loaded:null,values:null,etag:null,dirty:false,requestEpoch:0,conflict:false,conflictReviewed:false,conflictFields:[],idempotencyKey:null};}
export function toSettingsInput(response){return response.group==='notification'?notificationSettingsInput(response):structuredClone(response.values);}
export function applySettingsResponse(state,response,requestEpoch){
 if(state.requestEpoch!==requestEpoch||state.group!==response.group)return state;
 const latest=toSettingsInput(response),values={...latest},conflicts=new Set(state.conflictFields);
 const same=(left,right)=>JSON.stringify(canonical(left))===JSON.stringify(canonical(right));
 if(state.dirty&&state.values){
  const previous=state.loaded?toSettingsInput(state.loaded):{};
  const draft=settingsCommandValues(state.group,state.values);
  for(const key of Object.keys(state.values)){
   if(same(draft[key],previous[key])||same(draft[key],latest[key])){conflicts.delete(key);continue;}
   values[key]=state.values[key];
   // Secret references are redacted, so a concurrent replacement can look identical in both responses.
   if(!same(latest[key],previous[key])||(key.endsWith('SecretRef')&&state.loaded?.revision!==response.revision))
    conflicts.add(key);
  }
 }else conflicts.clear();
 const conflict=state.conflict||conflicts.size>0;
 return {...state,loaded:response,values,conflict,conflictReviewed:conflict,conflictFields:[...conflicts],etag:'"'+response.revision+'"'};
}
export function invalidateSettingsState(state){return {...createSettingsState(),group:state.group,requestEpoch:state.requestEpoch+1};}
export function applySettingsFailure(state,status){return [401,403,404].includes(status)?invalidateSettingsState(state):status===412?{...state,conflict:true,conflictReviewed:false}:state;}
function canonical(value){return Array.isArray(value)?value.map(canonical):value&&typeof value==='object'?Object.fromEntries(Object.keys(value).sort().map(k=>[k,canonical(value[k])])):value;}
export function settingsSemanticKey(group,values,etag){return JSON.stringify(canonical({group,values,etag}));}
export function settingsTestMessage(result){if(result.testKind!=='StructuralOnly')throw Error('配置测试结果类型不符合当前能力。');return '结构校验通过；未解析 SecretRef，未连接 SMTP/Webhook，未发送通知。';}
export function routeDefaultInput(input,defaults){return input.touched?input.timeoutMs:defaults.timeoutMs;}
export function settingsCommandValues(group,values){return group==='security'&&typeof values.allowedOrigins==='string'?{...values,allowedOrigins:values.allowedOrigins.split(/\r?\n/).map(s=>s.trim()).filter(Boolean)}:values;}
export function settingsShellAuthorityKey(user,path){return path==='/settings/sso'?ssoAuthorityKey(user):path==='/settings/system'?user.id:JSON.stringify([user.id,user.permissions,user.scopes]);}

export function settingsGroupFromSearch(search){const tabs=new URLSearchParams(search).getAll('tab');return tabs.length===1&&['security','release','gateway','audit','notification'].includes(tabs[0])?tabs[0]:'security';}
