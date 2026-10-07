import fs from 'node:fs/promises';import path from 'node:path';
import {loadState,writePrivate,withRuntimeLock,RuntimeError} from '../runtime/state.mjs';
import {docker,inspectResources,assertOwnership} from '../runtime/docker.mjs';
import {loadRelease,waitForRuntime} from '../runtime/lifecycle.mjs';
import {loadNotificationRuntime,loadNotificationSecrets,prepareNotificationSecrets,assertNotificationPorts,notificationStateName} from './runtime-secrets.mjs';
export async function notificationContext(directory,state,release){
 const notification=await loadNotificationRuntime(directory,state);if(!notification)return {notification:null};
 if(!release.applications?.['notification-fixture/WebApi.NotificationFixtureHost.dll'])throw new RuntimeError('Notification runtime requires a verified fixture build.',3);
 const secretDirectory=path.join(directory,'notification-secrets'),receipt=await loadNotificationSecrets(secretDirectory,state.ownerId,state.projectName);
 const resources=await inspectResources(state);assertOwnership(state,resources);const network=resources.find(r=>r.Kind==='network'&&r.Name===state.projectName+'_default');
 const cidrs=(network?.IPAM?.Config??[]).map(c=>c.Subnet).filter(Boolean);if(cidrs.some(c=>!/^\d{1,3}(\.\d{1,3}){3}\/\d{1,2}$/.test(c)))throw new RuntimeError('Notification fixture network requires verified IPv4 CIDR.',3);
 const environment=notification.enabled?{
  Notifications__SecretFilesJson:JSON.stringify({'vault://local-notification/smtp':'/run/notifications/smtp.json','vault://local-notification/webhook':'/run/notifications/webhook.json'}),
  Notifications__AllowedRecipients__0:'recipient@example.test',Notifications__AllowedSmtpEndpoints__0:'notification-fixture:2525',Notifications__AllowedWebhookUrls__0:'https://notification-fixture:9443/notify',
  Notifications__FixtureEnabled:'true',Notifications__FixtureCaFile:'/run/notifications/ca.crt',Notifications__ConsoleBaseUrl:'http://127.0.0.1:'+state.ports.console+'/',
  ...Object.fromEntries(cidrs.map((cidr,i)=>['Notifications__AllowedPrivateCidrs__'+i,cidr]))
 }:{};
 return {notification,receipt,environment,env:{WEBAPI_NOTIFICATION_SECRET_DIRECTORY:secretDirectory,WEBAPI_NOTIFICATION_SMTP_PORT:String(notification.ports.smtp),WEBAPI_NOTIFICATION_HTTPS_PORT:String(notification.ports.https),WEBAPI_NOTIFICATION_MANAGEMENT_PORT:String(notification.ports.management)}};
}
export async function assertNotificationSecretVolumes(state,receipt,image,{run=docker}={}){
 if(!/^.+@sha256:[a-f0-9]{64}$/.test(image??''))throw new RuntimeError('Pinned SDK required for notification volume verification.',3);
 const names=['notification-secrets','notification-fixture-secrets'].map(n=>state.projectName+'_'+n),volumes=JSON.parse((await run(['volume','inspect',...names])).stdout);if(volumes.length!==2||names.some(n=>!volumes.some(v=>v.Name===n)))throw new RuntimeError('Notification private volume missing.',3);assertOwnership(state,volumes);
 const files=Object.keys(receipt.files).sort();const result=await run(['run','--rm','--user','10001:10001','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges:true','--label','com.docker.compose.project='+state.projectName,'--label','com.webapi.runtime.owner='+state.ownerId,'--mount','type=volume,src='+names[0]+',dst=/private/sender,readonly','--mount','type=volume,src='+names[1]+',dst=/private/fixture,readonly',image,'sha256sum',...files.map(f=>'/private/'+f)]);
 const hashes=new Map(result.stdout.trim().split('\n').map(l=>{const [hash,file]=l.trim().split(/\s+/);return [file.replace('/private/',''),hash];}));if(files.some(f=>hashes.get(f)!==receipt.files[f]))throw new RuntimeError('Notification volume checksum mismatch; restore owned backup.',3);
}
export async function initializeNotificationFixture({directory,ports={smtp:57534,https:57535,management:57536}}){
 const state=await loadState(directory);return withRuntimeLock(directory,async()=>{
  const release=await loadRelease(path.join(directory,'release.json'));if(!release.applications?.['notification-fixture/WebApi.NotificationFixtureHost.dll'])throw new RuntimeError('Build and install verified notification candidate first.',3);
  let notification=await loadNotificationRuntime(directory,state);if(notification&&JSON.stringify(notification.ports)!==JSON.stringify(ports))throw new RuntimeError('Cannot change existing notification ports.',3);
  notification??={schemaVersion:1,ownerId:state.ownerId,projectName:state.projectName,enabled:true,fixtureEnabled:true,ports,secretReceipt:'notification-secrets/receipt.json'};
  await assertNotificationPorts(notification,state);await prepareNotificationSecrets({directory:path.join(directory,'notification-secrets'),owner:state.ownerId,projectName:state.projectName});await writePrivate(path.join(directory,notificationStateName),notification);
  const {prepareContext}=await import('../runtime/context.mjs');const ctx=await prepareContext(directory,state,release);await ctx.compose('run','--rm','--no-deps','init-notification-volumes');await ctx.compose('up','-d','--wait','notification-fixture');await ctx.compose('up','-d','control-plane','worker');await waitForRuntime(ctx);return {ownerId:state.ownerId,projectName:state.projectName,fixture:'Available',delivery:'NotTested'};
 });
}
export async function fixtureRequest(directory,core,url,{method='GET',body}={}){
 const state=await loadNotificationRuntime(directory,core);if(!state?.fixtureEnabled||!['/owner/observations','/owner/mode'].includes(url))throw new RuntimeError('Owned notification fixture is unavailable.',3);await loadNotificationSecrets(path.join(directory,'notification-secrets'),core.ownerId,core.projectName);
 const token=(await fs.readFile(path.join(directory,'notification-secrets/fixture/owner-token'),'utf8')).trim();const response=await fetch('http://127.0.0.1:'+state.ports.management+url,{method,headers:{'X-WebAPI-Fixture-Owner':token,...(body?{'Content-Type':'application/json'}:{})},body:body?JSON.stringify(body):undefined,signal:AbortSignal.timeout(5000)});if(!response.ok)throw new RuntimeError('Notification fixture owner request rejected: '+response.status,3);return response.json();
}
export function deriveNotificationStatus(notification,{channels=[],fixture=null}={}){const available=fixture?.binaryVerified===true&&/^[a-f0-9]{40}$/.test(fixture?.sourceRevision??'')&&fixture?.ownerId===notification?.ownerId&&fixture?.projectName===notification?.projectName;return {deployment:!notification?'Unconfigured':notification.enabled?'Configured':'Disabled',fixture:available?'Available':'Unavailable',delivery:'NotObserved',channels:channels.map(c=>({channel:c.channel,state:!c.configured?'Unconfigured':!c.enabled?'Disabled':notification?.enabled&&available?'Available':'Unavailable'}))};}
export async function captureHistoricalToolHashes(directory){
 const original=await loadState(directory);if(original.projectName!=='webapi-enterprise-local'||original.ports.console!==4192)throw new RuntimeError('Historical tools require original read-only installation identity.',3);const files={};const {createHash}=await import('node:crypto');const read=async(file)=>{const st=await fs.lstat(file);if(st.isSymbolicLink()||!st.isFile())throw new RuntimeError('Historical tool is not a regular immutable file.',3);return createHash('sha256').update(await fs.readFile(file)).digest('hex');};for(const name of ['manage.sh','tooling.json','manage-policies.sh','policy-tooling.json'])files[name]=await read(path.join(directory,name));
 for(const name of ['tooling.json','policy-tooling.json']){const manifest=JSON.parse(await fs.readFile(path.join(directory,name),'utf8')),relative=path.relative(directory,manifest.fixedDirectory);if(relative.startsWith('..')||path.isAbsolute(relative)||!['tooling','policy-tooling'].includes(relative.split(path.sep)[0]))throw new RuntimeError('Historical manifest directory is outside original installation.',3);for(const [file,expected]of Object.entries(manifest.files)){if(path.isAbsolute(file)||file.split('/').includes('..'))throw new RuntimeError('Invalid historical tooling file.',3);const hash=await read(path.join(manifest.fixedDirectory,file));if(hash!==expected)throw new RuntimeError('Historical fixed tooling checksum mismatch.',3);files[relative.replaceAll(path.sep,'/')+'/'+file]=hash;}}
 return files;
}
