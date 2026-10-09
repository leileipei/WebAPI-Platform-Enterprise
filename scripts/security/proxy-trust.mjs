import fs from 'node:fs/promises';import path from 'node:path';import {isIP} from 'node:net';
import {inspectResources,assertOwnership} from '../runtime/docker.mjs';import {writePrivate,safeFile,RuntimeError} from '../runtime/state.mjs';
export const consoleProxyReceiptName='console-proxy-trust.json';
export async function resolveOwnedConsoleProxy(state,{inspect=inspectResources}={}){
 const resources=await inspect(state);assertOwnership(state,resources);
 const consoles=resources.filter(r=>r.Kind==='container'&&r.Config?.Labels?.['com.docker.compose.service']==='console'&&r.Config?.Labels?.['com.docker.compose.oneoff']!=='True');
 if(consoles.length!==1||!consoles[0].State?.Running)throw new RuntimeError('One running owned Console is required for proxy trust.',3);
 const console=consoles[0],network=resources.find(r=>r.Kind==='network'&&r.Name===state.projectName+'_default'),endpoint=console.NetworkSettings?.Networks?.[state.projectName+'_default'];
 const ip=endpoint?.IPAddress;
 if(!network?.Id||endpoint?.NetworkID!==network.Id||!console.Id||isIP(ip??'')!==4||/^(0|127|169\.254|22[4-9]|23\d)\./.test(ip))throw new RuntimeError('Owned Console network or exact proxy IP invalid.',3);
 return{containerId:console.Id,networkId:network.Id,ipAddress:ip};
}
export async function verifyConsoleProxyTrust(state,receipt,{inspect=inspectResources}={}){
 try{
  const resources=await inspect(state),actual=await resolveOwnedConsoleProxy(state,{inspect:async()=>resources});
  if(!receipt||['containerId','networkId','ipAddress'].some(k=>receipt[k]!==actual[k]))return false;
  const cp=resources.filter(r=>r.Kind==='container'&&r.Config?.Labels?.['com.docker.compose.service']==='control-plane'&&r.Config?.Labels?.['com.docker.compose.oneoff']!=='True');
  if(cp.length!==1||!cp[0].State?.Running)return false;
  const env=(cp[0].Config?.Env??[]).filter(e=>/^HttpSecurity__TrustedProxy(?:Ips|Networks)__/i.test(e));
  return env.length===1&&env[0]==='HttpSecurity__TrustedProxyIps__0='+actual.ipAddress;
 }catch{return false;}
}
export async function applyConsoleProxyTrust(ctx,receipt,{inspect=inspectResources}={}){
 const actual=await resolveOwnedConsoleProxy(ctx.state,{inspect});if(['containerId','networkId','ipAddress'].some(k=>actual[k]!==receipt[k]))throw new RuntimeError('Console changed before proxy trust application.',3);
 const file=path.join(ctx.directory,'runtime-config.json');await safeFile(file);const config=JSON.parse(await fs.readFile(file,'utf8'));const environment=config.services?.['control-plane']?.environment;if(!environment)throw new RuntimeError('Control Plane runtime overlay missing.',3);
 for(const name of Object.keys(environment))if(/^HttpSecurity__TrustedProxy(?:Ips|Networks)__/i.test(name))delete environment[name];environment.HttpSecurity__TrustedProxyIps__0=actual.ipAddress;await writePrivate(file,config);
 await ctx.compose('up','-d','--no-deps','--force-recreate','control-plane');
 if(!await verifyConsoleProxyTrust(ctx.state,actual,{inspect}))throw new RuntimeError('Console proxy trust drift after Control Plane recreation; readiness unknown.',4);
 await writePrivate(path.join(ctx.directory,consoleProxyReceiptName),{schemaVersion:1,ownerId:ctx.state.ownerId,projectName:ctx.state.projectName,...actual});return actual;
}
export async function synchronizeConsoleProxyTrust(ctx,{inspect=inspectResources}={}){
 if(!ctx.securitySupported)return null;const actual=await resolveOwnedConsoleProxy(ctx.state,{inspect});
 if(await verifyConsoleProxyTrust(ctx.state,actual,{inspect})){await writePrivate(path.join(ctx.directory,consoleProxyReceiptName),{schemaVersion:1,ownerId:ctx.state.ownerId,projectName:ctx.state.projectName,...actual});return actual;}
 return applyConsoleProxyTrust(ctx,actual,{inspect});
}
