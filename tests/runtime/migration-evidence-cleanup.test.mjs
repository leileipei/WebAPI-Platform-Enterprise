import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID} from 'node:crypto';
import {cleanupFixture,validateAcceptance} from '../../scripts/runtime/acceptance.mjs';

test('evidence-enabled acceptance stops only its containers and retains every volume and directory',async()=>{
 const projectName='webapi-enterprise-local-test-'+randomUUID(),ownerId=randomUUID();
 const directory=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/cleanup-fixtures',projectName);
 await fs.mkdir(path.join(directory,'secrets'),{recursive:true,mode:0o700});await fs.writeFile(path.join(directory,'secrets','synthetic'),'retained',{mode:0o600});
 console.log('retained-cleanup-fixture='+directory);
 const state={projectName,ownerId},labels={'com.docker.compose.project':projectName,'com.webapi.runtime.owner':ownerId};
 const volumes=['migration-evidence','pg'].map(name=>({Kind:'volume',Name:projectName+'_'+name,Labels:{...labels,...(name==='migration-evidence'?{'com.docker.compose.volume':name,'com.webapi.migration.evidence-nonce':'a'.repeat(64)}:{})}}));
 const container={Kind:'container',Config:{Labels:labels}};const calls=[];let inspections=0;
 const result=await cleanupFixture(directory,null,{loadState:async()=>state,inspectResources:async()=>++inspections===1?[...volumes,container]:volumes,
  loadRelease:async()=>({}),prepareContext:async()=>({migrationEvidenceSupported:true,compose:async(...args)=>calls.push(args)})});
 assert.equal(result.retainedForEvidence,true);assert.equal(result.volumesRemaining,2);assert.ok(result.retainedVolumeNames.includes(projectName+'_migration-evidence'));
 assert.equal(calls.length,1);assert.ok(!calls[0].includes('--volumes'));assert.equal(await fs.readFile(path.join(directory,'secrets','synthetic'),'utf8'),'retained');
 const proof={projectName,chain:{passed:true},restart:{passed:true},failure:{passed:true},backupRestore:{passed:true},ui:{verified:true},preservedOldEnvironment:{unchanged:true},cleanup:result};
 assert.doesNotThrow(()=>validateAcceptance(proof));
 assert.throws(()=>validateAcceptance({...proof,cleanup:{...result,retainedVolumeNames:[]}}),/cleanup|evidence/i);
});

test('an evidence-enabled fixture with a missing volume is preserved and never removed',async()=>{
 const projectName='webapi-enterprise-local-test-'+randomUUID(),ownerId=randomUUID(),directory=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/cleanup-fixtures',projectName),calls=[];
 await fs.mkdir(directory,{recursive:true,mode:0o700});console.log('retained-cleanup-fixture='+directory);
 await assert.rejects(cleanupFixture(directory,null,{loadState:async()=>({projectName,ownerId}),inspectResources:async()=>[],loadRelease:async()=>({}),
  prepareContext:async()=>({migrationEvidenceSupported:true,compose:async(...args)=>calls.push(args)})}),/evidence|missing/i);
 assert.deepEqual(calls,[]);
});
