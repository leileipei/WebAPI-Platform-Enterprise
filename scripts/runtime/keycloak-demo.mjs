import {prepareContext} from './context.mjs';
import {loadRelease} from './lifecycle.mjs';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import {randomBytes,randomUUID,createHash} from 'node:crypto';
import {repository} from './build.mjs';
import {docker,inspectResources,assertOwnership} from './docker.mjs';
import {loadState,writePrivate,safeFile,ownedDirectory,validGuid,RuntimeError,withRuntimeLock} from './state.mjs';
import {validateSsoDeployment,assertIdpOwnership,ssoConfigName} from './sso-deployment.mjs';
const volumeNames=['pg','secrets-postgres','secrets-keycloak','client-secret'];
const secretNames=['postgres-password','idp-admin-password','viewer-password','unknown-password','client-secret'];
const digest=b=>createHash('sha256').update(b).digest('hex');
function fields(v,names){if(!v||typeof v!=='object'||Array.isArray(v)||Object.keys(v).some(k=>!names.includes(k)))throw new RuntimeError('Invalid IdP schema.');}
export function validateIdpState(s,runtime){
 fields(s,['schemaVersion','ownerId','projectName','runtimeOwnerId','runtimeProjectName','runtimeNetwork','port','networkAlias','realm','clientId','subject','unknownSubject','providerId','phase','recovery']);
 if(s.schemaVersion!==1||!validGuid(s.ownerId)||s.projectName!=='webapi-local-idp-'+s.ownerId||!validGuid(s.subject)||!validGuid(s.unknownSubject)||(s.providerId!==null&&!validGuid(s.providerId))||s.realm!=='webapi-local-demo'||s.clientId!=='webapi-console-local'||s.runtimeOwnerId!==runtime.ownerId||s.runtimeProjectName!==runtime.projectName||s.runtimeNetwork!==runtime.projectName+'_default'||s.networkAlias!=='webapi-local-idp-'+s.ownerId.slice(0,8))throw new RuntimeError('IdP/runtime owner or identity mismatch.');
 if(!['Prepared','SecretsReady','Restoring','Restored','Ready'].includes(s.phase))throw new RuntimeError('Invalid IdP initialization phase.');
 if(s.recovery!==null){fields(s.recovery,['sourceOwnerId','sourceProviderId']);if(!validGuid(s.recovery.sourceOwnerId)||s.recovery.sourceOwnerId===s.ownerId||(s.recovery.sourceProviderId!==null&&!validGuid(s.recovery.sourceProviderId)))throw new RuntimeError('Invalid IdP recovery identity.');}
 validateSsoDeployment({schemaVersion:1,ownerId:runtime.ownerId,projectName:runtime.projectName,enabled:true,publicBaseUrl:`http://127.0.0.1:${runtime.ports.console}`,idp:{ownerId:s.ownerId,projectName:s.projectName,port:s.port,networkAlias:s.networkAlias,secretVolume:s.projectName+'_client-secret'}},runtime);return s;
}
export async function inspectIdpResources(state,run=docker){
 const resources=new Map();
 for(const[kind,args,names]of [['container',['ps','-aq'],['keycloak','postgres','init-secrets'].map(n=>state.projectName+'-'+n+'-1')],['volume',['volume','ls','-q'],volumeNames.map(n=>state.projectName+'_'+n)],['network',['network','ls','-q'],[state.projectName+'_default']]]){
  const list=await run([...args,'--filter','label=com.docker.compose.project='+state.projectName]);const ids=list.stdout.trim().split(/\s+/).filter(Boolean);
  for(const namesToInspect of [ids,names].filter(n=>n.length)){
   const result=await run([kind,'inspect',...namesToInspect],{allowFailure:true});
   if(result.exitCode!==0&&(!(result.stderr??'').trim()||(result.stderr??'').trim().split('\n').some(line=>!/no such (?:volume|container|network|object)|(?:volume|container|network|object).*not found/i.test(line))))throw new RuntimeError('Cannot establish IdP resource ownership.',3);
   if(result.stdout.trim())for(const r of JSON.parse(result.stdout))resources.set(kind+':'+(r.Id??r.Name),{...r,Kind:kind});
  }
 }
 const result=[...resources.values()];assertIdpOwnership(state,result);return result;
}
export function renderIdpCompose(directory,s,images){
 for(const name of ['keycloak','postgres','sdk'])if(!/^\S+@sha256:[a-f0-9]{64}$/.test(images[name]))throw new RuntimeError('IdP requires locked image digests.');
 const labels={'com.webapi.idp.owner':s.ownerId};
 const script='set -eu; umask 077; '+secretNames.map(n=>`test -s /input/${n};`).join(' ')+
  'cp /input/postgres-password /pg-secrets/postgres-password; chown 999:999 /pg-secrets/postgres-password; chmod 600 /pg-secrets/postgres-password; '+
  'cp /input/idp-admin-password /kc-secrets/idp-admin-password; cp /input/postgres-password /kc-secrets/postgres-password; cp /input/realm.json /kc-secrets/realm.json; chown -R 1000:1000 /kc-secrets; chmod 700 /kc-secrets; chmod 600 /kc-secrets/*; '+
  'cp /input/client-secret /client-secret/client-secret; chown -R 10001:10001 /client-secret; chmod 700 /client-secret; chmod 600 /client-secret/client-secret;';
 const services={
  'init-secrets':{image:images.sdk,user:'0:0',labels,volumes:[{type:'bind',source:directory,target:'/input',read_only:true},'secrets-postgres:/pg-secrets','secrets-keycloak:/kc-secrets','client-secret:/client-secret'],entrypoint:['bash','-c',script]},
  postgres:{image:images.postgres,labels,restart:'unless-stopped',mem_limit:'512m',environment:{POSTGRES_USER:'keycloak',POSTGRES_DB:'keycloak',POSTGRES_PASSWORD_FILE:'/run/secrets/postgres-password'},networks:{default:{aliases:[s.networkAlias+'-postgres']}},volumes:['pg:/var/lib/postgresql/data','secrets-postgres:/run/secrets:ro'],healthcheck:{test:['CMD-SHELL','pg_isready -U keycloak -d keycloak'],interval:'2s',timeout:'3s',retries:30}},
  keycloak:{image:images.keycloak,labels,user:'1000:1000',restart:'unless-stopped',mem_limit:'768m',environment:{KC_HOSTNAME:`http://localhost:${s.port}`,KC_HOSTNAME_STRICT:'true',KC_DB:'postgres',KC_DB_URL:'jdbc:postgresql://'+s.networkAlias+'-postgres:5432/keycloak',KC_DB_USERNAME:'keycloak',KC_BOOTSTRAP_ADMIN_USERNAME:'local-idp-admin'},entrypoint:['/bin/bash','-ec','export KC_DB_PASSWORD="$(cat /run/idp/postgres-password)"; export KC_BOOTSTRAP_ADMIN_PASSWORD="$(cat /run/idp/idp-admin-password)"; exec /opt/keycloak/bin/kc.sh start-dev --import-realm --http-port='+s.port],volumes:['secrets-keycloak:/run/idp:ro','secrets-keycloak:/opt/keycloak/data/import:ro'],ports:[`127.0.0.1:${s.port}:${s.port}`],networks:{default:{},platform:{aliases:[s.networkAlias]}},depends_on:{postgres:{condition:'service_healthy'}}}
 };
 return {name:s.projectName,services,volumes:Object.fromEntries(volumeNames.map(n=>[n,{labels}])),networks:{default:{labels},platform:{external:true,name:s.runtimeNetwork}}};
}
async function images(){const normal=JSON.parse(await fs.readFile(path.join(repository,'deploy/images.lock.json'),'utf8')),idp=JSON.parse(await fs.readFile(path.join(repository,'deploy/sso-images.lock.json'),'utf8'));return{...normal,keycloak:idp.keycloak};}
async function ensurePort(port,resources){
 if(resources.some(r=>r.Kind==='container'&&Object.values(r.NetworkSettings?.Ports??{}).flat().some(p=>p?.HostIp==='127.0.0.1'&&p.HostPort===String(port))))return;
 const server=net.createServer();await new Promise((resolve,reject)=>{server.once('error',()=>reject(new RuntimeError('IdP loopback port is occupied.',3)));server.listen(port,'127.0.0.1',resolve);});await new Promise(resolve=>server.close(resolve));
}
export async function idpContext(runtimeDirectory,directory){
 const runtime=await loadState(runtimeDirectory);directory=await ownedDirectory(directory);await safeFile(path.join(directory,'owner.json'));await safeFile(path.join(directory,'idp.json'));const state=validateIdpState(JSON.parse(await fs.readFile(path.join(directory,'idp.json'),'utf8')),runtime),owner=JSON.parse(await fs.readFile(path.join(directory,'owner.json'),'utf8'));
 if(owner.ownerId!==state.ownerId||owner.projectName!==state.projectName)throw new RuntimeError('Foreign IdP directory owner.',3);
 const locked=await images();const config=path.join(directory,'compose.json');await writePrivate(config,renderIdpCompose(directory,state,locked));
 const compose=async(...args)=>{assertOwnership(runtime,await inspectResources(runtime));await inspectIdpResources(state);return docker(['compose','-p',state.projectName,'-f',config,...args]);};
 return {runtimeDirectory,directory,runtime,state,images:locked,compose};
}
async function writeRealm(ctx){
 const s=ctx.state,secret=await fs.readFile(path.join(ctx.directory,'client-secret'),'utf8');
 const realm={realm:s.realm,enabled:true,registrationAllowed:false,resetPasswordAllowed:false,rememberMe:false,sslRequired:'none',clients:[{clientId:s.clientId,enabled:true,publicClient:false,clientAuthenticatorType:'client-secret',secret:secret.trimEnd(),standardFlowEnabled:true,implicitFlowEnabled:false,directAccessGrantsEnabled:false,redirectUris:s.providerId?[`http://127.0.0.1:${ctx.runtime.ports.console}/auth/oidc/callback/${s.providerId}`]:[],webOrigins:[],defaultClientScopes:['profile','email'],attributes:{'pkce.code.challenge.method':'S256'}}],users:[]};
 for(const[id,username,file]of [[s.subject,'sso-demo-viewer','viewer-password'],[s.unknownSubject,'sso-demo-unbound','unknown-password']])realm.users.push({id,username,enabled:true,emailVerified:true,email:username+'@example.test',firstName:'本机演示',lastName:username,credentials:[{type:'password',value:(await fs.readFile(path.join(ctx.directory,file),'utf8')).trimEnd(),temporary:false}]});
 await writePrivate(path.join(ctx.directory,'realm.json'),realm);
}
export async function initializeIdp({runtimeDirectory,directory,port=4193}){
 return withRuntimeLock(runtimeDirectory,async()=>{
  const runtime=await loadState(runtimeDirectory);try{await fs.lstat(directory);throw new RuntimeError('IdP init requires a fresh directory; use start for an existing owner.');}catch(e){if(e.code!=='ENOENT')throw e;}
  const ownerId=randomUUID(),state=validateIdpState({schemaVersion:1,ownerId,projectName:'webapi-local-idp-'+ownerId,runtimeOwnerId:runtime.ownerId,runtimeProjectName:runtime.projectName,runtimeNetwork:runtime.projectName+'_default',port,networkAlias:'webapi-local-idp-'+ownerId.slice(0,8),realm:'webapi-local-demo',clientId:'webapi-console-local',subject:randomUUID(),unknownSubject:randomUUID(),providerId:null,phase:'Prepared',recovery:null},runtime);
  assertOwnership(runtime,await inspectResources(runtime));await inspectIdpResources(state);await ensurePort(port,[]);
  await fs.mkdir(directory,{mode:0o700});await writePrivate(path.join(directory,'owner.json'),{ownerId,projectName:state.projectName});await writePrivate(path.join(directory,'idp.json'),state);
  for(const name of secretNames)await writePrivate(path.join(directory,name),randomBytes(32).toString('base64url')+'\n');const ctx=await idpContext(runtimeDirectory,directory);await writeRealm(ctx);
  await resumeIdp(ctx);return {projectName:state.projectName,ownerId,issuer:`http://localhost:${port}/realms/${state.realm}`,privateDirectory:directory};
 });
}
export async function waitForIdp(ctx,timeout=180000){
 const url=`http://localhost:${ctx.state.port}/realms/${ctx.state.realm}/.well-known/openid-configuration`,until=Date.now()+timeout;
 while(Date.now()<until){try{const r=await fetch(url,{signal:AbortSignal.timeout(Math.max(1,Math.min(5000,until-Date.now())))});if(r.ok){const json=await r.json();if(json.issuer!==`http://localhost:${ctx.state.port}/realms/${ctx.state.realm}`)throw new RuntimeError('IdP issuer mismatch.');return json;}}catch(e){if(e instanceof RuntimeError)throw e;}await new Promise(r=>setTimeout(r,500));}
 throw new RuntimeError('IdP did not become ready; inspect private owned logs.',3);
}
export async function operateIdp(operation,{runtimeDirectory,directory}){
 if(!['start','restart','stop','status'].includes(operation))throw new RuntimeError('Unsupported IdP operation.');
 return withRuntimeLock(runtimeDirectory,async()=>{const ctx=await idpContext(runtimeDirectory,directory),resources=await inspectIdpResources(ctx.state);if(operation==='status')return{projectName:ctx.state.projectName,containers:resources.filter(r=>r.Kind==='container').map(r=>({name:r.Name,state:r.State.Status})),volumeCount:resources.filter(r=>r.Kind==='volume').length};
  if(operation==='stop')await ctx.compose('stop','keycloak','postgres');else{await ensurePort(ctx.state.port,resources);if(operation==='restart')await ctx.compose('stop','keycloak','postgres');await resumeIdp(ctx);}return {projectName:ctx.state.projectName,operation};});
}
export async function idpAdmin(ctx){
 await safeFile(path.join(ctx.directory,'idp-admin-password'));const body=new URLSearchParams({grant_type:'password',client_id:'admin-cli',username:'local-idp-admin',password:(await fs.readFile(path.join(ctx.directory,'idp-admin-password'),'utf8')).trimEnd()});
 const r=await fetch(`http://127.0.0.1:${ctx.state.port}/realms/master/protocol/openid-connect/token`,{method:'POST',body,signal:AbortSignal.timeout(10000)});if(!r.ok)throw new RuntimeError('IdP admin authentication failed.');const token=(await r.json()).access_token;
 return async(route,options={})=>{const r=await fetch(`http://127.0.0.1:${ctx.state.port}/admin/realms/${ctx.state.realm}${route}`,{...options,signal:AbortSignal.timeout(10000),headers:{Authorization:'Bearer '+token,'Content-Type':'application/json',...options.headers},body:options.body===undefined?undefined:JSON.stringify(options.body)});if(!r.ok)throw new RuntimeError('IdP admin operation failed: '+r.status);return r.status===204?null:r.json();};
}
export async function setIdpCallback(ctx,providerId){
 if(!validGuid(providerId))throw new RuntimeError('Invalid SSO provider ID.');const admin=await idpAdmin(ctx),clients=await admin('/clients?clientId='+ctx.state.clientId);if(clients.length!==1)throw new RuntimeError('IdP client identity mismatch.');
 const client=await admin('/clients/'+clients[0].id);await admin('/clients/'+client.id,{method:'PUT',body:{...client,redirectUris:[`http://127.0.0.1:${ctx.runtime.ports.console}/auth/oidc/callback/${providerId}`]}});ctx.state.providerId=providerId;await writePrivate(path.join(ctx.directory,'idp.json'),ctx.state);await writeRealm(ctx);
}
export async function saveSsoConnection(ctx,enabled=true){
 const config=validateSsoDeployment({schemaVersion:1,ownerId:ctx.runtime.ownerId,projectName:ctx.runtime.projectName,enabled,publicBaseUrl:`http://127.0.0.1:${ctx.runtime.ports.console}`,idp:{ownerId:ctx.state.ownerId,projectName:ctx.state.projectName,port:ctx.state.port,networkAlias:ctx.state.networkAlias,secretVolume:ctx.state.projectName+'_client-secret'}},ctx.runtime);await writePrivate(path.join(ctx.runtimeDirectory,ssoConfigName),config);return config;
}
const backupFiles=['owner.json','idp.json',...secretNames,'realm.json',...volumeNames.map(n=>n+'.tar')];
export async function backupIdp({runtimeDirectory,directory,target}){
 return withRuntimeLock(runtimeDirectory,async()=>{const ctx=await idpContext(runtimeDirectory,directory),resources=await inspectIdpResources(ctx.state);if(ctx.state.phase!=='Ready')throw new RuntimeError('IdP backup requires completed initialization or restore.');for(const name of volumeNames)if(!resources.some(r=>r.Kind==='volume'&&r.Name===ctx.state.projectName+'_'+name))throw new RuntimeError('IdP backup requires all owned volumes.');await fs.mkdir(target,{mode:0o700});
  try{await ctx.compose('stop','keycloak','postgres');
   const args=['run','--rm','--user','0:0','--mount',`type=bind,src=${target},dst=/backup`];for(const name of volumeNames)args.push('--mount',`type=volume,src=${ctx.state.projectName}_${name},dst=/volumes/${name},readonly`);args.push(ctx.images.sdk,'bash','-c','set -e; umask 077; for name in '+volumeNames.join(' ')+'; do tar --numeric-owner --exclude=./.webapi-idp-restore --exclude=./.webapi-idp-stage -cf /backup/$name.tar -C /volumes/$name .; done');await docker(args);
   const hashes={};for(const name of backupFiles){if(!name.endsWith('.tar')){await safeFile(path.join(directory,name));await fs.copyFile(path.join(directory,name),path.join(target,name));}await fs.chmod(path.join(target,name),0o600);hashes[name]=digest(await fs.readFile(path.join(target,name)));}await writePrivate(path.join(target,'backup-manifest.json'),{schemaVersion:1,ownerId:ctx.state.ownerId,projectName:ctx.state.projectName,files:hashes});return {projectName:ctx.state.projectName,files:Object.keys(hashes),hashes};
  }finally{await ctx.compose('up','-d','--wait','postgres');await ctx.compose('up','-d','keycloak');await waitForIdp(ctx);}
 });
}
export async function restoreIdp({runtimeDirectory,directory,target,port},deps={}){
 return withRuntimeLock(runtimeDirectory,async()=>{
  const runtime=await loadState(runtimeDirectory);try{await fs.lstat(directory);throw new RuntimeError('IdP restore requires fresh directory.');}catch(e){if(e.code!=='ENOENT')throw e;}
  const {manifest}=await validateIdpBackup(target);
  const previous=JSON.parse(await fs.readFile(path.join(target,'idp.json'),'utf8'));if(previous.ownerId!==manifest.ownerId||previous.projectName!==manifest.projectName)throw new RuntimeError('IdP backup owner mismatch.');const ownerId=randomUUID(),state=validateIdpState({...previous,ownerId,projectName:'webapi-local-idp-'+ownerId,runtimeOwnerId:runtime.ownerId,runtimeProjectName:runtime.projectName,runtimeNetwork:runtime.projectName+'_default',port,networkAlias:'webapi-local-idp-'+ownerId.slice(0,8),providerId:null,phase:'Restoring',recovery:{sourceOwnerId:previous.ownerId,sourceProviderId:previous.providerId}},runtime);
  await inspectIdpResources(state);await ensurePort(port,[]);await fs.mkdir(directory,{mode:0o700});await writePrivate(path.join(directory,'owner.json'),{ownerId,projectName:state.projectName});await writePrivate(path.join(directory,'idp.json'),state);for(const name of [...secretNames,'realm.json'])await writePrivate(path.join(directory,name),await fs.readFile(path.join(target,name),'utf8'));
  await writePrivate(path.join(directory,'restore-input.json'),{target:await ownedDirectory(target)});const ctx=await idpContext(runtimeDirectory,directory);await resumeIdp(ctx,deps);return {ownerId,projectName:state.projectName,hashesVerified:true,providerId:null};
 });
}
// Only a disposable platform owner can remove its linked IdP rehearsal resources.
export async function cleanupRestoredIdp({runtimeDirectory,directory}){
 const ctx=await idpContext(runtimeDirectory,directory);if(!/^webapi-enterprise-local-test-[a-f0-9-]{36}$/.test(ctx.runtime.projectName))throw new RuntimeError('Persistent IdP cannot be automatically deleted.');await inspectIdpResources(ctx.state);await saveSsoConnection(ctx,false);const platform=await prepareContext(runtimeDirectory,ctx.runtime,await loadRelease(path.join(runtimeDirectory,'release.json')));await platform.compose('stop','control-plane');await platform.compose('rm','-f','control-plane');await ctx.compose('down','--volumes');const resources=await inspectIdpResources(ctx.state);if(resources.length)throw new RuntimeError('IdP cleanup incomplete.');await fs.rm(directory,{recursive:true});return{projectName:ctx.state.projectName,resourcesRemaining:0};
}
async function validateIdpBackup(target){
 target=await ownedDirectory(target);await safeFile(path.join(target,'backup-manifest.json'));const manifest=JSON.parse(await fs.readFile(path.join(target,'backup-manifest.json'),'utf8'));
 if(manifest.schemaVersion!==1||Object.keys(manifest.files).sort().join('|')!==backupFiles.slice().sort().join('|'))throw new RuntimeError('Invalid IdP backup manifest.');
 for(const name of backupFiles){await safeFile(path.join(target,name));if(digest(await fs.readFile(path.join(target,name)))!==manifest.files[name])throw new RuntimeError('IdP backup checksum mismatch.');}return {target,manifest};
}
export async function resumeIdp(ctx,{wait=waitForIdp,inspect=inspectIdpResources,run=docker}={}){
 if(ctx.state.phase==='Prepared'){
  for(const name of [...secretNames,'realm.json'])await safeFile(path.join(ctx.directory,name));
  await ctx.compose('run','--rm','--no-deps','init-secrets');ctx.state.phase='SecretsReady';await writePrivate(path.join(ctx.directory,'idp.json'),ctx.state);
 }
 if(ctx.state.phase==='Restoring'){
  await safeFile(path.join(ctx.directory,'restore-input.json'));const {target}=JSON.parse(await fs.readFile(path.join(ctx.directory,'restore-input.json'),'utf8')),backup=await validateIdpBackup(target);
  if(backup.manifest.ownerId!==ctx.state.recovery?.sourceOwnerId&&ctx.state.recovery!==null)throw new RuntimeError('IdP resume backup owner mismatch.');
  const resources=await inspect(ctx.state);if(resources.some(r=>r.Kind==='container'&&r.State?.Running))throw new RuntimeError('Cannot continue IdP restore after services have started.');
  await ctx.compose('create','init-secrets','postgres','keycloak');
  for(const name of volumeNames){
   const expected=ctx.state.ownerId+':'+backup.manifest.files[name+'.tar'];
   const script=`set -euo pipefail; marker=/volume/.webapi-idp-restore; stage=/volume/.webapi-idp-stage; expected='${expected}';
if [ -f "$marker" ]; then test "$(cat "$marker")" = "$expected:done" && exit 0; test "$(cat "$marker")" = "$expected:staging"; else test -z "$(ls -A /volume)"; printf '%s:staging' "$expected" > "$marker"; chmod 600 "$marker"; fi
mkdir -p "$stage/data"; if [ ! -f "$stage/extracted" ]; then tar -xf /backup/${name}.tar --exclude=./.webapi-idp-restore --exclude=./.webapi-idp-stage -C "$stage/data"; touch "$stage/extracted"; fi
shopt -s dotglob nullglob; for entry in "$stage/data/"*; do name=$(basename "$entry"); if [ -e "/volume/$name" ]; then diff -rq "$entry" "/volume/$name"; rm -rf "$entry"; else mv "$entry" "/volume/$name"; fi; done
chown --reference="$stage/data" /volume; chmod --reference="$stage/data" /volume; rm -rf "$stage"; printf '%s:done' "$expected" > "$marker"; chmod 600 "$marker";`;
   await inspect(ctx.state);await run(['run','--rm','--user','0:0','--mount',`type=bind,src=${backup.target},dst=/backup,readonly`,'--mount',`type=volume,src=${ctx.state.projectName}_${name},dst=/volume`,ctx.images.sdk,'bash','-c',script]);
  }
  ctx.state.phase='Restored';await writePrivate(path.join(ctx.directory,'idp.json'),ctx.state);
 }
 await ctx.compose('up','-d','--wait','postgres');await ctx.compose('up','-d','keycloak');await wait(ctx);ctx.state.phase='Ready';await writePrivate(path.join(ctx.directory,'idp.json'),ctx.state);
}
