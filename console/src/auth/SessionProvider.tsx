import {createContext,useContext,useEffect,useState,type ReactNode} from 'react';import {apiRequest,ApiError} from '../api/client';import type {Identity,Scope} from '../api/types';
type Session={user:Identity|null;loading:boolean;error:string;refresh:()=>Promise<void>;login:(u:string,p:string)=>Promise<void>;logout:()=>Promise<void>;can:(code:string,scope?:Scope,write?:boolean)=>boolean};
const Context=createContext<Session>(null!);
export function SessionProvider({children}:{children:ReactNode}){const[user,setUser]=useState<Identity|null>(null),[loading,setLoading]=useState(true),[error,setError]=useState('');
 async function refresh(){try{setUser(await apiRequest<Identity>('/auth/me'));setError('');}catch(e){if(e instanceof ApiError&&e.status===401)setUser(null);else setError((e as Error).message);}finally{setLoading(false);}}
 useEffect(()=>{void refresh();const expired=()=>setUser(null);window.addEventListener('session-expired',expired);return()=>window.removeEventListener('session-expired',expired);},[]);
 const can=(code:string,scope?:Scope,write=false)=>!!user?.permissions.includes(code)&&(!scope||user.scopes.some(g=>g.scope.organizationId===scope.organizationId&&(!g.scope.projectId||g.scope.projectId===scope.projectId)&&(!g.scope.environmentId||g.scope.environmentId===scope.environmentId)&&(!write||g.accessMode==='read_write')));
 return <Context.Provider value={{user,loading,error,refresh,can,login:async(username,password)=>{setUser(await apiRequest<Identity>('/auth/login',{method:'POST',body:{username,password}}));setError('');},logout:async()=>{await apiRequest('/auth/logout',{method:'POST'});setUser(null);}}}>{children}</Context.Provider>;
}export const useSession=()=>useContext(Context);
