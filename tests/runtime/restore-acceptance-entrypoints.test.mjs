import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {isLocalDockerEndpointBound,withLocalDockerEndpoint} from '../../scripts/runtime/docker.mjs';
import {initializeState,writePrivate} from '../../scripts/runtime/state.mjs';
import {captureOriginalRuntime} from '../../scripts/delivery/pipeline-preservation.mjs';
import {finishPipelineScenario} from '../../scripts/delivery/pipeline-scenario.mjs';

const revision='a'.repeat(40);
const restoreBudget={maxSnapshotBytes:811,minHostFreeBytes:812,maxTargetBytes:813,minTargetFreeBytes:814};
const missingDirectory='/run/webapi-missing-'+randomUUID();
const quotaSocket=path.join(missingDirectory,'quota.sock');
const dockerSocket=path.join(missingDirectory,'docker.sock');
const flags=[
 '--max-snapshot-bytes','811','--min-host-free-bytes','812',
 '--max-target-bytes','813','--min-target-free-bytes','814',
 '--restore-quota-socket',quotaSocket,'--restore-docker-socket',dockerSocket
];
const modes=['e2e','faults','browser'];
const entries=[
 {name:'promotion',script:'scripts/check-promotion.sh',modes,cli:async()=>(await import('../../scripts/delivery/cli.mjs')).promotionCli},
 {name:'pipeline',script:'scripts/check-pipeline.sh',modes,cli:async()=>(await import('../../scripts/delivery/pipeline-cli.mjs')).pipelineCli},
 {name:'security',script:'scripts/security/acceptance.mjs',modes:['--revision'],cli:async()=>(await import('../../scripts/security/acceptance.mjs')).securityAcceptanceCli}
];

for(const entry of entries){
 for(const mode of entry.modes){
  test(entry.name+' '+mode+' sends all CLI flags to real restore preflight',()=>{
   const executable=entry.name==='security'?process.execPath:'bash';
   const result=spawnSync(executable,[entry.script,mode,revision,...flags],{
    env:{...process.env,WEBAPI_NODE:process.execPath},encoding:'utf8'
   });
   assert.equal(result.error,undefined);
   assert.equal(result.status,1,result.stderr);
   assert.match(result.stderr,/requires Linux|canonical root-controlled local Unix socket/);
  });

  test(entry.name+' '+mode+' preserves mode and executes inside the verified Docker scope',async()=>{
   const cli=await entry.cli(),endpoint={path:dockerSocket},events=[];
   const storageVerifier=()=>assert.fail('the scenario owns restore verification');
   const result={passed:true};
   const deps={
    async configureStorage(options){
     assert.deepEqual(options,{restoreBudget,quotaSocket,dockerSocket});
     events.push('preflight');
     return {restoreBudget,storageVerifier,withDocker:action=>withLocalDockerEndpoint(endpoint,action)};
    },
    async runAcceptance(options){
     assert.ok(isLocalDockerEndpointBound(endpoint));
     await Promise.resolve();
     assert.ok(isLocalDockerEndpointBound(endpoint));
     assert.equal(options.storageVerifier,storageVerifier);
     assert.equal(options.restoreBudget,restoreBudget);
     assert.equal(options.sourceRevision??options.revision,revision);
     if(entry.name!=='security'){
      assert.match(path.basename(options.directory),/^webapi-enterprise-local-test-[a-f0-9-]{36}$/);
      assert.equal(path.dirname(options.directory),path.resolve('.runtime/tests'));
      assert.equal(options.browser,entry.name==='promotion'||mode!=='faults');
     }
     if(entry.name==='pipeline'){
      assert.equal(options.mode,mode);
      assert.equal(options.faults,mode!=='browser');
      assert.deepEqual(options.chains,[2,4]);
     }
     events.push('scenario');
     return result;
    }
   };
   assert.equal(await cli([mode,revision,...flags],deps),result);
   assert.deepEqual(events,['preflight','scenario']);
   assert.equal(isLocalDockerEndpointBound(),false);
   const stopped=new DOMException('test scenario cancelled','AbortError');
   deps.runAcceptance=async()=>{assert.ok(isLocalDockerEndpointBound(endpoint));throw stopped;};
   await assert.rejects(cli([mode,revision,...flags],deps),error=>error===stopped);
   assert.equal(isLocalDockerEndpointBound(),false);
  });
 }

 test(entry.name+' rejects ambiguous input and closed preflight before any setup',async()=>{
  const cli=await entry.cli(),mode=entry.modes[0],events=[];
  const unavailable=Error('fixture preflight refused');
  const deps={
   configureStorage(){events.push('preflight');throw unavailable;},
   runAcceptance(){assert.fail('scenario must not start');}
  };
  for(const args of [
   [mode,'main',...flags],[mode,revision],[mode,revision,...flags.slice(0,-2)],
   [mode,revision,...flags.slice(2)],[mode,revision,'--max-snapshot-bytes','1e3',...flags.slice(2)],
   [mode,revision,...flags,'--max-target-bytes','42'],[mode,revision,...flags,'--ignore-quota']
  ])await assert.rejects(cli(args,deps));
  assert.deepEqual(events,[]);
  await assert.rejects(cli([mode,revision,...flags],deps),error=>error===unavailable);
  assert.deepEqual(events,['preflight']);
 });
}

