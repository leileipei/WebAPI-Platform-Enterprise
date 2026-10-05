import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {randomUUID} from 'node:crypto';
import {initializeState, writePrivate} from '../../scripts/runtime/state.mjs';
import {prepareContext} from '../../scripts/runtime/context.mjs';
async function fixture(t) {
 const parent=await fs.mkdtemp(path.join(os.tmpdir(),'webapi-sso-deploy-'));
 t.after(()=>fs.rm(parent,{recursive:true,force:true}));
 const directory=path.join(parent,'local'),state=await initializeState(directory,{bootstrapUsername:'local-admin'});
 const release={sourceRevision:'a'.repeat(40),imageId:'sha256:'+'b'.repeat(64)};
 const deploy=path.join(directory,'artifacts',release.sourceRevision,'deploy');await fs.mkdir(deploy,{recursive:true});
 await fs.writeFile(path.join(deploy,'compose.runtime.yml'),JSON.stringify({services:{'control-plane':{environment:{ASPNETCORE_ENVIRONMENT:'Development'}}}}));
 const ownerId=randomUUID(),projectName='webapi-local-idp-'+ownerId;
 const config={schemaVersion:1,ownerId:state.ownerId,projectName:state.projectName,enabled:true,publicBaseUrl:'http://127.0.0.1:4192',idp:{ownerId,projectName,port:4193,networkAlias:'webapi-local-idp-'+ownerId.slice(0,8),secretVolume:projectName+'_client-secret'}};
 return {directory,state,release,config,file:path.join(directory,'sso-deployment.json')};
}
test('normal context regeneration retains precise local SSO and readonly secret mount',async t=>{
 const f=await fixture(t);await writePrivate(f.file,f.config);
 for(let i=0;i<2;i++){
  await prepareContext(f.directory,f.state,f.release);const overlay=JSON.parse(await fs.readFile(path.join(f.directory,'runtime-config.json'),'utf8'));
  const cp=overlay.services['control-plane'];assert.equal(cp.environment.Sso__FixtureEnabled,'true');
  assert.equal(cp.environment.Sso__PublicBaseUrl,f.config.publicBaseUrl);assert.equal(cp.environment.Sso__AllowedOrigins__0,'http://localhost:4193');
  assert.deepEqual(JSON.parse(cp.environment.Sso__FixtureConnectOverridesJson),{'http://localhost:4193':f.config.idp.networkAlias});
  assert.equal(cp.environment['Sso__SecretFiles__local-demo'],'/run/sso/client-secret');
  assert.deepEqual(cp.volumes,[{type:'volume',source:'sso-client-secret',target:'/run/sso',read_only:true}]);
  assert.deepEqual(overlay.volumes['sso-client-secret'],{external:true,name:f.config.idp.secretVolume});
 }
});
test('foreign owner and changed console origin are rejected before replacing runtime overlay',async t=>{
 const f=await fixture(t);const overlay=path.join(f.directory,'runtime-config.json');await writePrivate(overlay,{unchanged:true});
 for(const config of [{...f.config,ownerId:randomUUID()},{...f.config,publicBaseUrl:'http://external.example:4192'},{...f.config,idp:{...f.config.idp,port:4192}},{...f.config,idp:{...f.config.idp,networkAlias:'arbitrary-host'}},{...f.config,allowedOrigins:['http://anywhere.example']}]) {
  await writePrivate(f.file,config);await assert.rejects(prepareContext(f.directory,f.state,f.release),/SSO|origin|owner|port|schema|identity/i);
  assert.deepEqual(JSON.parse(await fs.readFile(overlay,'utf8')),{unchanged:true});
 }
});
test('SSO config symlink and nonprivate mode cannot be consumed',async t=>{
 const f=await fixture(t),target=path.join(f.directory,'other.json');await writePrivate(target,f.config);await fs.symlink(target,f.file);
 await assert.rejects(prepareContext(f.directory,f.state,f.release),/private|symlink/i);
 await fs.unlink(f.file);await writePrivate(f.file,f.config);await fs.chmod(f.file,0o644);
 await assert.rejects(prepareContext(f.directory,f.state,f.release),/private/i);
});
test('disabled local SSO and absent config leave normal runtime authentication intact',async t=>{
 const f=await fixture(t);for(const config of [null,{...f.config,enabled:false}]){
  if(config)await writePrivate(f.file,config);await prepareContext(f.directory,f.state,f.release);
  const overlay=JSON.parse(await fs.readFile(path.join(f.directory,'runtime-config.json'),'utf8'));
  assert.equal(overlay.services['control-plane'].environment.Sso__FixtureEnabled,undefined);
  assert.equal(overlay.services['control-plane'].environment.Authentication__CookieName,'WebApi.Local.'+f.state.ownerId+'.Session');
  assert.equal(overlay.volumes,undefined);
 }
});
test('cold backup includes protected SSO settings and reviewer credentials, restore disables live IdP binding',async t=>{
 const {copySsoBackup,restoreSsoBackup}=await import('../../scripts/runtime/sso-deployment.mjs');
 const f=await fixture(t);await writePrivate(f.file,f.config);await fs.mkdir(path.join(f.directory,'demo'),{mode:0o700});
 await writePrivate(path.join(f.directory,'demo','local-demo-api-approver.password'),'private-reviewer-password');
 const target=path.join(f.directory,'backup');await fs.mkdir(target,{mode:0o700});await copySsoBackup(f.directory,target,f.state);
 assert.deepEqual(JSON.parse(await fs.readFile(path.join(target,'sso-deployment.json'),'utf8')),f.config);
 const restored=path.join(f.directory,'restored');await fs.mkdir(restored,{mode:0o700});const state={...f.state,ownerId:randomUUID(),projectName:'webapi-enterprise-local-test-'+randomUUID(),ports:{console:14192,gatewayA:14196,gatewayB:14197}};
 await restoreSsoBackup(target,restored,state);const config=JSON.parse(await fs.readFile(path.join(restored,'sso-deployment.json'),'utf8'));
 assert.equal(config.enabled,false);assert.equal(config.ownerId,state.ownerId);assert.equal(config.publicBaseUrl,'http://127.0.0.1:14192');assert.equal(config.idp.secretVolume,f.config.idp.secretVolume);
 assert.equal(await fs.readFile(path.join(restored,'demo','local-demo-api-approver.password'),'utf8'),'private-reviewer-password');
 assert.equal((await fs.stat(path.join(restored,'sso-deployment.json'))).mode&0o777,0o600);
});
test('secret volume must belong to the exact independent IdP owner',async t=>{
 const {assertSsoSecretVolume}=await import('../../scripts/runtime/sso-deployment.mjs');const f=await fixture(t);
 const resource={Name:f.config.idp.secretVolume,Labels:{'com.docker.compose.project':f.config.idp.projectName,'com.webapi.idp.owner':f.config.idp.ownerId}};
 await assertSsoSecretVolume(f.config,async()=>({stdout:JSON.stringify([resource])}));
 await assert.rejects(assertSsoSecretVolume(f.config,async()=>({stdout:JSON.stringify([{...resource,Labels:{...resource.Labels,'com.webapi.idp.owner':randomUUID()}}])})),/owner/);
});
