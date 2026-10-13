import {serializeTransformDraft,parseTransformDraft} from './json-body-draft.mjs';
const size=value=>new TextEncoder().encode(value).length;
const token=/^[!#$%&'*+.^_`|~A-Za-z0-9-]+$/;
const methods=['GET','POST','PUT','PATCH','DELETE','HEAD','OPTIONS'];
const protectedNames=new Set(['authorization','x-api-key','cookie','set-cookie','host','forwarded','connection','keep-alive','proxy-connection','proxy-authenticate','proxy-authorization','te','trailer','transfer-encoding','upgrade','traceparent','tracestate','baggage','content-length','content-type','content-encoding','vary','cache-control','age','expires','etag','last-modified','date','content-md5','digest','content-digest','repr-digest','signature','signature-input']);
const text=(v,max)=>typeof v==='string'&&v.length>0&&v.length<=max&&!/[\x00-\x1f\x7f]/.test(v);
const exact=(v,names)=>v&&typeof v==='object'&&!Array.isArray(v)&&Object.keys(v).length===names.length&&names.every(n=>Object.hasOwn(v,n));
const unique=(values,ignoreCase=false)=>new Set(values.map(v=>ignoreCase?String(v).toLowerCase():v)).size===values.length;
export function protectedTransformHeader(name){return protectedNames.has(name.toLowerCase())||/^(x-webapi-|x-forwarded-|access-control-)/i.test(name);}
export function initialNetworkPolicyDraft(type){return structuredClone({cors:{allowedOrigins:[],allowCredentials:false,allowedMethods:['GET','POST','PUT','PATCH','DELETE','HEAD','OPTIONS'],allowedHeaders:['Content-Type','Authorization','X-API-Key'],exposedHeaders:[],maxAgeSeconds:600},ip_filter:{mode:'AllowList',ranges:[]},transform:{requestHeaders:[],responseHeaders:[],path:null,query:[],requestBody:null,responseBody:null}}[type]??{});}
function origin(value){if(value==='*')return value;if(!/^https?:\/\/[^/?#@\\\s]+$/i.test(value))throw new Error('来源须为精确 HTTP(S) scheme + host + port，不能带路径');const url=new URL(value);return url.origin;}
function headers(values,max){return Array.isArray(values)&&values.length<=max&&unique(values,true)&&values.every(v=>text(v,128)&&token.test(v)&&v!=='*'&&!/^(x-webapi-|x-forwarded-)/i.test(v)&&!['cookie','set-cookie','host','forwarded','connection','keep-alive','proxy-connection','proxy-authenticate','proxy-authorization','te','trailer','transfer-encoding','upgrade','traceparent','tracestate','baggage'].includes(v.toLowerCase()));}
function cidr(value){
 if(!text(value,128)||value.trim()!==value)return false;const parts=value.split('/');if(parts.length>2)return false;let address=parts[0],bits=parts[1];
 if(address.includes(':')){if(!/^[0-9a-f:.]+$/i.test(address))return false;try{new URL('http://['+address+']');}catch{return false;}return bits===undefined||/^\d+$/.test(bits)&&Number(bits)<=128;}
 const octets=address.split('.');return octets.length===4&&octets.every(v=>/^(0|[1-9]\d{0,2})$/.test(v)&&Number(v)<=255)&&(bits===undefined||/^(0|[1-9]\d?)$/.test(bits)&&Number(bits)<=32);
}
function prefix(value){return value===null||text(value,512)&&value.startsWith('/')&&!/[%?#\\{}\x00-\x1f\x7f]/.test(value)&&!value.includes('//')&&!value.split('/').some(s=>s==='.'||s==='..');}
function pointer(value){return typeof value==='string'&&value.length<=512&&!/[\x00-\x1f\x7f]/.test(value)&&(value===''||value.startsWith('/'))&&!/~(?![01])/.test(value)&&value.split('/').length<=33;}
export function validateNetworkPolicyDraft(draft,scope={}){
 try{
  const c=draft.config;
  if(draft.type==='cors'){
   if(!exact(c,['allowedOrigins','allowCredentials','allowedMethods','allowedHeaders','exposedHeaders','maxAgeSeconds']))return 'CORS 配置包含未知或缺失字段';
   if(!Array.isArray(c.allowedOrigins)||c.allowedOrigins.length<1||c.allowedOrigins.length>64)return '明确填写 1–64 个允许来源';const normalized=c.allowedOrigins.map(origin);if(!unique(normalized)||normalized.includes('*')&&(normalized.length!==1||c.allowCredentials))return '来源不得重复；通配来源不能携带 credentials';
   if(typeof c.allowCredentials!=='boolean'||!Array.isArray(c.allowedMethods)||c.allowedMethods.length<1||c.allowedMethods.length>7||!unique(c.allowedMethods)||c.allowedMethods.some(v=>!methods.includes(v))||!headers(c.allowedHeaders,64)||!headers(c.exposedHeaders,32)||!Number.isInteger(c.maxAgeSeconds)||c.maxAgeSeconds<0||c.maxAgeSeconds>86400)return '检查方法、Header 与 maxAge（0–86400 秒）';
  }else if(draft.type==='ip_filter'){
   if(!exact(c,['mode','ranges'])||!['AllowList','DenyList'].includes(c.mode)||!Array.isArray(c.ranges)||c.ranges.length<1||c.ranges.length>256||!unique(c.ranges)||c.ranges.some(v=>!cidr(v)))return '明确选择名单模式，填写 1–256 个合法 IP / CIDR；服务端还会规范化重复网段';
  }else if(draft.type==='transform'){
   serializeTransformDraft(c);
   for(const field of ['requestHeaders','responseHeaders','query']){
    if(!Array.isArray(c[field])||c[field].length>32||!unique(c[field].map(v=>v.name),field!=='query'))return '每方向最多 32 条不重复操作';
    for(const op of c[field])if(!['Set','Remove'].includes(op.action)||!text(op.name,128)||field!=='query'&&(!token.test(op.name)||protectedTransformHeader(op.name))||op.action==='Set'&&(typeof op.value!=='string'||size(op.value)>1024||/[\x00-\x1f\x7f]/.test(op.value)))return 'Header / Query 名称、值或受保护字段不合法';
   }
   if(c.path!==null&&(!exact(c.path,['removePrefix','addPrefix'])||!prefix(c.path.removePrefix)||!prefix(c.path.addPrefix)||c.path.removePrefix===null&&c.path.addPrefix===null))return '路径须为绝对字面量段前缀，不允许编码、点段和占位符';
   for(const direction of ['requestBody','responseBody'])if(c[direction]!==null){
    const b=c[direction],selector=direction==='requestBody'?'methods':'statusCodes';if(!exact(b,[selector,'maxBodyBytes','operations']))return '正文方向包含未知或缺失字段';
    const limit=scope.limits?.transformMaxBodyBytes??1048576;if(!Number.isInteger(b.maxBodyBytes)||b.maxBodyBytes<1024||b.maxBodyBytes>Math.min(1048576,limit))return '正文上限须为 1024 字节至部署上限';
    if(!Array.isArray(b[selector])||!b[selector].length||!unique(b[selector])||b[selector].length>(direction==='requestBody'?6:32)||b[selector].some(v=>direction==='requestBody'?!methods.filter(m=>m!=='HEAD').includes(v):!Number.isInteger(v)||v<200||v>599||[204,205,206,304].includes(v)))return '检查请求方法 / 响应状态选择';
    for(const operation of b.operations){if(!pointer(operation.path)||operation.op==='remove'&&operation.path===''||['copy','move'].includes(operation.op)&&!pointer(operation.from))return 'JSON Pointer 不合法；remove 不允许移除根';if(operation.op==='move'&&operation.path.startsWith(operation.from+'/'))return '不能移动到自身子路径';}
   }
  }else return '该网络策略类型尚未开放';return null;
 }catch(error){return error.message;}
}
export function networkPolicyReview(type,config){const c=typeof config==='string'?(type==='transform'?parseTransformDraft(config):JSON.parse(config)):config;return {type,schemaVersion:'2.3',jsonContractReviewRequired:type==='transform',allowedOrigins:c.allowedOrigins??[],credentials:c.allowCredentials===true,ranges:c.ranges??[],allNetwork:(c.ranges??[]).some(v=>v.endsWith('/0')),path:c.path??null,requestBody:c.requestBody??null,responseBody:c.responseBody??null,frozenBodyRules:type==='transform'};}
