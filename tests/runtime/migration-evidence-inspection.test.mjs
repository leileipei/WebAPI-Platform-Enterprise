import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID} from 'node:crypto';
import * as backup from '../../scripts/runtime/acceptance-backup.mjs';

const image='sha256:'+'a'.repeat(64),hash='b'.repeat(64),contentHash='c'.repeat(64),openInput=async()=>({fd:123,close:async()=>{}});

test('backup and restore inspect archive semantics in a read-only, networkless verifier',async()=>{
  assert.equal(typeof backup.inspectMigrationEvidenceArchive,'function');
  const calls=[];
  const result=await backup.inspectMigrationEvidenceArchive('/owned/evidence.tar',image,{
    validate:async file=>calls.push(['structure',file]),hash:async()=>hash,openInput,
    run:async args=>{calls.push(args);return {stdout:JSON.stringify({schemaVersion:1,attempts:1,pendingAttempts:1,contentHash}),exitCode:0};}
  });
  assert.equal(result.sha256,hash);assert.equal(result.pendingAttempts,1);
  const args=calls[1];assert.ok(args.includes('--network'));assert.ok(args.includes('none'));assert.ok(args.includes('--read-only'));
  assert.ok(args.includes('--inspect-evidence-archive-stdin'));assert.ok(args.includes('--interactive'));assert.ok(args.includes('WEBAPI_MIGRATION_ARCHIVE_SHA256='+hash));
  assert.equal(calls.filter(call=>call[0]==='structure').length,2);
});

test('invalid semantic output, validator failure and archive replacement fail closed',async()=>{
  assert.equal(typeof backup.inspectMigrationEvidenceArchive,'function');
  const validate=async()=>{};
  await assert.rejects(backup.inspectMigrationEvidenceArchive('/owned/evidence.tar',image,{validate,hash:async()=>hash,openInput,run:async()=>({stdout:'{}',exitCode:0})}));
  await assert.rejects(backup.inspectMigrationEvidenceArchive('/owned/evidence.tar',image,{validate,hash:async()=>hash,openInput,run:async()=>{throw Error('invalid archive');}}));
  let reads=0;
  await assert.rejects(backup.inspectMigrationEvidenceArchive('/owned/evidence.tar',image,{validate,hash:async()=>++reads===1?hash:'d'.repeat(64),openInput,run:async()=>({stdout:JSON.stringify({schemaVersion:1,attempts:1,pendingAttempts:0,contentHash}),exitCode:0})}));
  await assert.rejects(backup.inspectMigrationEvidenceArchive('/owned/bad,path.tar',image,{validate,hash:async()=>hash,run:async()=>({})}));
});

test('unresolved evidence-backed restore is held before volume creation or extraction',()=>{
  assert.equal(typeof backup.migrationEvidenceRestoreHeld,'function');
  assert.equal(backup.migrationEvidenceRestoreHeld({migrationEvidenceSupported:true},{attempts:2,pendingAttempts:0}),false);
  assert.equal(backup.migrationEvidenceRestoreHeld({migrationEvidenceSupported:true},{attempts:2,pendingAttempts:2}),true);
  assert.throws(()=>backup.migrationEvidenceRestoreHeld({migrationEvidenceSupported:true},null),/inspection/i);
  assert.throws(()=>backup.migrationEvidenceRestoreHeld({migrationEvidenceSupported:false},{pendingAttempts:0}),/unexpected/i);
  assert.equal(backup.migrationEvidenceRestoreHeld({migrationEvidenceSupported:false},null),false);
});

test('only a verified fixed archive and matching database can clear an evidence restore marker',async()=>{
  assert.equal(typeof backup.verifyRestoredMigrationEvidence,'function');
  const projectName='webapi-enterprise-local-test-'+randomUUID();
  const directory=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/restore-marker-fixtures',projectName);
  await fs.mkdir(directory,{recursive:true,mode:0o700});
  console.log('retained-restore-marker-fixture='+directory);
  const state={ownerId:randomUUID(),projectName},file=path.join(directory,'restore-pending.json');
  await fs.writeFile(file,JSON.stringify({schemaVersion:1,ownerId:state.ownerId,projectName:state.projectName}),{mode:0o600});
  const frozen={path:path.join(directory,'migration-evidence-input-'+randomUUID()+'.tar'),sha256:hash,identity:{}};
  const inspection={schemaVersion:1,attempts:1,pendingAttempts:0,sha256:hash,contentHash};
  const calls=[];
  const ctx={directory,state,migrationEvidenceSupported:true,
    compose:async(...args)=>calls.push(['compose',...args]),
    raw:async(args)=>{calls.push(['raw',...args]);return {exitCode:0,stdout:JSON.stringify({schemaVersion:1,attempts:1,committedAttempts:1,contentHash:'c'.repeat(64),continuity:'Unknown'})};}};
  const options={inspect:async()=>[],verifyFrozen:async value=>{assert.equal(value,frozen);calls.push(['verify']);}};
  assert.equal(await backup.finishMigrationEvidenceRestore(directory,state,inspection,{ownerId:state.ownerId}),false);
  assert.ok((await fs.stat(file)).isFile());
  const verified=await backup.verifyRestoredMigrationEvidence(ctx,frozen,inspection,options);
  const command=calls.find(call=>call[0]==='raw');
  assert.ok(command.includes('--no-deps'));assert.ok(command.includes('--verify-evidence-restore-digest'));
  assert.ok(command.includes('WEBAPI_MIGRATION_ARCHIVE_CONTENT_SHA256='+contentHash));
  assert.ok(!command.includes('--volume'));
  assert.equal(calls.filter(call=>call[0]==='verify').length,2);
  assert.equal(await backup.finishMigrationEvidenceRestore(directory,state,inspection,verified),true);
  await assert.rejects(fs.stat(file),{code:'ENOENT'});
});

