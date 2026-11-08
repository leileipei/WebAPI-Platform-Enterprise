import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash,randomUUID} from 'node:crypto';
import {freezeRestoreInputs,verifyFrozenRestoreInputs,requireRestoreSourceCoverage,restoreInputNames,restoreInputSecretModes} from '../../scripts/runtime/restore-input-snapshot.mjs';
import {validateRestoreManifestScope,dataVolumes} from '../../scripts/runtime/acceptance-backup.mjs';
import {notificationBackupRequiredInputs} from '../../scripts/notifications/runtime-secrets.mjs';

const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const budget={maxSnapshotBytes:16*1024*1024,minHostFreeBytes:1024*1024,maxTargetBytes:16*1024*1024,minTargetFreeBytes:1024*1024};
async function fixture(){
 const root=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/restore-snapshot-fixtures',randomUUID());
 const source=path.join(root,'source'),parent=path.join(root,'outputs');
 await fs.mkdir(source,{recursive:true,mode:0o700});await fs.mkdir(parent,{mode:0o700});
 console.log('isolated-restore-snapshot-fixture-retained='+root);return {root,source,parent};
}

test('fixed restore inputs remain unchanged when the original volume is replaced',async()=>{
 const {source,parent}=await fixture(),volume=Buffer.from('original synthetic business volume'),secret=Buffer.from('synthetic-only-secret');
 await fs.writeFile(path.join(source,'pg.tar'),volume,{mode:0o600});await fs.mkdir(path.join(source,'secrets'),{mode:0o700});
 await fs.writeFile(path.join(source,'secrets/postgres-password'),secret,{mode:0o600});
 const frozen=await freezeRestoreInputs(source,parent,{files:{'pg.tar':hash(volume),'secrets/postgres-password':hash(secret)}},{budget});
 const index=JSON.parse(await fs.readFile(path.join(frozen.directory,'.webapi-restore-input-index.json'),'utf8'));
 assert.equal(index.files['pg.tar'].sha256,hash(volume));assert.equal(index.files['secrets/postgres-password'].sourceMode,0o600);
 await fs.rename(path.join(source,'pg.tar'),path.join(source,'original-retained.tar'));
 await fs.writeFile(path.join(source,'pg.tar'),'replacement',{mode:0o600});
 assert.deepEqual(await fs.readFile(path.join(frozen.directory,'pg.tar')),volume);
 await verifyFrozenRestoreInputs(frozen);
 await fs.chmod(path.join(frozen.directory,'pg.tar'),0o600);
 await fs.writeFile(path.join(frozen.directory,'pg.tar'),'changed copy');
 await assert.rejects(verifyFrozenRestoreInputs(frozen));
});

test('mismatched and symbolic link inputs are rejected with the original retained',async()=>{
 const {source,parent}=await fixture(),file=path.join(source,'pg.tar');await fs.writeFile(file,'synthetic',{mode:0o600});
 await assert.rejects(freezeRestoreInputs(source,parent,{files:{'pg.tar':'a'.repeat(64)}},{budget}));
 await fs.rename(file,path.join(source,'original.tar'));await fs.symlink('original.tar',file);
 await assert.rejects(freezeRestoreInputs(source,parent,{files:{'pg.tar':hash(Buffer.from('synthetic'))}},{budget}));
 assert.ok((await fs.stat(path.join(source,'original.tar'))).isFile());
});

test('every restored data volume and required metadata must have a manifest digest',()=>{
 const required=[...dataVolumes.map(name=>name+'.tar'),'runtime.json','release.json','secrets/postgres-password'];
 const files=Object.fromEntries(required.map(name=>[name,'a'.repeat(64)]));
 assert.doesNotThrow(()=>validateRestoreManifestScope({schemaVersion:1,files}));
 for(const missing of required){const incomplete={...files};delete incomplete[missing];assert.throws(()=>validateRestoreManifestScope({schemaVersion:1,files:incomplete}),/required/i);}
 const notification={...files,'notification-runtime.json':'a'.repeat(64)};
 assert.throws(()=>validateRestoreManifestScope({schemaVersion:1,files:notification}),/required/i);
 for(const name of notificationBackupRequiredInputs)notification[name]='a'.repeat(64);
 assert.doesNotThrow(()=>validateRestoreManifestScope({schemaVersion:1,files:notification}));
 assert.throws(()=>restoreInputNames({files:{'../outside':'a'.repeat(64)}}));
});

test('unlisted implicitly copied secrets and optional configuration are blocked',async()=>{
 const {source}=await fixture();await fs.mkdir(path.join(source,'secrets'),{mode:0o700});
 const secret=Buffer.from('synthetic-only-secret');await fs.writeFile(path.join(source,'secrets/postgres-password'),secret,{mode:0o600});
 await fs.writeFile(path.join(source,'secrets/extra'),'unlisted',{mode:0o600});
 const manifest={files:{'secrets/postgres-password':hash(secret)}};
 await assert.rejects(requireRestoreSourceCoverage(source,manifest,{directories:['secrets']}));
 manifest.files['secrets/extra']=hash(Buffer.from('unlisted'));
 await requireRestoreSourceCoverage(source,manifest,{directories:['secrets']});
 await fs.writeFile(path.join(source,'verification-reports.tar'),'unlisted',{mode:0o600});
 await assert.rejects(requireRestoreSourceCoverage(source,manifest,{optionalFiles:['verification-reports.tar']}));
});

