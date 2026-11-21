import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash} from 'node:crypto';
export function assertFixtureBinding(url,directory,fixture,credentials,context) {
  const base=new URL(url),expected=new URL(fixture.url);
  assert.equal(base.protocol,'http:');assert.equal(base.hostname,'127.0.0.1');
  assert.ok(base.port&&!['4192','4194','4196','4197'].includes(base.port));
  assert.equal(base.href,expected.href,'Fixture URL mismatch.');assert.equal(base.pathname,'/');
  assert.equal(base.search,'');assert.equal(base.hash,'');assert.equal(base.username,'');assert.equal(base.password,'');
  assert.equal(path.resolve(directory),fixture.directory);assert.equal(path.basename(directory),'product-counts-'+fixture.owner);
  assert.match(fixture.owner,/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
  assert.equal(credentials.owner,fixture.owner);assert.equal(context.owner,fixture.owner);
  assert.equal(context.productRevision,fixture.productRevision);assert.match(fixture.productRevision,/^[a-f0-9]{40}$/);
}
export function assertServedIdentity(fixture,identity) {
  for(const field of ['owner','productRevision','sourceManifestSha256','staticFiles'])
    assert.deepEqual(identity[field],fixture[field],'Served fixture identity mismatch.');
  assert.ok(Object.keys(fixture.staticFiles).includes('index.html'));
}
export function publicFailure(_error,phase,scenario) {
  return {name:'BrowserAcceptanceError',phase,scenario,reason:'Acceptance stage did not complete; private exception details were not recorded.'};
}
export async function claimEvidenceDirectory(directory) {
  await fs.mkdir(path.dirname(directory),{recursive:true,mode:0o700});
  await fs.mkdir(directory,{mode:0o700}); // Exclusive directory creation is the claim.
  await fs.writeFile(path.join(directory,'started.json'),JSON.stringify({startedAt:new Date().toISOString()})+'\n',{flag:'wx',mode:0o600});
}
export async function sourceHashes(directory) {
  const files={};
  async function walk(relative='') {
    for(const entry of await fs.readdir(path.join(directory,relative),{withFileTypes:true})) {
      if(entry.isDirectory()&&['bin','obj','node_modules','dist'].includes(entry.name)||entry.name==='node_modules')continue;
      const name=path.join(relative,entry.name);
      if(entry.isDirectory())await walk(name);
      else {assert.ok(entry.isFile(),'Unexpected non-file source input.');files[name]=createHash('sha256').update(await fs.readFile(path.join(directory,name))).digest('hex');}
    }
  }
  await walk();return files;
}
export async function verifySourceManifest(directory,expected) {
  assert.deepEqual(await sourceHashes(directory),expected,'Frozen source changed or contains extra inputs.');
}
