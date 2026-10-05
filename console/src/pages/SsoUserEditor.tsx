import {useEffect,useRef,useState,type KeyboardEvent} from 'react';
import {apiRequest} from '../api/client';
import {useSession} from '../auth/SessionProvider';
import {useRemote,Editor,Feedback,type Field,Details} from '../ui';
import type {Page} from '../api/types';
import type {SsoProvider,ExternalIdentity} from '../sso/contracts';
import {ssoUserPayload,ssoBindingPayload,mayEditSsoBinding} from '../sso/user.mjs';
export function SsoUserEditor({user,onSaved,onCancel}:{user?:any;onSaved:()=>void;onCancel:()=>void}){
 const readOnlyDialog=useRef<HTMLElement>(null),previousFocus=useRef<HTMLElement|null>(null);
 const session=useSession(),authorized=session.can('user.manage')&&session.can('system.sso.manage'),[error,setError]=useState('');
 const providers=useRemote<Page<SsoProvider>>(authorized?'/settings/sso/providers':null,0,true),binding=useRemote<ExternalIdentity>(authorized&&user?'/users/'+user.id+'/external-identity':null);
 useEffect(()=>{if(!authorized)onCancel();},[authorized]);
 useEffect(()=>{previousFocus.current=document.activeElement as HTMLElement;return()=>previousFocus.current?.focus();},[]);
 useEffect(()=>{readOnlyDialog.current?.querySelector<HTMLElement>('button')?.focus();},[providers.loading,binding.loading,providers.error,binding.error,user?.status]);
 function dialogKeys(e:KeyboardEvent<HTMLElement>){if(e.key==='Escape'){e.preventDefault();onCancel();}if(e.key==='Tab'){const items=[...e.currentTarget.querySelectorAll<HTMLElement>('button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled)')];if(e.shiftKey&&document.activeElement===items[0]){e.preventDefault();items.at(-1)?.focus();}else if(!e.shiftKey&&document.activeElement===items.at(-1)){e.preventDefault();items[0]?.focus();}}}

 useEffect(()=>{const expired=()=>onCancel();window.addEventListener('session-expired',expired);return()=>window.removeEventListener('session-expired',expired);},[]);
 if(!authorized)return null;
 if(providers.loading||user&&binding.loading)return <div className="overlay"><section ref={readOnlyDialog} onKeyDown={dialogKeys} className="dialog" role="dialog" aria-modal="true" aria-label="正在读取 SSO 绑定"><button className="btn" onClick={onCancel}>关闭</button><Feedback {...providers}/><Feedback {...binding}/></section></div>;
 if(providers.error||user&&binding.error)return <div className="overlay"><section ref={readOnlyDialog} onKeyDown={dialogKeys} className="dialog" role="dialog" aria-modal="true" aria-label="绑定读取失败"><button className="btn" onClick={onCancel}>关闭</button><Feedback {...providers}/><Feedback {...binding}/><button className="btn" onClick={()=>{providers.reload();binding.reload();}}>重试</button></section></div>;
 if(user&&!mayEditSsoBinding(user,session.user!.permissions))return <div className="overlay"><section ref={readOnlyDialog} onKeyDown={dialogKeys} className="dialog" role="dialog" aria-modal="true" aria-label="SSO 绑定详情"><header><h2>SSO 绑定 · {user.displayName}</h2><button className="text-btn" onClick={onCancel}>关闭</button></header><p className="notice">请先在用户管理中停用此账号，再修正外部身份绑定。角色及数据范围仍由平台单独配置。</p><Details value={binding.data}/></section></div>;
 const options=(providers.data?.items||[]).map(p=>({value:p.id,label:p.name+(p.enabled?' · 已启用':' · 未启用')}));
 const fields:Field[]=[...(!user?[{key:'username',label:'平台用户名',required:true},{key:'displayName',label:'显示名称',required:true},{key:'email',label:'资料邮箱',type:'email'}]:[]),{key:'providerId',label:'SSO 身份源',required:true,options},{key:'subject',label:'外部 Subject',required:true,help:'使用身份服务的稳定 Subject，区分大小写；请从身份服务核对，不以邮箱替代。'},...(user?[{key:'enabled',label:'绑定启用',type:'checkbox'}]:[])];
 return <><Editor title={user?'修正 SSO 绑定 · '+user.displayName:'预创建 SSO 用户'} fields={fields} initial={user?binding.data:{email:'',providerId:'',subject:''}} help="保存后按原流程显式分配角色和数据范围；SSO 用户无本地密码。" close={onCancel} save={async(value,key,fresh)=>{try{await apiRequest(user?'/users/'+user.id+'/external-identity':'/users/sso',{method:user?'PUT':'POST',body:user?ssoBindingPayload(value):ssoUserPayload(value),etag:user?'"'+(fresh?.revision??binding.data?.revision)+'"':undefined,idempotencyKey:key});onSaved();}catch(e){setError((e as Error).message);throw e;}}} loadLatest={user?()=>apiRequest('/users/'+user.id+'/external-identity'):undefined}/>{error&&<span className="sr-only" role="status">{error}</span>}</>;
}
