export const sourceTestTypes=[{value:'InterfaceFunction',label:'接口功能测试'},{value:'Integration',label:'集成测试'},{value:'ContractCompatibility',label:'契约兼容性测试'}];
export const productionTestTypes=[{value:'EntryConnectivity',label:'入口连通性'},{value:'AuthenticationAuthorization',label:'认证与授权'},{value:'CriticalBusinessCall',label:'关键业务调用'}];
export function deliveryAuthorityKey(user){return JSON.stringify([user?.id,[...(user?.permissions||[])].sort(),(user?.scopes||[]).map(x=>[x.scope.organizationId,x.scope.projectId??null,x.scope.environmentId??null,x.accessMode]).sort((a,b)=>JSON.stringify(a).localeCompare(JSON.stringify(b)))]);}
export function createDeliveryState(context={}){return {...context,epoch:0,data:undefined,draft:undefined,preview:undefined,conflict:false,error:''};}
const same=(s,t)=>t&&['actorAuthority','artifactId','sourceEnvironmentId','targetEnvironmentId','epoch'].every(k=>s[k]===t[k]);
export function deliveryReducer(state,event){if(event.type==='context'){const next=createDeliveryState(event.context);return {...next,epoch:state.epoch+1};}if(event.type==='draft')return {...state,draft:event.draft};if(!same(state,event.tag))return state;if(event.type==='loaded'&&state.authorized!==false)return {...state,data:event.data,error:'',conflict:false};if(event.type==='failed'){if([401,403,404].includes(event.status))return {...state,authorized:false,data:undefined,draft:undefined,preview:undefined,error:event.message||'访问已失效',conflict:false};return {...state,error:event.message||'操作失败',conflict:[409,412].includes(event.status)};}return state;}
export function manualEvidenceLabel(row){return row.isManual?'人工登记 · '+(row.result==='Passed'?'通过':'失败'):'来源未知 · 请核对证据';}
export function deliveryPolicyLabel(policy){return policy?.mode==='PromotionRequired'?'生产晋级门禁已启用':'生产晋级门禁未启用（Legacy）';}
export function verificationExpiryLabel(minutes){return `人工证据有效期：${minutes} 分钟，自测试结束时间起计算`;}
export function mayAcceptTest(row,userId){return row.status==='Requested'&&row.canAccept===true&&row.requestedBy!==userId;}

export function deliveryIsCurrent(state,context){return state.authorized!==false&&['actorAuthority','artifactId'].every(k=>state[k]===context[k]);}
