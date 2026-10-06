import {test} from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
const root=path.resolve(import.meta.dirname,'../..');
test('self-compiled dependencies have immutable source provenance and matching bytes',async()=>{
 const dir=path.join(root,'third_party/json-everything');
 const manifest=JSON.parse(await fs.readFile(path.join(dir,'source-manifest.json'),'utf8'));
 assert.equal(manifest.projects.JsonSchema.version,'9.4.0');
 assert.equal(manifest.projects.JsonPointer.version,'7.0.2');
 assert.equal(manifest.projects['Json.More'].version,'3.0.1');
 for(const [name,entry] of Object.entries(manifest.files)){
  assert(!path.isAbsolute(name)&&!name.split('/').includes('..'));
  const data=await fs.readFile(path.join(dir,name));
  assert.equal(crypto.createHash('sha256').update(data).digest('hex'),entry.sha256,name);
  assert.match(entry.commit,/^[0-9a-f]{40}$/);
 }
 assert(Object.keys(manifest.files).length>100);
 assert.match(await fs.readFile(path.join(dir,'LICENSE'),'utf8'),/^MIT License/);
});
