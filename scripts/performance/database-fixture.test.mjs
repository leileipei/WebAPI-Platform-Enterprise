import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {createOwner} from './owner.mjs';
import {verifyFixtureRestore,captureDatabaseFixture,restoreDatabaseFixture} from './database-fixture.mjs';
const state=()=>({businessFingerprint:'a'.repeat(64),appliedMigrations:['initial'],pendingMigrations:[],tables:[{name:'organizations',columns:['id','name'],rows:2,bytes:8192,dataHash:'b'.repeat(64)}],indexes:[{table:'organizations',name:'PK_organizations',columns:['id'],descending:[false],unique:true,valid:true,ready:true,bytes:8192,definitionHash:'c'.repeat(64)}]});
test('fixture_parity_requires_actual_data_ids_history_and_index_layout_but_not_physical_file_size',()=>{
  const before=state(),after=state();after.tables[0].bytes*=2;after.indexes[0].bytes*=2;assert.equal(verifyFixtureRestore(before,after),true);
  for(const mutate of [s=>s.businessFingerprint='d'.repeat(64),s=>s.tables[0].dataHash='d'.repeat(64),s=>s.tables[0].rows++,s=>s.appliedMigrations.push('other'),s=>s.indexes[0].definitionHash='d'.repeat(64),s=>s.indexes[0].valid=false,s=>s.tables.push(s.tables[0])]){const changed=state();mutate(changed);assert.equal(verifyFixtureRestore(before,changed),false);}
  assert.equal(verifyFixtureRestore({},{}),false);
});
test('foreign_postgres_never_creates_dump_or_resets_database',async()=>{
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'webapi-fixture-test-'));try{
    const owner=await createOwner(root),calls=[];
    const docker=async args=>{calls.push(args);return {stdout:JSON.stringify([{Config:{Labels:{'com.docker.compose.project':'foreign','com.webapi.performance.owner':'foreign'},Image:'postgres'},State:{Running:true}}])};};
    await assert.rejects(()=>captureDatabaseFixture({owner,postgresId:'a'.repeat(64),key:'ordinary-100',state:state(),docker}),/ownership/i);
    assert.equal(calls.length,1);assert.equal(calls[0][0],'inspect');assert.equal((await fs.readdir(owner.directory)).includes('database-fixtures'),false);
  }finally{await fs.rm(root,{recursive:true,force:true});}
});
test('tampered_private_fixture_is_rejected_before_any_database_mutation',async()=>{
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'webapi-fixture-test-'));try{
    const owner=await createOwner(root);await fs.mkdir(path.join(owner.directory,'database-fixtures'),{mode:0o700});await fs.writeFile(path.join(owner.directory,'database-fixtures','ordinary-100.dump'),'tampered',{mode:0o600});
    let calls=0;const docker=async()=>{calls++;throw Error('Must not reach Docker');};
    await assert.rejects(()=>restoreDatabaseFixture({owner,postgresId:'a'.repeat(64),fixture:{owner:owner.owner,key:'ordinary-100',bytes:8,sha256:'f'.repeat(64)},docker}),/hash/i);assert.equal(calls,0);
    await assert.rejects(()=>restoreDatabaseFixture({owner,postgresId:'a'.repeat(64),fixture:{owner:owner.owner,key:'../other',bytes:8,sha256:'f'.repeat(64)},docker}),/invalid/i);assert.equal(calls,0);
  }finally{await fs.rm(root,{recursive:true,force:true});}
});
