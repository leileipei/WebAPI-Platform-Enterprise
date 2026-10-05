export function createSettingsState(){return {group:'security',loaded:null,values:null,etag:null,dirty:false,requestEpoch:0,conflict:false,conflictReviewed:false,idempotencyKey:null};}
export function toSettingsInput(response){const values=structuredClone(response.values);if(response.group==='notification'){values.smtpSecretRef={operation:'Keep'};values.webhookSecretRef={operation:'Keep'};}return values;}
export function applySettingsResponse(state,response,requestEpoch){if(state.requestEpoch!==requestEpoch||state.group!==response.group)return state;return {...state,loaded:response,conflictReviewed:state.conflict,etag:'"'+response.revision+'"',values:state.dirty?state.values:toSettingsInput(response)};}
export function invalidateSettingsState(state){return {...createSettingsState(),group:state.group,requestEpoch:state.requestEpoch+1};}
export function applySettingsFailure(state,status){return [401,403,404].includes(status)?invalidateSettingsState(state):status===412?{...state,conflict:true,conflictReviewed:false}:state;}
function canonical(value){return Array.isArray(value)?value.map(canonical):value&&typeof value==='object'?Object.fromEntries(Object.keys(value).sort().map(k=>[k,canonical(value[k])])):value;}
export function settingsSemanticKey(group,values,etag){return JSON.stringify(canonical({group,values,etag}));}
export function settingsTestMessage(result){if(result.testKind!=='StructuralOnly')throw Error('配置测试结果类型不符合当前能力。');return '结构校验通过；未解析 SecretRef，未连接 SMTP/Webhook，未发送通知。';}
export function routeDefaultInput(input,defaults){return input.touched?input.timeoutMs:defaults.timeoutMs;}
export function settingsCommandValues(group,values){return group==='security'&&typeof values.allowedOrigins==='string'?{...values,allowedOrigins:values.allowedOrigins.split(/\r?\n/).map(s=>s.trim()).filter(Boolean)}:values;}
export function settingsShellAuthorityKey(user,path){return path==='/settings/system'?user.id:JSON.stringify([user.id,user.permissions,user.scopes]);}
