import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs/promises';import os from 'node:os';import path from 'node:path';import {randomUUID} from 'node:crypto';
const moduleUrl='../../scripts/runtime/keycloak-demo.mjs';
function state(){const ownerId=randomUUID();return {schemaVersion:1,ownerId,projectName:'webapi-local-idp-'+ownerId,runtimeOwnerId:randomUUID(),runtimeProjectName:'webapi-enterprise-local',runtimeNetwork:'webapi-enterprise-local_default',port:4193,networkAlias:'webapi-local-idp-'+ownerId.slice(0,8),realm:'webapi-local-demo',clientId:'webapi-console-local',subject:randomUUID(),unknownSubject:randomUUID(),providerId:null,phase:'Ready',recovery:null};}
test('IdP render publishes only loopback, keeps database private, and uses private files not secret environment values',async()=>{
 const {renderIdpCompose}=await import(moduleUrl),s=state();const compose=renderIdpCompose('/private/local',s,{keycloak:'quay.io/keycloak/keycloak@sha256:'+'a'.repeat(64),postgres:'postgres@sha256:'+'b'.repeat(64),sdk:'sdk@sha256:'+'c'.repeat(64)});
 assert.deepEqual(compose.services.keycloak.ports,['127.0.0.1:4193:4193']);assert.equal(compose.services.postgres.ports,undefined);
 assert.equal(compose.services.keycloak.environment.KC_DB,'postgres');assert.equal(compose.services.keycloak.environment.KC_DB_URL,'jdbc:postgresql://'+s.networkAlias+'-postgres:5432/keycloak');assert.equal(compose.services.keycloak.environment.KC_BOOTSTRAP_ADMIN_PASSWORD,undefined);assert.equal(compose.services.keycloak.environment.KC_DB_PASSWORD,undefined);
 assert.ok(compose.services.keycloak.entrypoint.join(' ').includes('/run/idp/idp-admin-password'));
 assert.equal(compose.services.keycloak.user,'1000:1000');assert.equal(compose.services.keycloak.restart,'unless-stopped');
 assert.equal(compose.volumes.pg.labels['com.webapi.idp.owner'],s.ownerId);assert.equal(compose.networks.platform.external,true);
});
test('foreign exact-name IdP resource is rejected before compose action',async t=>{
 const {inspectIdpResources}=await import(moduleUrl),s=state();const calls=[];
 const run=async args=>{calls.push(args);if(args[0]==='volume'&&args[1]==='inspect')return{stdout:JSON.stringify([{Name:s.projectName+'_pg',Labels:{'com.docker.compose.project':s.projectName,'com.webapi.idp.owner':randomUUID()}}]),exitCode:0};return{stdout:args[1]==='inspect'?'[]':'',stderr:'',exitCode:0};};
 await assert.rejects(inspectIdpResources(s,run),/owner/);assert.ok(calls.every(a=>!a.includes('up')&&!a.includes('down')));
});
test('IdP state rejects foreign platform association and unapproved fields',async()=>{
 const {validateIdpState}=await import(moduleUrl),s=state();assert.equal(validateIdpState(s,{ownerId:s.runtimeOwnerId,projectName:s.runtimeProjectName,ports:{console:4192,gatewayA:4196,gatewayB:4197}}),s);
 assert.throws(()=>validateIdpState({...s,runtimeOwnerId:randomUUID()},{ownerId:s.runtimeOwnerId,projectName:s.runtimeProjectName,ports:{console:4192,gatewayA:4196,gatewayB:4197}}),/owner/);
 assert.throws(()=>validateIdpState({...s,port:4192},{ownerId:s.runtimeOwnerId,projectName:s.runtimeProjectName,ports:{console:4192,gatewayA:4196,gatewayB:4197}}),/port/);
 assert.throws(()=>validateIdpState({...s,arbitraryOrigins:['*']},{ownerId:s.runtimeOwnerId,projectName:s.runtimeProjectName,ports:{console:4192,gatewayA:4196,gatewayB:4197}}),/schema/);
});
test('failed secret initialization resumes with identical private files and completed starts do not recopy secrets',async t=>{
 const {resumeIdp}=await import(moduleUrl);const parent=await fs.mkdtemp(path.join(os.tmpdir(),'webapi-idp-resume-'));t.after(()=>fs.rm(parent,{recursive:true,force:true}));
 const s={...state(),phase:'Prepared',recovery:null},calls=[],ctx={directory:parent,state:s,compose:async(...a)=>{calls.push(a);if(calls.length===1)throw Error('initialization interrupted');}};
 for(const name of ['postgres-password','idp-admin-password','viewer-password','unknown-password','client-secret','realm.json'])await fs.writeFile(path.join(parent,name),'unchanged-input',{mode:0o600});
 await assert.rejects(resumeIdp(ctx,{wait:async()=>{}}),/interrupted/);
 const before=await fs.readFile(path.join(parent,'client-secret'),'utf8');await resumeIdp(ctx,{wait:async()=>{}});assert.equal(s.phase,'Ready');assert.equal(await fs.readFile(path.join(parent,'client-secret'),'utf8'),before);
 const copied=calls.filter(a=>a.includes('init-secrets')).length;await resumeIdp(ctx,{wait:async()=>{}});assert.equal(calls.filter(a=>a.includes('init-secrets')).length,copied);
});
test('partial volume restore failure keeps services stopped and can continue after checksums are revalidated',async t=>{
 const {resumeIdp}=await import(moduleUrl),{createHash}=await import('node:crypto');const parent=await fs.mkdtemp(path.join(os.tmpdir(),'webapi-idp-restore-'));t.after(()=>fs.rm(parent,{recursive:true,force:true}));await fs.chmod(parent,0o700);
 const target=path.join(parent,'backup');await fs.mkdir(target,{mode:0o700});const files={};
 for(const name of ['owner.json','idp.json','postgres-password','idp-admin-password','viewer-password','unknown-password','client-secret','realm.json','pg.tar','secrets-postgres.tar','secrets-keycloak.tar','client-secret.tar']){const content='protected-backup-'+name;await fs.writeFile(path.join(target,name),content,{mode:0o600});files[name]=createHash('sha256').update(content).digest('hex');}
 const sourceOwnerId=randomUUID();await fs.writeFile(path.join(target,'backup-manifest.json'),JSON.stringify({schemaVersion:1,ownerId:sourceOwnerId,files}),{mode:0o600});
 await fs.writeFile(path.join(parent,'restore-input.json'),JSON.stringify({target}),{mode:0o600});
 const s={...state(),phase:'Restoring',recovery:{sourceOwnerId,sourceProviderId:randomUUID()}},calls=[],ctx={directory:parent,state:s,images:{sdk:'sdk@sha256:'+'a'.repeat(64)},compose:async(...args)=>{calls.push(args);}};
 let extracted=0;await assert.rejects(resumeIdp(ctx,{inspect:async()=>[],wait:async()=>{},run:async()=>{if(++extracted===2)throw Error('volume extraction interrupted');}}),/interrupted/);
 assert.equal(s.phase,'Restoring');assert.ok(!calls.some(a=>a.includes('up')));
 await resumeIdp(ctx,{inspect:async()=>[],wait:async()=>{},run:async()=>{}});assert.equal(s.phase,'Ready');assert.equal(JSON.parse(await fs.readFile(path.join(parent,'idp.json'),'utf8')).phase,'Ready');
});
