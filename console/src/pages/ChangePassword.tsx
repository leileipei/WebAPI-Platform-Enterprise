import {useRef,useState,type FormEvent} from 'react';
import {useSession} from '../auth/SessionProvider';
import {passwordChangeBody} from '../auth/password-state.mjs';
import {navigate} from '../Shell';
import {useUnsaved} from '../ui';

export function ChangePassword(){
 const session=useSession(),[currentPassword,setCurrent]=useState(''),[newPassword,setNew]=useState(''),[confirmPassword,setConfirm]=useState(''),[busy,setBusy]=useState(false),[error,setError]=useState(''),[discard,setDiscard]=useState(false);const errorBox=useRef<HTMLDivElement>(null);
 const dirty=!!(currentPassword||newPassword||confirmPassword);
 useUnsaved(dirty,()=>setError('密码尚未保存，请完成修改，或退出并清除输入。'));
 const clear=()=>{setCurrent('');setNew('');setConfirm('');};
 async function submit(event:FormEvent){
  event.preventDefault();if(busy)return;setError('');
  try{const body=passwordChangeBody({currentPassword,newPassword,confirmPassword});setBusy(true);await session.changePassword(body);clear();navigate(location.pathname==='/auth/change-password'?'/dashboard':location.pathname+location.search,true);}
  catch(e){setError((e as Error).message);requestAnimationFrame(()=>errorBox.current?.focus());}finally{setBusy(false);}
 }
 async function exit(){setBusy(true);setError('');try{await session.logout();clear();}catch(e){setError((e as Error).message);}finally{setBusy(false);}}
 if(session.user?.authSource==='sso')return <div className="login"><div className="login-form"><h1>企业账号密码由身份提供方管理</h1><p>请前往企业登录系统修改密码。</p><button className="btn" onClick={()=>navigate('/dashboard')}>返回控制台</button></div></div>;
 return <div className="login"><div className="login-intro"><div className="brand-mark">W</div><small>WEBAPI PLATFORM ENTERPRISE</small><h1>保护账号<br/>从更新密码开始</h1><p>{session.user?.mustChangePassword?'这是新账号或重置后的首次登录。完成改密后，即可按已分配的权限进入管理控制台。':'修改本地账号密码后，其他登录会话将失效。'}</p></div><div className="login-form"><small>ACCOUNT SECURITY</small><h2>{session.user?.mustChangePassword?'首次登录 · 修改密码':'修改本地密码'}</h2><p>{session.user?.displayName} · {session.user?.username}</p>{(error||session.error)&&<div ref={errorBox} tabIndex={-1} className="error" role="alert">{error||session.error}</div>}<form onSubmit={submit}><fieldset className="form-grid" disabled={busy}><label>当前密码<input autoFocus type="password" autoComplete="current-password" required maxLength={1024} value={currentPassword} onChange={e=>setCurrent(e.target.value)}/></label><label>新密码<input type="password" autoComplete="new-password" required maxLength={1024} value={newPassword} onChange={e=>setNew(e.target.value)}/><small>遵循当前平台密码策略；保留输入的空格和字符形式。</small></label><label>确认新密码<input type="password" autoComplete="new-password" required maxLength={1024} value={confirmPassword} onChange={e=>setConfirm(e.target.value)}/></label><button className="btn primary" type="submit">{busy?'正在修改…':'确认修改并继续'}</button></fieldset></form><button type="button" className="text-btn" disabled={busy} onClick={()=>dirty?setDiscard(true):void exit()}>退出登录</button>{discard&&<div className="notice" role="alertdialog" aria-label="退出并清除密码输入"><p>尚未保存的密码输入将被清除。</p><button className="btn" disabled={busy} onClick={()=>setDiscard(false)}>继续修改</button><button className="btn danger" disabled={busy} onClick={()=>void exit()}>退出并清除输入</button></div>}</div></div>;
}
