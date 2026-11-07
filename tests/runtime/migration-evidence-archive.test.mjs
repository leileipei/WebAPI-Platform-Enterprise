import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {validateMigrationEvidenceArchive,hashMigrationEvidenceArchive} from '../../scripts/runtime/migration-evidence-archive.mjs';

async function fixture(t){
  const parent=await fs.mkdtemp(path.join(os.tmpdir(),'webapi-evidence-archive-'));
  if(process.env.WEBAPI_TEST_RETAIN_FIXTURES==='1')console.log('isolated-archive-fixture-retained='+parent);
  else t.after(()=>fs.rm(parent,{recursive:true,force:true}));
  const dir=path.join(parent,'evidence');await fs.mkdir(dir,{mode:0o700});
  for(const name of ['.journal.lock','.journal.config','a'.repeat(32)+'.start.json']){
    const file=path.join(dir,name);await fs.writeFile(file,name==='.journal.lock'?'':'{}',{mode:0o600});await fs.chown(file,10001,10001);
  }
  await fs.chown(dir,10001,10001);return {parent,dir,archive:path.join(parent,'evidence.tar')};
}
async function archive(f){
  const result=spawnSync('tar',['--numeric-owner','-cf',f.archive,'-C',f.dir,'.'],{encoding:'utf8'});
  assert.equal(result.status,0,result.stderr);await fs.chmod(f.archive,0o600);
}
test('accepts a bounded regular-file GNU tar of an evidence directory',async t=>{
  const f=await fixture(t);await archive(f);const result=await validateMigrationEvidenceArchive(f.archive);assert.equal(result.attemptStarts,1);assert.equal(result.files,3);
  assert.equal(await hashMigrationEvidenceArchive(f.archive),createHash('sha256').update(await fs.readFile(f.archive)).digest('hex'));
});
test('rejects linked or nested entries without extracting the archive',async t=>{
  const f=await fixture(t);await fs.symlink('../outside',path.join(f.dir,'unsafe'));await archive(f);
  await assert.rejects(validateMigrationEvidenceArchive(f.archive));
});
test('rejects oversized records and truncated archives',async t=>{
  const f=await fixture(t);const file=path.join(f.dir,'a'.repeat(32)+'.start.json');await fs.writeFile(file,Buffer.alloc(65537));await archive(f);
  await assert.rejects(validateMigrationEvidenceArchive(f.archive));
  await fs.writeFile(file,'{}');await archive(f);await fs.truncate(f.archive,512);
  await assert.rejects(validateMigrationEvidenceArchive(f.archive));
});
test('rejects altered tar headers and non-private files',async t=>{
  const f=await fixture(t);await archive(f);const handle=await fs.open(f.archive,'r+');await handle.write(Buffer.from([88]),0,1,20);await handle.close();
  await assert.rejects(validateMigrationEvidenceArchive(f.archive));
  await archive(f);await fs.chmod(f.archive,0o644);await assert.rejects(validateMigrationEvidenceArchive(f.archive));
});