test('verification reads evidence without opening the restore backend',async()=>{
 for(const entry of entries.slice(0,2)){
  const cli=await entry.cli(),seen=[];
  const deps={
   configureStorage(){assert.fail('read-only verification opened the restore backend');},
   verifyDirectory(directory){seen.push(path.resolve(directory));return {passed:false};}
  };
  assert.deepEqual(await cli(['verify','evidence'],deps),{passed:false});
  await assert.rejects(cli(['verify','evidence',...flags],deps));
  assert.deepEqual(seen,[path.resolve('evidence')]);
 }
});

test('security review supports existing fixtures and scopes explicit finalization to the dedicated daemon',async()=>{
 const cli=await entries[2].cli(),endpoint={path:dockerSocket},seen=[];
 const deps={
  configureStorage(){return {restoreBudget,storageVerifier:()=>{},withDocker:action=>withLocalDockerEndpoint(endpoint,action)};},
  finalizeDirectory(directory){seen.push([directory,isLocalDockerEndpointBound(endpoint)]);return {complete:true};}
 };
 const args=['--finalize','security-fixture'];
 await cli(args,deps);
 await cli([...args,...flags],deps);
 assert.deepEqual(seen,[[path.resolve(args[1]),false],[path.resolve(args[1]),true]]);
});

test('security review cannot finalize a scoped fixture via the ambient Docker daemon',async t=>{
 const root=await fs.mkdtemp(path.join(os.tmpdir(),'webapi-security-review-scope-'));
 t.after(()=>fs.rm(root,{recursive:true,force:true}));
 const projectName='webapi-enterprise-local-test-'+randomUUID(),directory=path.join(root,projectName);
 await initializeState(directory,{projectName,bootstrapUsername:'test-admin'});
 await writePrivate(path.join(directory,'security-proof.json'),{projectName,dedicatedDocker:true});
 const cli=await entries[2].cli();
 await assert.rejects(cli(['--finalize',directory]),/dedicated Docker/);
 assert.equal((await fs.stat(directory)).isDirectory(),true);
});

test('pipeline on a dedicated daemon retains its fixture and marks original runtime preservation unknown',async()=>{
 const endpoint={path:dockerSocket},proof={isolatedAcceptance:true},closeCalls=[];
 await withLocalDockerEndpoint(endpoint,async()=>{
  const originalBefore=await captureOriginalRuntime('/absent-original-webapi-runtime');
  await finishPipelineScenario({
   c:{state:{projectName:'fixture'},close:async options=>{closeCalls.push(options);return {directoryRetained:true};}},
   proof,failed:false,output:'unused',originalBefore,deps:{writeScenarioProof:async()=>{}}
  });
 });
 assert.equal(proof.originalPreservation.status,'Unknown');
 assert.equal(proof.originalPreservation.observed,false);
 assert.equal(proof.cleanup.originalRuntimeUntouched,false);
 assert.equal(proof.isolatedAcceptance,false);
 assert.deepEqual(closeCalls,[{retainOnFailure:true}]);
});
