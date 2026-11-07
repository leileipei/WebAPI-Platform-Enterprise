import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID,createHash} from 'node:crypto';
import {parseRestoreManifest,readRestoreManifest} from '../../scripts/runtime/restore-manifest.mjs';
import {resolveRestoreReleaseCapabilities,verifyRestoreRevisionIdentity} from '../../scripts/runtime/restore-release-capabilities.mjs';

test('restore manifest rejects duplicate keys, including escaped equivalent names',()=>{
 assert.throws(()=>parseRestoreManifest('{"files":{},"files":{}}'),/duplicate/i);
 assert.throws(()=>parseRestoreManifest(String.raw`{"files":{"node-a":"one","node-\u0061":"two"}}`),/duplicate/i);
 assert.deepEqual(parseRestoreManifest('{"files":{"x":"a"},"schemaVersion":1}'),{files:{x:'a'},schemaVersion:1});
});

test('restore manifest refuses a hard link and retains the original',async()=>{
 const directory=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/restore-snapshot-fixtures',randomUUID());
 await fs.mkdir(directory,{recursive:true,mode:0o700});console.log('isolated-manifest-fixture-retained='+directory);
 const file=path.join(directory,'manifest.json'),alias=path.join(directory,'alias.json');await fs.writeFile(file,'{"schemaVersion":1}',{mode:0o600});await fs.link(file,alias);
 await assert.rejects(readRestoreManifest(file),/unsafe/i);assert.ok((await fs.lstat(alias)).isFile());
});

test('fixed source revision determines core secret and platform capabilities',async()=>{
 const release={sourceRevision:'a'.repeat(40)},compose={volumes:{'secrets-cache':{},'migration-evidence':{},'verification-reports':{}},services:{'control-plane':{environment:{Authentication__LoginProtection__HmacSecretFile:'/run/secrets/login-protection-hmac'}}}};
 const script='copy_secret() { :; }\npostgres) copy_secret postgres-password x y ;;\ncontrol-plane) for secret in node-a node-b ip-hmac cursor-signing login-protection-hmac; do copy_secret "$secret" x y; done ;;\ncopy_secret cache-hmac cache 10001\n';
 const contracts={[createHash('sha256').update(script).digest('hex')]:['cache-hmac','cursor-signing','ip-hmac','login-protection-hmac','node-a','node-b','postgres-password']};
 const readAt=async(_revision,file)=>file.endsWith('compose.runtime.yml')?JSON.stringify(compose):script;
 const result=await resolveRestoreReleaseCapabilities(release,{readAt,contracts});
 assert.deepEqual(result.coreSecrets,['cache-hmac','cursor-signing','ip-hmac','login-protection-hmac','node-a','node-b','postgres-password']);
 assert.equal(result.cache,true);assert.equal(result.loginProtection,true);assert.equal(result.evidence,true);assert.equal(result.reports,true);
 await assert.rejects(resolveRestoreReleaseCapabilities(release,{readAt:async()=>{throw Error('missing');}}),/unavailable/i);
 await assert.rejects(resolveRestoreReleaseCapabilities(release,{readAt:async(_revision,file)=>file.endsWith('compose.runtime.yml')?JSON.stringify(compose):'unrecognised',contracts}),/secret contract/i);
});

test('manifest, release and known runtime revision must agree',()=>{
 const revision='a'.repeat(40),release={schemaVersion:1,configVersion:1,imageId:'sha256:'+'b'.repeat(64),sourceRevision:revision};
 assert.equal(verifyRestoreRevisionIdentity({sourceRevision:revision},release,{releaseId:revision}),revision);
 assert.equal(verifyRestoreRevisionIdentity({sourceRevision:revision},release,{releaseId:null}),revision);
 assert.throws(()=>verifyRestoreRevisionIdentity({sourceRevision:'c'.repeat(40)},release,{releaseId:revision}),/revision/i);
 assert.throws(()=>verifyRestoreRevisionIdentity({sourceRevision:revision},release,{releaseId:'d'.repeat(40)}),/revision/i);
});

test('current deployment files have an explicitly registered secret contract',async()=>{
 const compose=await fs.readFile('deploy/compose.runtime.yml','utf8'),script=await fs.readFile('deploy/runtime/init-volumes.sh','utf8');
 const capabilities=await resolveRestoreReleaseCapabilities({sourceRevision:'a'.repeat(40)},
  {readAt:async(_revision,file)=>file.endsWith('compose.runtime.yml')?compose:script});
 assert.ok(capabilities.coreSecrets.includes('node-a'));assert.ok(capabilities.coreSecrets.includes('cursor-signing'));
});