test('a declared pipeline requires matching delivery metadata',()=>{
 const volumes=[...dataVolumes,'delivery-secrets','delivery-lkg-a','delivery-lkg-b'];
 const files=Object.fromEntries([...volumes.map(name=>name+'.tar'),'runtime.json','release.json','secrets/postgres-password','pipeline-runtime.json','verification-reports.tar'].map(name=>[name,'a'.repeat(64)]));
 const pipeline={stageGroups:[
  {type:'PipelineStageGroup',order:1,environmentId:'source',kind:'Source',services:['gateway-a','gateway-b'],volumes:['lkg-a','lkg-b']},
  {type:'PipelineStageGroup',order:2,environmentId:'prod',kind:'Production',services:['prod-gateway-a','prod-gateway-b'],volumes:['delivery-secrets','delivery-lkg-a','delivery-lkg-b']}
 ]};
 assert.throws(()=>validateRestoreManifestScope({schemaVersion:1,files,pipeline}),/pipeline.*delivery/i);
 files['delivery-runtime.json']='a'.repeat(64);
 const delivery={services:['prod-gateway-a','prod-gateway-b'],volumes:['delivery-secrets','delivery-lkg-a','delivery-lkg-b']};
 assert.doesNotThrow(()=>validateRestoreManifestScope({schemaVersion:1,files,pipeline,delivery}));
});

test('orphaned optional volumes and incomplete cache or login records are blocked',()=>{
 const required=[...dataVolumes.map(name=>name+'.tar'),'runtime.json','release.json','secrets/postgres-password'];
 const base=Object.fromEntries(required.map(name=>[name,'a'.repeat(64)]));
 for(const name of ['delivery-lkg-a.tar','pipeline-stage-2-secrets.tar','notification-fixture-data.tar','secrets/cache-hmac','secrets/login-protection-hmac'])
  assert.throws(()=>validateRestoreManifestScope({schemaVersion:1,files:{...base,[name]:'a'.repeat(64)}}),/unexpected|missing|required/i);
 const cache={...base,'secrets/cache-hmac':'a'.repeat(64),'cache-secret-receipt.json':'a'.repeat(64),'secrets-cache.tar':'a'.repeat(64)};
 assert.doesNotThrow(()=>validateRestoreManifestScope({schemaVersion:1,files:cache}));
});

test('restored secrets preserve their private owner permissions',async()=>{
 const {root,source,parent}=await fixture(),bytes=Buffer.from('synthetic-only-secret');
 await fs.mkdir(path.join(source,'secrets'),{mode:0o700});
 await fs.writeFile(path.join(source,'secrets/postgres-password'),bytes,{mode:0o600});
 await fs.mkdir(path.join(source,'notification-secrets/sender'),{recursive:true,mode:0o700});
 await fs.writeFile(path.join(source,'notification-secrets/sender/webhook.json'),bytes,{mode:0o600});
 const frozen=await freezeRestoreInputs(source,parent,{files:{'secrets/postgres-password':hash(bytes),'notification-secrets/sender/webhook.json':hash(bytes)}},{budget});
 const destination=path.join(root,'destination');await fs.mkdir(destination,{mode:0o700});
 await fs.cp(path.join(frozen.directory,'secrets'),path.join(destination,'secrets'),{recursive:true});
 await fs.cp(path.join(frozen.directory,'notification-secrets'),path.join(destination,'notification-secrets'),{recursive:true});
 assert.equal((await fs.stat(path.join(destination,'secrets/postgres-password'))).mode&0o777,0o400);
 await restoreInputSecretModes(destination,frozen);
 await restoreInputSecretModes(destination,frozen,'notification-secrets/');
 assert.equal((await fs.stat(path.join(destination,'secrets/postgres-password'))).mode&0o777,0o600);
 assert.equal((await fs.stat(path.join(destination,'notification-secrets/sender/webhook.json'))).mode&0o777,0o600);
 await verifyFrozenRestoreInputs(frozen);
});

test('the retained snapshot index is checked as well as the archived data',async()=>{
 const {source,parent}=await fixture(),bytes=Buffer.from('synthetic volume');
 await fs.writeFile(path.join(source,'pg.tar'),bytes,{mode:0o600});
 const frozen=await freezeRestoreInputs(source,parent,{files:{'pg.tar':hash(bytes)}},{budget});
 const index=path.join(frozen.directory,'.webapi-restore-input-index.json');
 await fs.chmod(index,0o600);await fs.writeFile(index,'{}');
 await assert.rejects(verifyFrozenRestoreInputs(frozen));
});
