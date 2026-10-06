export class ApiError extends Error { constructor(status,detail,traceId,issues=[],code){super(detail);this.status=status;this.traceId=traceId;this.issues=Array.isArray(issues)?issues:[];this.code=code;} }
let token;
export function clearCsrfToken(){token=undefined;}
export async function refreshCsrfToken(){clearCsrfToken();const res=await fetch('/api/v1/auth/csrf',{credentials:'same-origin',cache:'no-store'});if(!res.ok)throw new ApiError(res.status,'无法获取登录安全令牌');token=(await res.json()).token;return token;}
export async function apiRequest(path,options={}){
 const method=options.method||'GET',write=!['GET','HEAD'].includes(method);
 if(write&&!token)await refreshCsrfToken();
 const headers={Accept:'application/json',...(write?{'Content-Type':'application/json','X-CSRF-Token':token,'Idempotency-Key':options.idempotencyKey||crypto.randomUUID()}:{}),...(options.etag?{'If-Match':options.etag}:{}),...options.headers};
 const response=await fetch('/api/v1'+path,{method,credentials:'same-origin',cache:'no-store',headers,body:options.body===undefined?undefined:JSON.stringify(options.body),signal:options.signal});
 if(!response.ok){const problem=await response.json().catch(()=>({}));if(response.status===401&&path!='/auth/login'){token=undefined;globalThis.dispatchEvent?.(new Event('session-expired'));}if([403,404].includes(response.status))globalThis.dispatchEvent?.(new Event('permission-refresh'));throw new ApiError(response.status,problem.detail||problem.title||'服务暂不可用，请重试。',problem.traceId,problem.issues,problem.code);}
 if(path==='/auth/login'||path==='/auth/logout'){token=undefined;if(path==='/auth/login'){const csrf=await fetch('/api/v1/auth/csrf',{credentials:'same-origin',cache:'no-store'});if(csrf.ok)token=(await csrf.json()).token;}}
 if(options.responseType==='response')return response;
 return response.status===204?undefined:response.json();
}
