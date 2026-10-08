import test from'node:test';import assert from'node:assert/strict';import fs from'node:fs/promises';import path from'node:path';import{randomUUID,randomBytes}from'node:crypto';
import{initializeState}from'../../../scripts/runtime/state.mjs';import{docker,inspectResources,assertOwnership}from'../../../scripts/runtime/docker.mjs';
import {ensureCacheSecret,assertCacheSecretVolume} from '../../../scripts/gateway-policies/runtime-secrets.mjs';
test('secretsReadableByNonRootAndNotWorldReadable; source volumes survive restart',async()=>{
 const project='webapi-enterprise-local-test-'+randomUUID(),directory=path.resolve('.runtime/tests/'+project);const state=await initializeState(directory,{projectName:project,bootstrapUsername:'volume-test-admin'});const input=path.join(directory,'secrets');await fs.mkdir(input,{mode:0o700});for(const name of ['postgres-password','node-a','node-b','ip-hmac','cursor-signing','bootstrap-password'])await fs.writeFile(path.join(input,name),randomBytes(32).toString('base64'),{mode:0o600});
 const cache=await ensureCacheSecret(directory,state);
 const release=JSON.parse(await fs.readFile(process.env.WEBAPI_RUNTIME_TEST_RELEASE??'.runtime/local-build/release.json','utf8'));const env={...process.env,WEBAPI_RUNTIME_PROJECT:project,WEBAPI_RUNTIME_OWNER:state.ownerId,WEBAPI_RUNTIME_IMAGE:release.imageId,WEBAPI_RUNTIME_SECRET_DIRECTORY:input,WEBAPI_RUNTIME_ENVIRONMENT_ID:randomUUID()};const files=['deploy/compose.runtime.yml','deploy/compose.runtime.gateways.yml'];
 const compose=async(...args)=>{assertOwnership(state,await inspectResources(state));return docker(['compose',...files.flatMap(f=>['-f',f]),...args],{env});};
 try{
  await compose('run','--rm','--no-deps','init-volumes');await assertCacheSecretVolume(state,cache.receipt,release.dependencyImages.sdk);
  for(const [role,secret]of [['gateway-a','node-a'],['gateway-b','node-b'],['worker','postgres-password']]){const script=`test "$(stat -c '%a:%u' /run/secrets/${secret})" = 600:10001; test -r /run/secrets/${secret}; `+(role.startsWith('gateway')?'test ! -e /run/secrets/postgres-password; test ! -e /run/secrets/'+(role==='gateway-a'?'node-b':'node-a'):'');await compose('run','--rm','--no-deps','--entrypoint','bash',role,'-c','set -e; '+script);}
  await compose('up','-d','postgres','redis','collector','prometheus','loki','tempo');
  const urls=['http://collector:13133','http://prometheus:9090/-/ready','http://loki:3100/ready','http://tempo:3200/ready'];
  for(const url of urls){const until=Date.now()+90000;while(true){const probe=await docker(['compose',...files.flatMap(f=>['-f',f]),'run','--rm','--no-deps','--entrypoint','dotnet','console','/app/runtime-tool/WebApi.RuntimeTool.dll','probe',url],{env,allowFailure:true});if(probe.exitCode===0)break;if(Date.now()>until)throw Error('Source never ready: '+url);await new Promise(r=>setTimeout(r,1000));}}
  await compose('run','--rm','--no-deps','--entrypoint','bash','init-volumes','-c','set -e; for name in loki-data tempo-data prometheus-data; do printf persistence-check > /volumes/$name/runtime-persistence-check; done');
  await compose('restart','loki','tempo','prometheus');await compose('run','--rm','--no-deps','--entrypoint','bash','init-volumes','-c','set -e; for name in loki-data tempo-data prometheus-data; do test -s /volumes/$name/runtime-persistence-check; done');
 }finally{
  assert.match(project,/^webapi-enterprise-local-test-[a-f0-9-]{36}$/);assertOwnership(state,await inspectResources(state));await compose('--profile','tools','down','--volumes','--remove-orphans');assert.equal((await inspectResources(state)).length,0);await fs.rm(directory,{recursive:true,force:true});
 }
});
