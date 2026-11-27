import {useEffect,useState} from 'react';
import {version} from '../../package.json';

export function LoginDeployment() {
 const [environment,setEnvironment]=useState<string|null>();
 useEffect(()=>{
  const controller=new AbortController();
  async function read() {
   try {
    const response=await fetch('/api/v1/auth/deployment',{signal:controller.signal,cache:'no-store',credentials:'omit'});
    if(!response.ok)throw Error();
    const info=await response.json();
    if(!controller.signal.aborted)setEnvironment(typeof info.environment==='string'&&info.environment.trim()?info.environment:null);
   } catch {
    if(!controller.signal.aborted)setEnvironment(null);
   }
  }
  void read();
  return ()=>controller.abort();
 },[]);
 return <div className="login-deployment" aria-label="部署信息"><span>控制台版本 {version}</span>
  <span>{environment===undefined?'正在读取部署环境…':environment?'部署环境 '+environment:'部署环境暂不可用'}</span></div>;
}
