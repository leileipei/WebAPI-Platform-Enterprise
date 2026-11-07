import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash,randomUUID} from 'node:crypto';
import * as freeze from '../../scripts/runtime/migration-evidence-freeze.mjs';

async function fixture(){
 const directory=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/frozen-input-fixtures',randomUUID());
 await fs.mkdir(directory,{recursive:true,mode:0o700});console.log('retained-frozen-input-fixture='+directory);
 return directory;
}

test('freezing creates a private distinct copy of the exact verified bytes',async()=>{
 assert.equal(typeof freeze.freezeMigrationEvidenceArchive,'function');
 const directory=await fixture(),source=path.join(directory,'source.tar'),data=Buffer.from('isolated-evidence-bytes');
 await fs.writeFile(source,data,{mode:0o600});const digest=createHash('sha256').update(data).digest('hex');
 const one=await freeze.freezeMigrationEvidenceArchive(source,directory,digest);
 const two=await freeze.freezeMigrationEvidenceArchive(source,directory,digest);
 assert.notEqual(one.path,two.path);assert.equal(one.sha256,digest);assert.deepEqual(await fs.readFile(one.path),data);
 assert.equal((await fs.stat(one.path)).mode&0o777,0o400);
 await fs.writeFile(source,'changed');assert.deepEqual(await fs.readFile(one.path),data);
});

test('wrong hashes and symbolic links never become usable frozen inputs',async()=>{
 assert.equal(typeof freeze.freezeMigrationEvidenceArchive,'function');
 const directory=await fixture(),source=path.join(directory,'source.tar');await fs.writeFile(source,'fixture',{mode:0o600});
 await assert.rejects(freeze.freezeMigrationEvidenceArchive(source,directory,'a'.repeat(64)),/hash|checksum/i);
 const link=path.join(directory,'link.tar');await fs.symlink(source,link);
 await assert.rejects(freeze.freezeMigrationEvidenceArchive(link,directory,'a'.repeat(64)),/unsafe|link/i);
 assert.ok((await fs.readdir(directory)).some(name=>name.startsWith('migration-evidence-input-')),'failed copy remains for inspection');
});

test('frozen input identity is rechecked and altered bytes cannot be used',async()=>{
 assert.equal(typeof freeze.verifyFrozenMigrationEvidenceArchive,'function');
 const directory=await fixture(),source=path.join(directory,'source.tar'),data=Buffer.alloc(1024,7);
 await fs.writeFile(source,data,{mode:0o600});const digest=createHash('sha256').update(data).digest('hex');
 const frozen=await freeze.freezeMigrationEvidenceArchive(source,directory,digest);
 await freeze.verifyFrozenMigrationEvidenceArchive(frozen);
 await fs.chmod(frozen.path,0o600);await fs.writeFile(frozen.path,Buffer.alloc(1024,8));await fs.chmod(frozen.path,0o400);
 await assert.rejects(freeze.verifyFrozenMigrationEvidenceArchive(frozen),/identity|changed|hash/i);
 assert.ok((await fs.stat(frozen.path)).isFile());
});
