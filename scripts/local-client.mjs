import fs from 'node:fs';
export class LocalClient{
 constructor(base='http://127.0.0.1:5090', {signal}={}) {
  this.base=base;
  this.cookies=new Map();
  this.signal=signal;
 }
 async request(path, options={}) {
  const signal=options.signal ?? this.signal;
  const headers={'Content-Type':'application/json', ...options.headers};
  if (options.method && options.method!=='GET') {
   const csrf=await this.request('/auth/csrf', {signal});
   headers['X-CSRF-Token']=csrf.token;
   headers['Idempotency-Key']=options.key || crypto.randomUUID();
  }
  const r=await fetch(this.base+'/api/v1'+path, {
   ...options, signal,
   headers:{Cookie:[...this.cookies].map(([k,v])=>k+'='+v).join('; '), ...headers},
   body:options.body===undefined ? undefined : JSON.stringify(options.body),
  });
  for (const cookie of r.headers.getSetCookie()) {
   const first=cookie.split(';')[0], at=first.indexOf('=');
   this.cookies.set(first.slice(0,at),first.slice(at+1));
  }
  const result=r.status===204 ? null : await r.json();
  if (!r.ok) throw Object.assign(new Error(result.detail||result.title||r.status),{status:r.status,traceId:result.traceId,code:result.code});
  return result;
 }
 async login(username,password){return this.request('/auth/login',{method:'POST',body:{username,password}});}
}
export async function admin(){const c=new LocalClient();await c.login('local-admin',fs.readFileSync(new URL('../.secrets/local-admin-password',import.meta.url),'utf8'));return c;}
