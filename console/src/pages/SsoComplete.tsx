import {useEffect,useState} from 'react';
import {useSession} from '../auth/SessionProvider';
import {clearCsrfToken,refreshCsrfToken} from '../api/transport.mjs';
import {completeSsoLogin} from '../sso/login.mjs';
import {navigate} from '../Shell';
export function SsoComplete(){const session=useSession(),[error,setError]=useState(''),[attempt,setAttempt]=useState(0);useEffect(()=>{let active=true;clearCsrfToken();void completeSsoLogin({refresh:()=>session.refresh(true),csrf:refreshCsrfToken,navigate:path=>{if(active)navigate(path);},returnPath:new URLSearchParams(location.search).get('returnPath')}).catch(e=>{if(active)setError((e as Error).message);});return()=>{active=false;};},[attempt]);return <div className="sso-complete card"><div className="brand-mark">W</div><h1>正在完成企业登录</h1><p>恢复平台会话与访问权限…</p>{error&&<div className="error" role="alert">{error}</div>}{error&&<><button className="btn primary" onClick={()=>{setError('');setAttempt(n=>n+1);}}>重试恢复</button><button className="btn" onClick={()=>navigate('/login')}>返回登录页</button></>}</div>;}
