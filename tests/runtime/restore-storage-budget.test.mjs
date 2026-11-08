import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash,randomUUID} from 'node:crypto';
import {validateRestoreStorageBudget,requireRestoreStorageProof,persistRestoreStorageProof} from '../../scripts/runtime/restore-storage-budget.mjs';
import {freezeRestoreInputs} from '../../scripts/runtime/restore-input-snapshot.mjs';
import {restoreColdBackup,dataVolumes} from '../../scripts/runtime/acceptance-backup.mjs';

const budget={maxSnapshotBytes:1024,minHostFreeBytes:1048576,maxTargetBytes:1048576,minTargetFreeBytes:1048576};
const args={budget,projectName:'webapi-enterprise-local-test-'+randomUUID(),directory:'/owned/test',volumeNames:['pg','lkg-a']};

test('restore storage budgets are explicit and reject invalid or overflowing limits',()=>{
 assert.deepEqual(validateRestoreStorageBudget(budget),budget);
 for(const value of [undefined,{...budget,maxSnapshotBytes:0},{...budget,minHostFreeBytes:-1},{...budget,maxTargetBytes:Number.MAX_SAFE_INTEGER}])
  assert.throws(()=>validateRestoreStorageBudget(value),/budget/i);
});

test('restore cannot proceed without a bound hard quota provider and matching scope',async()=>{
 await assert.rejects(requireRestoreStorageProof({...args,phase:'preflight'}),/quota/i);
 let quotaId='synthetic-quota-identity';const fake=async input=>({schemaVersion:1,kind:'enforced-hard-quota',phase:input.phase,projectName:input.projectName,directory:input.directory,
  quotaId,quotaBytes:budget.maxTargetBytes,availableBytes:budget.maxTargetBytes+budget.minTargetFreeBytes,
  evidenceDigest:'a'.repeat(64),volumeNames:input.volumeNames,...(input.phase!=='preflight'?{ownerId:input.ownerId,volumeIdentities:input.volumeIdentities}:{})});
 const proof=await requireRestoreStorageProof({...args,phase:'preflight',provider:fake});
 const identities={'pg':'b'.repeat(64),'lkg-a':'c'.repeat(64)},ownerId=randomUUID();
 const initialized=await requireRestoreStorageProof({...args,phase:'before-initialization',provider:fake,previous:proof,ownerId,volumeIdentities:identities});
 await requireRestoreStorageProof({...args,phase:'before-extraction',provider:fake,previous:initialized,ownerId,volumeIdentities:identities});
 await assert.rejects(requireRestoreStorageProof({...args,phase:'before-extraction',provider:fake,previous:initialized,ownerId,volumeIdentities:{...identities,pg:'d'.repeat(64)}}),/proof|identity/i);
 await assert.rejects(requireRestoreStorageProof({...args,phase:'before-extraction',previous:proof,ownerId,volumeIdentities:identities,provider:fake}),/preflight|proof/i);
 quotaId='different-quota-identity';await assert.rejects(requireRestoreStorageProof({...args,phase:'before-extraction',previous:initialized,ownerId,volumeIdentities:identities,provider:fake}),/quota/i);quotaId='synthetic-quota-identity';
 await assert.rejects(requireRestoreStorageProof({...args,phase:'preflight',provider:async input=>({...await fake(input),volumeNames:['pg']})}),/quota/i);
});

test('snapshot byte budget is enforced before a private copy is created',async()=>{
 const root=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/restore-snapshot-fixtures',randomUUID()),source=path.join(root,'source'),parent=path.join(root,'outputs');
 await fs.mkdir(source,{recursive:true,mode:0o700});await fs.mkdir(parent,{mode:0o700});console.log('isolated-budget-fixture-retained='+root);
 const bytes=Buffer.alloc(1025,1);await fs.writeFile(path.join(source,'pg.tar'),bytes,{mode:0o600});
 const manifest={files:{'pg.tar':createHash('sha256').update(bytes).digest('hex')}};
 await assert.rejects(freezeRestoreInputs(source,parent,manifest,{budget}),/budget/i);
 assert.deepEqual(await fs.readdir(parent),[]);
});

test('quota evidence is persisted without overwriting an existing record',async()=>{
 const directory=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/restore-snapshot-fixtures',randomUUID());
 await fs.mkdir(directory,{recursive:true,mode:0o700});console.log('isolated-quota-proof-fixture-retained='+directory);
 const proof={schemaVersion:1,phase:'preflight',evidenceDigest:'a'.repeat(64)};
 const file=await persistRestoreStorageProof(directory,proof),before=await fs.readFile(file,'utf8');
 await assert.rejects(persistRestoreStorageProof(directory,{...proof,evidenceDigest:'b'.repeat(64)}),{code:'EEXIST'});
 assert.equal(await fs.readFile(file,'utf8'),before);assert.equal((await fs.stat(file)).mode&0o777,0o600);
});

test('missing target quota provider blocks restore before creating its directory',async()=>{
 const trace=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace');
 const root=path.join(trace,'restore-snapshot-fixtures',randomUUID());await fs.mkdir(root,{recursive:true,mode:0o700});
 const target=path.join(root,'backup');await fs.mkdir(path.join(target,'secrets'),{recursive:true,mode:0o700});
 const revision='a'.repeat(40),ports={console:61931,gatewayA:61932,gatewayB:61933};
 const previous={schemaVersion:1,projectName:'webapi-enterprise-local-test-'+randomUUID(),ownerId:randomUUID(),ports,bootstrapUsername:'isolated-admin',binding:null,demoEnabled:false,releaseId:revision,initialized:true,bindingPhase:null,pendingConfiguration:null};
 const release={schemaVersion:1,configVersion:1,imageId:'sha256:'+'b'.repeat(64),sourceRevision:revision};
 const contents=Object.fromEntries([...dataVolumes.map(name=>[name+'.tar','synthetic']),['runtime.json',JSON.stringify(previous)],['release.json',JSON.stringify(release)],['secrets/postgres-password','synthetic']]);
 const files={};for(const [name,content] of Object.entries(contents)){await fs.writeFile(path.join(target,name),content,{mode:0o600});files[name]=createHash('sha256').update(content).digest('hex');}
 await fs.writeFile(path.join(target,'backup-manifest.json'),JSON.stringify({schemaVersion:1,sourceRevision:revision,files}),{mode:0o600});
 const projectName='webapi-enterprise-local-test-'+randomUUID(),directory=path.join(root,projectName);
 const capabilityResolver=async()=>({coreSecrets:['postgres-password'],cache:false,loginProtection:false,evidence:false,reports:false});
 await assert.rejects(restoreColdBackup({target,projectName,directory,ports,
  contextFactory:async()=>{throw Error('context must not run');},capabilityResolver,restoreBudget:budget}),/quota/i);
 assert.equal(await fs.lstat(directory).catch(error=>error.code==='ENOENT'?null:Promise.reject(error)),null);
});