test('mismatched restored database or changed archive leaves the restore marker intact',async()=>{
  assert.equal(typeof backup.verifyRestoredMigrationEvidence,'function');
  const projectName='webapi-enterprise-local-test-'+randomUUID();
  const directory=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/restore-marker-fixtures',projectName);
  await fs.mkdir(directory,{recursive:true,mode:0o700});
  const state={ownerId:randomUUID(),projectName},file=path.join(directory,'restore-pending.json');
  await fs.writeFile(file,JSON.stringify({schemaVersion:1,ownerId:state.ownerId,projectName:state.projectName}),{mode:0o600});
  const frozen={path:path.join(directory,'migration-evidence-input-'+randomUUID()+'.tar'),sha256:hash,identity:{}};
  const inspection={schemaVersion:1,attempts:1,pendingAttempts:0,sha256:hash,contentHash};
  const ctx={directory,state,migrationEvidenceSupported:true,compose:async()=>{},raw:async()=>({exitCode:0,stdout:JSON.stringify({schemaVersion:1,attempts:1,committedAttempts:0,contentHash:'c'.repeat(64),continuity:'Unknown'})})};
  await assert.rejects(backup.verifyRestoredMigrationEvidence(ctx,frozen,inspection,{inspect:async()=>[],verifyFrozen:async()=>{}}));
  await assert.rejects(backup.verifyRestoredMigrationEvidence(ctx,frozen,inspection,{inspect:async()=>[],verifyFrozen:async()=>{throw Error('archive changed');}}));
  assert.ok((await fs.stat(file)).isFile());
});

test('evidence extraction consumes the fixed input rather than the mutable backup path',()=>{
  assert.equal(typeof backup.restoreVolumeExtractionArgs,'function');
  const projectName='webapi-enterprise-local-test-'+randomUUID();
  const directory=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/restore-marker-fixtures',projectName);
  const frozen={path:path.join(directory,'migration-evidence-input-'+randomUUID()+'.tar'),sha256:hash,identity:{}};
  const ctx={directory,state:{projectName},release:{dependencyImages:{sdk:image}},migrationEvidenceSupported:true};
  const args=backup.restoreVolumeExtractionArgs(ctx,'/owned/original-backup',['pg','migration-evidence'],frozen);
  assert.ok(args.includes('type=bind,src='+frozen.path+',dst=/verified-migration-evidence.tar,readonly'));
  assert.ok(args.at(-1).includes('tar -xf /verified-migration-evidence.tar -C /volumes/$name'));
  assert.throws(()=>backup.restoreVolumeExtractionArgs(ctx,'/owned/original-backup',['pg','migration-evidence'],null));
  assert.throws(()=>backup.restoreVolumeExtractionArgs(ctx,'/owned/original-backup',['pg','migration-evidence'],{...frozen,path:frozen.path+',foreign'}));
});

test('restore remains held when the valid archive contains an unresolved attempt',async()=>{
  assert.equal(typeof backup.finishMigrationEvidenceRestore,'function');
  const directory=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/restore-marker-fixtures',randomUUID());
  await fs.mkdir(directory,{recursive:true,mode:0o700});
  console.log('retained-restore-marker-fixture='+directory);
  const state={ownerId:randomUUID(),projectName:'webapi-isolated-'+randomUUID()},file=path.join(directory,'restore-pending.json');
  const marker=JSON.stringify({schemaVersion:1,ownerId:state.ownerId,projectName:state.projectName,sourceOwnerId:randomUUID()});
  await fs.writeFile(file,marker,{mode:0o600});
  assert.equal(await backup.finishMigrationEvidenceRestore(directory,state,{pendingAttempts:1}),false);
  assert.equal(await fs.readFile(file,'utf8'),marker);
  await assert.rejects(backup.finishMigrationEvidenceRestore(directory,{...state,ownerId:randomUUID()},{pendingAttempts:0}));
  assert.equal(await fs.readFile(file,'utf8'),marker);
});

test('a complete archive alone cannot clear the restore marker without database and extraction reconciliation',async()=>{
  const directory=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/restore-marker-fixtures',randomUUID());
  await fs.mkdir(directory,{recursive:true,mode:0o700});
  console.log('retained-restore-marker-fixture='+directory);
  const state={ownerId:randomUUID(),projectName:'webapi-isolated-'+randomUUID()},file=path.join(directory,'restore-pending.json');
  await fs.writeFile(file,JSON.stringify({schemaVersion:1,ownerId:state.ownerId,projectName:state.projectName}),{mode:0o600});
  assert.equal(await backup.finishMigrationEvidenceRestore(directory,state,{pendingAttempts:0}),false);
  assert.ok((await fs.stat(file)).isFile());
});

test('a legacy restore without a migration evidence archive can finish its existing workflow',async()=>{
  const directory=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/restore-marker-fixtures',randomUUID());
  await fs.mkdir(directory,{recursive:true,mode:0o700});
  const state={ownerId:randomUUID(),projectName:'webapi-isolated-'+randomUUID()},file=path.join(directory,'restore-pending.json');
  await fs.writeFile(file,JSON.stringify({schemaVersion:1,ownerId:state.ownerId,projectName:state.projectName}),{mode:0o600});
  assert.equal(await backup.finishMigrationEvidenceRestore(directory,state,null),true);
  await assert.rejects(fs.stat(file),{code:'ENOENT'});
});
