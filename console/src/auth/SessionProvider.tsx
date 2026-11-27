import {createContext,useContext,useEffect,useLayoutEffect,useMemo,useState,useRef,type ReactNode} from 'react';import {clearCsrfToken,invalidatePendingWrites} from '../api/transport.mjs';import {apiRequest,ApiError} from '../api/client';import type {Identity,Scope} from '../api/types';
type Session={user:Identity|null;loading:boolean;error:string;refresh:(requireAuthenticated?:boolean)=>Promise<void>;login:(u:string,p:string)=>Promise<void>;logout:()=>Promise<void>;changePassword:(body:{currentPassword:string;newPassword:string;confirmPassword:string})=>Promise<void>;can:(code:string,scope?:Scope,write?:boolean)=>boolean;canAct:(code:string,scope:Scope|null)=>boolean};
const Context=createContext<Session>(null!);
export function SessionProvider({children}:{children:ReactNode}){const[user,setUser]=useState<Identity|null>(null),[loading,setLoading]=useState(true),[error,setError]=useState('');const pending=useRef<Promise<void>|null>(null),authenticated=useRef(false);
 function refresh(requireAuthenticated=false):Promise<void>{const verify=()=>{if(requireAuthenticated&&!authenticated.current)throw Error('企业登录会话恢复失败，请返回登录页重试。');};if(pending.current)return pending.current.then(verify);const run=(async()=>{try{setUser(await apiRequest<Identity>('/auth/me'));authenticated.current=true;setError('');}catch(e){authenticated.current=false;if(e instanceof ApiError&&[401,403,404].includes(e.status))setUser(null);else setError('会话与权限刷新失败：'+(e as Error).message);}finally{setLoading(false);pending.current=null;}})();pending.current=run;return run.then(verify);}
 useEffect(()=>{void refresh();const expired=()=>{clearCsrfToken();authenticated.current=false;setUser(null);},denied=()=>{void refresh();};window.addEventListener('session-expired',expired);window.addEventListener('permission-refresh',denied);return()=>{window.removeEventListener('session-expired',expired);window.removeEventListener('permission-refresh',denied);};},[]);
 const authority=JSON.stringify([user?.id,user?.permissions,user?.scopes,user?.rolePermissions,user?.isPlatformAdministrator,user?.mustChangePassword,error]);
 useLayoutEffect(invalidatePendingWrites,[authority]);
 const rolePermissions=useMemo(()=>new Map(user?.rolePermissions?.map(group=>[group.organizationId,new Set(group.permissions)])),[user]);
 const roleAllows=(code:string,org:string|null)=>!!(rolePermissions.get(null)?.has(code)||rolePermissions.get(org)?.has(code));
 function can(code:string,scope?:Scope,write=false){
   if(error||!user||user.mustChangePassword||!user.permissions.includes(code))return false;
   if(!scope)return true;
   if(write&&!roleAllows(code,scope.organizationId))return false;
   return user.scopes.some(grant=>grant.scope.organizationId===scope.organizationId
     &&(!grant.scope.projectId||grant.scope.projectId===scope.projectId)
     &&(!grant.scope.environmentId||grant.scope.environmentId===scope.environmentId)
     &&(!write||grant.accessMode==='read_write'));
 }
 function canAct(code:string,scope:Scope|null){
   if(!scope)return user?.isPlatformAdministrator===true&&roleAllows(code,null)&&can(code);
   const write=!code.endsWith('.read');
   return can(code,scope,write)&&(write||roleAllows(code,scope.organizationId));
 }
 return <Context.Provider value={{user,loading,error,refresh,can,canAct,changePassword:async(body)=>{const identity=await apiRequest<Identity>('/auth/change-password',{method:'POST',body});if(pending.current)await pending.current;clearCsrfToken();authenticated.current=true;setUser(identity);setError('');},login:async(username,password)=>{setUser(await apiRequest<Identity>('/auth/login',{method:'POST',body:{username,password}}));setError('');},logout:async()=>{await apiRequest('/auth/logout',{method:'POST'});setUser(null);}}}>{children}</Context.Provider>;
}export const useSession=()=>useContext(Context);
