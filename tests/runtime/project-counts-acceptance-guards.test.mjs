import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {assertFixtureBinding,assertServedIdentity,claimEvidenceDirectory,publicFailure,verifySourceManifest} from '../../scripts/acceptance/project-counts-guards.mjs';
const owner='10000000-0000-4000-8000-000000000001';
const fixture={owner,directory:'/tmp/product-counts-'+owner,url:'http://127.0.0.1:31942',productRevision:'a'.repeat(40),staticFiles:{'index.html':'b'.repeat(64)},sourceManifestSha256:'c'.repeat(64)};
test('login rejects a different loopback service or mismatched fixture owner before credentials are used',()=>{
 assert.doesNotThrow(()=>assertFixtureBinding(fixture.url,fixture.directory,fixture,{owner}, {owner,productRevision:fixture.productRevision}));
 for(const url of ['http://127.0.0.1:31943','http://localhost:31942','https://127.0.0.1:31942','http://127.0.0.1:31942/other'])assert.throws(()=>assertFixtureBinding(url,fixture.directory,fixture,{owner},{owner,productRevision:fixture.productRevision}));
 assert.throws(()=>assertFixtureBinding(fixture.url,fixture.directory,fixture,{owner:'other'},{owner,productRevision:fixture.productRevision}));
 assert.throws(()=>assertFixtureBinding(fixture.url,fixture.directory,fixture,{owner},{owner,productRevision:'d'.repeat(40)}));
});
test('served identity binds the owner product SHA static assets and frozen source manifest',()=>{
 const identity={owner,productRevision:fixture.productRevision,staticFiles:fixture.staticFiles,sourceManifestSha256:fixture.sourceManifestSha256};
 assert.doesNotThrow(()=>assertServedIdentity(fixture,identity));
 for(const field of ['owner','productRevision','staticFiles','sourceManifestSha256'])assert.throws(()=>assertServedIdentity(fixture,{...identity,[field]:'mismatch'}));
});
test('public failure never includes Playwright fill parameters exception names or private input',()=>{
 const secret='Synthetic-Aa1-secret-123';
 const error=new Error('locator.fill timed out: fill("'+secret+'")');error.name=secret;
 const result=JSON.stringify(publicFailure(error,'login','all'));
 assert.equal(result.includes(secret),false);assert.deepEqual(JSON.parse(result),{name:'BrowserAcceptanceError',phase:'login',scenario:'all',reason:'Acceptance stage did not complete; private exception details were not recorded.'});
});
test('evidence output is claimed atomically and cannot overwrite an interrupted run',async t=>{
 const root=await fs.mkdtemp(path.join(os.tmpdir(),'counts-guard-'));t.after(()=>fs.rm(root,{recursive:true,force:true}));
 const output=path.join(root,'run');const claims=await Promise.allSettled([claimEvidenceDirectory(output),claimEvidenceDirectory(output)]);
 assert.equal(claims.filter(r=>r.status==='fulfilled').length,1);assert.equal(claims.filter(r=>r.status==='rejected').length,1);
 await fs.writeFile(path.join(output,'PC-all.png'),'retained');await assert.rejects(claimEvidenceDirectory(output));
 assert.equal(await fs.readFile(path.join(output,'PC-all.png'),'utf8'),'retained');assert.ok(JSON.parse(await fs.readFile(path.join(output,'started.json'))).startedAt);
});
test('frozen input verification rejects modified files and injected untracked compilation inputs',async t=>{
 const root=await fs.mkdtemp(path.join(os.tmpdir(),'counts-source-'));t.after(()=>fs.rm(root,{recursive:true,force:true}));
 await fs.mkdir(path.join(root,'src'));await fs.writeFile(path.join(root,'src','test.cs'),'original');
 const manifest={'src/test.cs':'0682c5f2076f099c34cfdd15a9e063849ed437a49677e6fcc5b4198c76575be5'};
 await verifySourceManifest(root,manifest);
 await fs.writeFile(path.join(root,'src','extra.cs'),'injected');await assert.rejects(verifySourceManifest(root,manifest));await fs.unlink(path.join(root,'src','extra.cs'));
 await fs.writeFile(path.join(root,'src','test.cs'),'modified');await assert.rejects(verifySourceManifest(root,manifest));
});
