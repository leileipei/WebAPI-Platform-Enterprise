import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs/promises';import path from 'node:path';import {randomUUID,createHash} from 'node:crypto';
import {initializeRuntime,operateRuntime} from '../../../scripts/runtime/lifecycle.mjs';import {prepareContext} from '../../../scripts/runtime/context.mjs';import {inspectResources,assertOwnership} from '../../../scripts/runtime/docker.mjs';import {freePorts} from '../../../scripts/runtime/acceptance.mjs';import {getRuntimeStatus} from '../../../scripts/runtime/status.mjs';import {resolveOwnedConsoleProxy,synchronizeConsoleProxyTrust} from '../../../scripts/security/proxy-trust.mjs';
test('actual immutable Console recreation requires exact proxy registration without secret rotation',async()=>{
 const projectName='webapi-enterprise-local-test-'+randomUUID(),directory=path.resolve('.runtime/tests/'+projectName),releaseFile=path.resolve(process.env.WEBAPI_SECURITY_RELEASE_FILE??'.runtime/security-build/release.json');let ctx,passed=false;
 try{
  const state=await initializeRuntime({directory,projectName,ports:await freePorts(),bootstrapUsername:'security-proxy-fixture',releaseFile});
  const release=JSON.parse(await fs.readFile(releaseFile,'utf8'));ctx=await prepareContext(directory,state,release);assert.equal(ctx.securitySupported,true);
  const file=path.join(directory,'secrets/login-protection-hmac'),hash=createHash('sha256').update(await fs.readFile(file)).digest('hex'),original=await resolveOwnedConsoleProxy(state);
  assert.equal((await getRuntimeStatus(state,{directory})).loginProtection.state,'Ready');
  await ctx.compose('up','-d','--no-deps','--force-recreate','console');const replaced=await resolveOwnedConsoleProxy(state);assert.notEqual(replaced.containerId,original.containerId);
  assert.notEqual((await getRuntimeStatus(state,{directory})).loginProtection.state,'Ready');
  await synchronizeConsoleProxyTrust(ctx);assert.equal((await getRuntimeStatus(state,{directory})).loginProtection.state,'Ready');
  await operateRuntime('restart',state,{directory});assert.equal((await getRuntimeStatus(state,{directory})).loginProtection.state,'Ready');assert.equal(createHash('sha256').update(await fs.readFile(file)).digest('hex'),hash);passed=true;
 }finally{
  if(ctx&&passed){assertOwnership(ctx.state,await inspectResources(ctx.state));await ctx.compose('--profile','tools','down','--volumes','--remove-orphans');assert.equal((await inspectResources(ctx.state)).length,0);await fs.rm(directory,{recursive:true,force:true});}
  if(!passed)console.error('Security proxy fixture retained for diagnosis: '+projectName);
 }
});
