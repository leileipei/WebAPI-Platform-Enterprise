import {parseSchemaDocument} from '../contracts/schema-state.mjs';
const reserved=new Set(['iss','sub','aud','exp','nbf','iat','jti','auth_time','nonce','acr','amr','sid','cnf']);
const uuid=s=>typeof s==='string'&&/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(s)&&!/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(s);
const text=(s,max)=>typeof s==='string'&&s.length>0&&s.length<=max&&s.trim().length>0&&!/[\u0000-\u001f\u007f]/.test(s);
const fields=(c,keys)=>c&&typeof c==='object'&&!Array.isArray(c)&&Object.keys(c).length===keys.length&&keys.every(k=>Object.hasOwn(c,k));
const list=(items,min,max,length,allowed)=>Array.isArray(items)&&items.length>=min&&items.length<=max&&new Set(items).size===items.length&&items.every(s=>text(s,length)&&(!allowed||allowed.includes(s)));
export function initialJwtDraft(){return {mode:'JWT',issuer:'',audiences:[],allowedAlgorithms:['RS256'],allowedTokenTypes:['JWT','at+jwt'],clockSkewSeconds:30,maxTokenLifetimeSeconds:3600,applicationClaim:'azp',applicationMappings:[],jwks:{keys:[]},forwardBearer:false};}
function decode(textValue,min,max){if(typeof textValue!=='string'||textValue.length>Math.ceil(max*4/3)||textValue.length%4===1||!/^[A-Za-z0-9_-]+$/.test(textValue))throw new Error('公开密钥编码无效');let binary;try{binary=atob(textValue.replaceAll('-','+').replaceAll('_','/')+'='.repeat((4-textValue.length%4)%4));}catch{throw new Error('公开密钥编码无效');}if(binary.length<min||binary.length>max||btoa(binary).replaceAll('+','-').replaceAll('/','_').replace(/=+$/,'')!==textValue)throw new Error('公开密钥长度或编码无效');return Uint8Array.from(binary,c=>c.charCodeAt(0));}
function publicKeys(jwks,algorithms){
 if(!fields(jwks,['keys'])||!Array.isArray(jwks.keys)||jwks.keys.length<1||jwks.keys.length>8)throw new Error('公开 JWKS 须含 1–8 个公钥');const kids=new Set();
 for(const k of jwks.keys){if(!text(k.kid,256)||kids.has(k.kid)||k.use!=='sig'||!algorithms.includes(k.alg))throw new Error('kid 须唯一，密钥用途和算法须匹配');kids.add(k.kid);let allowed;
  if(k.kty==='RSA'&&k.alg==='RS256'){allowed=['kid','kty','alg','use','n','e'];const n=decode(k.n,256,512),e=decode(k.e,1,512);const bits=(n.length-1)*8+Math.floor(Math.log2(n[0]))+1;const integer=b=>BigInt('0x'+Array.from(b,x=>x.toString(16).padStart(2,'0')).join(''));const exponent=integer(e);if(!n[0]||bits<2048||bits>4096||!(n.at(-1)&1)||!e[0]||exponent<3n||exponent%2n===0n||exponent>=integer(n))throw new Error('RSA 公钥须为 2048–4096 位');}
  else if(k.kty==='EC'&&k.alg==='ES256'){allowed=['kid','kty','alg','use','crv','x','y'];if(k.crv!=='P-256')throw new Error('EC 仅支持 P-256');decode(k.x,32,32);decode(k.y,32,32);}else throw new Error('仅接受 RS256 / ES256 公开密钥');
  if(allowed.some(name=>!Object.hasOwn(k,name))||Object.keys(k).some(name=>![...allowed,'key_ops','x5c','x5t','x5t#S256'].includes(name)))throw new Error('禁止私钥、网络取钥地址与未知字段');
  if(k.key_ops!==undefined&&(!list(k.key_ops,1,1,16,['verify'])))throw new Error('key_ops 仅允许 verify');for(const name of ['x5t','x5t#S256'])if(k[name]!==undefined)decode(k[name],1,96);
  if(k.x5c!==undefined){if(!list(k.x5c,1,8,8192))throw new Error('证书元数据无效');for(const cert of k.x5c){let binary;try{binary=atob(cert);}catch{throw new Error('证书元数据编码无效');}if(!binary.length||btoa(binary)!==cert)throw new Error('证书元数据编码无效');}}
 }
 return jwks;
}
export function parsePublicJwksFile(raw,algorithms=['RS256','ES256']){if(new TextEncoder().encode(raw).length>32768)throw new Error('公开 JWKS JSON 不得超过 32KiB');return publicKeys(parseSchemaDocument(raw),algorithms);}
export function validateAdvancedPolicyDraft(draft,scope){
 const c=draft.config;if(!c||typeof c!=='object'||Array.isArray(c))return '配置必须是对象';if(new TextEncoder().encode(JSON.stringify(c)).length>32768)return '配置不能超过 32KiB';const int=(k,a,b)=>Number.isInteger(c[k])&&c[k]>=a&&c[k]<=b;
 if(draft.type==='authentication'){
  if(c.mode!=='JWT')return fields(c,['mode'])&&['ApiKey','Anonymous'].includes(c.mode)?null:'请选择有效认证模式';
  if(!fields(c,Object.keys(initialJwtDraft())))return 'JWT 配置包含未知或缺失字段';let issuer;try{issuer=new URL(c.issuer);}catch{return 'Issuer 须为精确的绝对 HTTP(S) 地址';}
  if(!text(c.issuer,512)||/\s/.test(c.issuer)||!['https:','http:'].includes(issuer.protocol)||issuer.username||issuer.password||/[?#]/.test(c.issuer))return 'Issuer 不得包含凭据、空白、query 或 fragment';
  if(!list(c.audiences,1,8,256))return 'Audience 须为 1–8 项不重复精确值';if(!list(c.allowedAlgorithms,1,2,16,['RS256','ES256'])||!list(c.allowedTokenTypes,1,2,16,['JWT','at+jwt']))return '请选择固定算法和令牌类型';
  if(!int('clockSkewSeconds',0,120)||!int('maxTokenLifetimeSeconds',60,86400))return '时钟容差 0–120 秒；最大有效期 60–86400 秒';if(!text(c.applicationClaim,64)||!/^[A-Za-z0-9_-]+$/.test(c.applicationClaim)||reserved.has(c.applicationClaim))return '应用 Claim 须为未保留的 ASCII 名称';
  if(!Array.isArray(c.applicationMappings)||c.applicationMappings.length<1||c.applicationMappings.length>128||new Set(c.applicationMappings.map(m=>m.claimValue)).size!==c.applicationMappings.length)return '应用映射须为 1–128 项，Claim 值不得重复';
  for(const m of c.applicationMappings){if(!fields(m,['claimValue','applicationId'])||!text(m.claimValue,256)||!uuid(m.applicationId))return '应用映射值或应用 ID 无效';if(scope?.applications&&!scope.applications.some(a=>a.id===m.applicationId&&a.organizationId===scope.organizationId&&(a.projectId===null||a.projectId===undefined||a.projectId===scope.projectId)&&(!scope.projectId?!a.projectId:true)))return '映射应用不在当前可读资源范围';}
  if(typeof c.forwardBearer!=='boolean')return 'Bearer 转发须明确选择';try{publicKeys(c.jwks,c.allowedAlgorithms);}catch(e){return e.message;}return null;
 }
 if(draft.type==='retry'){
  if(!fields(c,['maxAttempts','perAttemptTimeoutMs','baseDelayMs','maxDelayMs','jitterPercent','retryStatusCodes','retryConnectionFailures']))return '重试配置包含未知或缺失字段';
  if(!int('maxAttempts',1,3)||!int('perAttemptTimeoutMs',10,300000)||!int('baseDelayMs',0,1000)||!int('maxDelayMs',0,5000)||c.maxDelayMs<c.baseDelayMs||!int('jitterPercent',0,100))return '检查尝试次数、单次超时、退避与抖动预算';
  if(!Array.isArray(c.retryStatusCodes)||c.retryStatusCodes.length>3||new Set(c.retryStatusCodes).size!==c.retryStatusCodes.length||c.retryStatusCodes.some(n=>![502,503,504].includes(n))||typeof c.retryConnectionFailures!=='boolean'||c.maxAttempts>1&&!c.retryStatusCodes.length&&!c.retryConnectionFailures)return '重试状态仅 502/503/504，并至少选择一种重试来源';return null;
 }
 if(draft.type==='cache'){
  if(!fields(c,['ttlSeconds','maxEntryBytes','varyHeaders','identityPartition','redisFailureMode']))return '缓存配置包含未知或缺失字段';
  if(!scope?.limits)return '请先读取当前范围的部署缓存限制';
  if(!int('ttlSeconds',1,3600)||!int('maxEntryBytes',1024,Math.min(1048576,scope.limits.maxEntryBytes)))return 'TTL 1–3600 秒，条目不得超过部署上限';
  if(c.identityPartition!=='VerifiedIdentity'||c.redisFailureMode!=='Bypass')return '身份分区和 Redis 故障旁路为固定保护';
  const denied=['cookie','authorization','x-api-key','host'];if(!Array.isArray(c.varyHeaders)||c.varyHeaders.length>8||new Set(c.varyHeaders.map(v=>typeof v==='string'?v.toLowerCase():v)).size!==c.varyHeaders.length||c.varyHeaders.some(v=>typeof v!=='string'||v.length>128||!/^[!#$%&'*+.^_`|~A-Za-z0-9-]+$/.test(v)||denied.includes(v.toLowerCase())||v.toLowerCase().startsWith('x-webapi-')||!scope.limits.allowedVaryHeaders.some(a=>a.toLowerCase()===v.toLowerCase())))return 'Vary 最多 8 项，仅允许部署登记的非敏感请求头';return null;
 }
 return '该类型尚未开放';
}
export function changeAuthenticationMode(state,mode,confirmed){if(!['ApiKey','Anonymous','JWT'].includes(mode)||mode===state.draft.config.mode)return state;if(state.dirty&&!confirmed)return {...state,pendingMode:mode};const modeDrafts={...state.modeDrafts,[state.draft.config.mode]:structuredClone(state.draft.config)},config=structuredClone(modeDrafts[mode]??(mode==='JWT'?initialJwtDraft():{mode}));return {...state,draft:{...state.draft,config},modeDrafts,pendingMode:null,dirty:true};}
export function acceptApplicationOptions(state,event){return state.scope===event.scope&&state.generation===event.generation?{...state,items:event.items}:state;}
export function effectiveAuthentication(route){return route.effectiveAuthenticationMode==='JWT'?'JWT':route.requireApiKey?'ApiKey':'Anonymous';}
export function routePolicyPayload(value,original){const body={};for(const key of ['apiVersionId','routeName','path','clusterId','priority','enabled','timeoutMs'])body[key]=value[key];body.methods=(value.methodsText??value.methods?.join(',')??'').split(',').map(v=>v.trim().toUpperCase()).filter(Boolean);body.requireApiKey=original?.requireApiKey??value.requireApiKey??true;const selected=value.authenticationSelection;if(['ApiKey','Anonymous'].includes(selected)&&selected!==effectiveAuthentication(original??{requireApiKey:true})){body.authenticationChange=selected;body.requireApiKey=selected==='ApiKey';}else if(typeof value.requireApiKey==='boolean'&&value.requireApiKey!==original?.requireApiKey&&selected===undefined){body.requireApiKey=value.requireApiKey;if(original)body.authenticationChange=value.requireApiKey?'ApiKey':'Anonymous';}return body;}
export function advancedPolicyReview(policies,{baselinePolicies=null}={}){const parse=p=>typeof p.config==='string'?JSON.parse(p.config):p.config;return(policies??[]).filter(p=>p.enabled!==false).flatMap(p=>{try{const c=parse(p);if(p.type==='authentication'&&c.mode==='JWT'){const kids=(c.jwks?.keys??[]).map(k=>k.kid);const old=baselinePolicies?.find(x=>(x.sourcePolicyId??x.id)===(p.sourcePolicyId??p.id));const oldKids=old?(parse(old).jwks?.keys??[]).map(k=>k.kid):[];return [{id:p.id,type:p.type,kids,addedKids:baselinePolicies===null?null:kids.filter(k=>!oldKids.includes(k)),removedKids:baselinePolicies===null?null:oldKids.filter(k=>!kids.includes(k)),mappingCount:c.applicationMappings?.length??0,forwardBearer:c.forwardBearer===true}];}if(p.type==='retry')return[{id:p.id,type:p.type,maxAttempts:c.maxAttempts}];if(p.type==='cache')return[{id:p.id,type:p.type,ttlSeconds:c.ttlSeconds,coldStart:true}];return [];}catch{return[{id:p.id,type:p.type,error:'配置摘要不可读取'}];}});}
