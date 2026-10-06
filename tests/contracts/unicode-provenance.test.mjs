import {test} from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
const root=path.resolve(import.meta.dirname,'../..');
test('fixed Unicode IDNA source and derived tables retain matching provenance',async()=>{
 const directory=path.join(root,'third_party/unicode-idna');
 const manifest=JSON.parse(await fs.readFile(path.join(directory,'source-manifest.json'),'utf8'));
 assert.equal(manifest.version,'17.0.0');
 for(const [name,hash] of Object.entries(manifest.files)){
  assert(!path.isAbsolute(name)&&!name.split('/').includes('..'));
  assert.equal(crypto.createHash('sha256').update(await fs.readFile(path.join(directory,name))).digest('hex'),hash,name);
 }
 assert.equal(crypto.createHash('sha256').update(await fs.readFile(path.join(root,manifest.derived.path))).digest('hex'),manifest.derived.sha256);
});
