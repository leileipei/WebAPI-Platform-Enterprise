function invalid(){throw Error('入口必须为 HTTP/HTTPS 域名和端口，前缀须为不含空段、编码或相对段的路径。');}
function origin(value){
 if(value==null||value==='')return null;
 if(typeof value!=='string'||value.length>2048||/[\s\u0000-\u001f\u007f-\u009f\\?#@%]/u.test(value)||!/^https?:\/\/[^/]+\/?$/i.test(value))invalid();
 let url;try{url=new URL(value);}catch{invalid();}
 if(!url.hostname||!['http:','https:'].includes(url.protocol)||url.username||url.password||url.pathname!=='/')invalid();
 if(url.origin.length>2048)invalid();return url.origin;
}
export function normalizeAccessSettings(settings,isProduction=false){
 const publicUrl=origin(settings.gatewayPublicUrl),internalUrl=origin(settings.gatewayInternalUrl);let basePath=settings.basePath??'/';
 if(typeof basePath!=='string'||basePath.length>512||basePath.endsWith('//'))invalid();if(basePath==='')basePath='/';
 if(basePath!=='/'){basePath=basePath.replace(/\/$/,'');if(!/^\/(?:[A-Za-z0-9._~-]+)(?:\/[A-Za-z0-9._~-]+)*$/.test(basePath)||basePath.split('/').some(p=>p==='.'||p==='..'))invalid();}
 if(isProduction&&publicUrl&&!publicUrl.startsWith('https://'))throw Error('生产环境公开入口必须使用 HTTPS。');
 return {...settings,gatewayPublicUrl:publicUrl,gatewayInternalUrl:internalUrl,basePath};
}
function route(path){
 if(typeof path!=='string'||!path.startsWith('/')||path.length>1024||/[?%#\\\u0000-\u001f\u007f-\u009f]/u.test(path))invalid();const trimmed=path==='/'?path:path.replace(/\/+$/,'');const segments=trimmed.split('/'),names=new Set();
 for(let i=1;i<segments.length;i++){const part=segments[i];if((!part&&trimmed!=='/')||part==='.'||part==='..')invalid();if(/[{}]/.test(part)){const match=/^\{(\*{0,2})([A-Za-z_][A-Za-z0-9_]*)\}$/.exec(part);if(!match||names.has(match[2].toLowerCase())||match[1]&&i!==segments.length-1)invalid();names.add(match[2].toLowerCase());}else if(part.includes('*'))invalid();}return trimmed;
}
export function buildAddressTemplate(settings,routePath){const normalized=normalizeAccessSettings(settings);const path=route(routePath);return normalized.gatewayPublicUrl?normalized.gatewayPublicUrl+(normalized.basePath==='/'?'':normalized.basePath)+path:null;}
export function buildAddressExample(settings,routePath,values){const template=buildAddressTemplate(settings,routePath);if(!template)return null;const matches=[...template.matchAll(/\{(\*{0,2})([A-Za-z_][A-Za-z0-9_]*)\}/g)];if(matches.some(m=>m[1]||!Object.hasOwn(values,m[2])))return template;return template.replace(/\{(\*{0,2})([A-Za-z_][A-Za-z0-9_]*)\}/g,(_,star,name)=>encodeURIComponent(values[name]).replace(/[!'()*]/g,c=>'%'+c.charCodeAt(0).toString(16).toUpperCase()));}
export function buildCurlExample(method,address){if(!['GET','POST','PUT','PATCH','DELETE','HEAD','OPTIONS','TRACE'].includes(method)||typeof address!=='string'||!/^https?:\/\//.test(address))invalid();return `curl --request ${method} '${address.replace(/'/g,"'\\''")}'`;}
