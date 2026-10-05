import fs from 'node:fs/promises';
import path from 'node:path';
import {LocalClient} from '../local-client.mjs';
import {loadSsoDeployment} from './sso-deployment.mjs';
import {safeFile,writePrivate,RuntimeError,withRuntimeLock} from './state.mjs';
import {prepareContext} from './context.mjs';
import {loadRelease} from './lifecycle.mjs';
import {idpContext,idpAdmin,setIdpCallback,saveSsoConnection,waitForIdp} from './keycloak-demo.mjs';
export async function localAdmin(ctx,passwordFile){
 await safeFile(passwordFile);const client=new LocalClient(`http://127.0.0.1:${ctx.runtime.ports.console}`),request=client.request.bind(client);
 client.request=(route,options={})=>request(route,{...options,headers:{...options.headers,Origin:client.base}});
 await client.login(ctx.runtime.bootstrapUsername,(await fs.readFile(passwordFile,'utf8')).trimEnd());return client;
}
export async function switchSsoConnection({runtimeDirectory,directory,enabled,passwordFile}){
 return withRuntimeLock(runtimeDirectory,async()=>{
  const ctx=await idpContext(runtimeDirectory,directory);
  if(enabled)await waitForIdp(ctx);
  else if(ctx.state.providerId){const admin=await localAdmin(ctx,passwordFile),p=await admin.request('/settings/sso/providers/'+ctx.state.providerId);if(p.enabled)await admin.request('/settings/sso/providers/'+p.id+'/disable',{method:'POST',body:{},headers:{'If-Match':'"'+p.revision+'"'}});}
  await saveSsoConnection(ctx,enabled);const release=await loadRelease(path.join(runtimeDirectory,'release.json')),platform=await prepareContext(runtimeDirectory,ctx.runtime,release);await platform.compose('up','-d','--force-recreate','--wait','control-plane');
  return {connectionEnabled:enabled,providerDisabled:!enabled&&Boolean(ctx.state.providerId),applicationImageUnchanged:true};
 });
}
export async function provisionSsoDemo({runtimeDirectory,directory,passwordFile,rebindRestored=false}){
 return withRuntimeLock(runtimeDirectory,async()=>{
  const ctx=await idpContext(runtimeDirectory,directory);if(!ctx.runtime.binding)throw new RuntimeError('SSO demo requires the existing bound local environment.');
  const connection=await loadSsoDeployment(runtimeDirectory,ctx.runtime);if(!connection?.enabled||connection.idp.ownerId!==ctx.state.ownerId)throw new RuntimeError('Connect this owned IdP before platform provisioning.');
  const admin=await localAdmin(ctx,passwordFile),idp=await idpAdmin(ctx),actual=await idp('/users/'+ctx.state.subject);if(actual.id!==ctx.state.subject||actual.username!=='sso-demo-viewer')throw new RuntimeError('IdP subject mismatch.');
  let provider=ctx.state.providerId?await admin.request('/settings/sso/providers/'+ctx.state.providerId):null;
  if(!provider){provider=await admin.request('/settings/sso/providers',{method:'POST',body:{organizationId:null,name:'本机 SSO 演示 · Keycloak',issuer:`http://localhost:${ctx.state.port}/realms/${ctx.state.realm}`,clientId:ctx.state.clientId,secretRef:'file://sso/local-demo',scopes:['openid','profile','email'],claimMapping:{displayName:'name',email:'email'}}});ctx.state.providerId=provider.id;await writePrivate(path.join(directory,'idp.json'),ctx.state);}
  if(provider.issuer!==`http://localhost:${ctx.state.port}/realms/${ctx.state.realm}`||provider.clientId!==ctx.state.clientId||provider.secretRef!=='file://sso/local-demo')throw new RuntimeError('Recorded demo provider configuration changed; review before retry.');
  await setIdpCallback(ctx,provider.id);
  const tested=await admin.request('/settings/sso/providers/'+provider.id+'/test',{method:'POST',body:{},headers:{'If-Match':'"'+provider.revision+'"'}});if(tested.status!=='Passed')throw new RuntimeError('Provider structural test failed; platform user not provisioned.');

  let user=(await admin.request('/users?search=sso-demo-viewer')).items.find(u=>u.username==='sso-demo-viewer');
  if(!user)user=await admin.request('/users/sso',{method:'POST',body:{username:'sso-demo-viewer',displayName:'本机 SSO 演示 · 只读用户',email:'sso-demo-viewer@example.test',providerId:provider.id,subject:ctx.state.subject}});
  if(rebindRestored)user=await rebindRestoredViewer(ctx,admin,user,provider);
  const binding=await admin.request('/users/'+user.id+'/external-identity');if(binding.providerId!==provider.id||binding.subject!==ctx.state.subject)throw new RuntimeError('Existing viewer binding differs; refusing account takeover.');
  const role=(await admin.request('/roles')).find(r=>r.code==='Viewer');if(!role)throw new RuntimeError('Viewer role missing.');
  await admin.request('/users/'+user.id+'/roles',{method:'PUT',body:{roleIds:[role.id]},headers:{'If-Match':'"'+user.revision+'"'}});
  user=(await admin.request('/users?search=sso-demo-viewer')).items.find(u=>u.id===user.id);
  const environment=await admin.request('/environments/'+ctx.runtime.binding.environmentId),project=await admin.request('/projects/'+environment.projectId),scope={organizationId:project.organizationId,projectId:project.id,environmentId:environment.id};
  await admin.request('/users/'+user.id+'/scopes',{method:'PUT',body:{scopes:[{scope,accessMode:'read'}]},headers:{'If-Match':'"'+user.revision+'"'}});
  if(!provider.enabled){await admin.request('/settings/sso/providers/'+provider.id+'/enable',{method:'POST',body:{},headers:{'If-Match':'"'+provider.revision+'"'}});provider=await admin.request('/settings/sso/providers/'+provider.id);}
  if(rebindRestored){const journal=JSON.parse(await fs.readFile(path.join(directory,'rebind-journal.json'),'utf8'));user=(await admin.request('/users')).items.find(u=>u.id===user.id);if(user.status!==journal.priorStatus)await admin.request('/users/'+user.id,{method:'PUT',body:{displayName:user.displayName,email:user.email,status:journal.priorStatus},headers:{'If-Match':'"'+user.revision+'"'}});await writePrivate(path.join(directory,'rebind-journal.json'),{...journal,completed:true});}
  const receipt={providerId:provider.id,userId:user.id,username:user.username,subject:ctx.state.subject,role:'Viewer',scope,accessMode:'read',providerEnabled:true,automaticDefault:false};await writePrivate(path.join(directory,'platform-receipt.json'),receipt);return receipt;
 });
}
export async function rebindRestoredViewer(ctx,admin,user,provider){
 if(!ctx.state.recovery?.sourceProviderId||user.username!=='sso-demo-viewer'||user.authSource!=='sso')throw new RuntimeError('Explicit IdP recovery and original SSO viewer required.');
 const file=path.join(ctx.directory,'rebind-journal.json');let journal;
 try{await safeFile(file);journal=JSON.parse(await fs.readFile(file,'utf8'));}catch(e){if(e.code!=='ENOENT')throw e;}
 const binding=await admin.request('/users/'+user.id+'/external-identity');
 if(binding.subject!==ctx.state.subject||![ctx.state.recovery.sourceProviderId,provider.id].includes(binding.providerId))throw new RuntimeError('Recovered subject/provider mismatch; refusing account takeover.');
 if(!journal){if(binding.providerId!==ctx.state.recovery.sourceProviderId)throw new RuntimeError('Recovery binding journal missing.');journal={userId:user.id,priorStatus:user.status,sourceProviderId:ctx.state.recovery.sourceProviderId,targetProviderId:provider.id,subject:ctx.state.subject,completed:false};await writePrivate(file,journal);}
 if(journal.userId!==user.id||journal.sourceProviderId!==ctx.state.recovery.sourceProviderId||journal.targetProviderId!==provider.id||journal.subject!==ctx.state.subject||!['Active','Disabled'].includes(journal.priorStatus))throw new RuntimeError('Recovery journal identity mismatch.');
 const old=await admin.request('/settings/sso/providers/'+journal.sourceProviderId);if(old.enabled)await admin.request('/settings/sso/providers/'+old.id+'/disable',{method:'POST',body:{},headers:{'If-Match':'"'+old.revision+'"'}});
 if(user.status!=='Disabled')await admin.request('/users/'+user.id,{method:'PUT',body:{displayName:user.displayName,email:user.email,status:'Disabled'},headers:{'If-Match':'"'+user.revision+'"'}});
 if(binding.providerId!==provider.id)await admin.request('/users/'+user.id+'/external-identity',{method:'PUT',body:{providerId:provider.id,subject:ctx.state.subject,enabled:true},headers:{'If-Match':'"'+binding.revision+'"'}});
 return (await admin.request('/users')).items.find(u=>u.id===user.id);
}
