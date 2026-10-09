import test from 'node:test';
import assert from 'node:assert/strict';
import {verifyMigrationCompletion} from './migration.mjs';
import {parseArguments} from './sources.mjs';
const identity=source=>({source,harness:'c'.repeat(40),productTree:'d'.repeat(40),harnessTree:'e'.repeat(40),harnessFingerprint:'f'.repeat(64)});
const state=()=>({appliedMigrations:['initial'],pendingMigrations:[],pendingModelChanges:false,modelMatched:true,businessFingerprint:'a'.repeat(64),tables:[{name:'organizations',columns:['id','name'],rows:1,bytes:8192,dataHash:'b'.repeat(64)}],indexes:[{table:'organizations',name:'PK_organizations',columns:['id'],descending:[false],unique:true,valid:true,ready:true,bytes:8192,definitionHash:'c'.repeat(64)}]});
const valid=()=>({fixtureRestore:{complete:true,receipt:{sha256:"3".repeat(64),bytes:1024},before:state(),after:state(),mutationObserved:true},immutable:true,identities:{baseline:identity('a'.repeat(40)),candidate:identity('b'.repeat(40))},images:{baseline:{imageId:'sha256:'+'1'.repeat(64)},candidate:{imageId:'sha256:'+'2'.repeat(64)}},baselineState:state(),empty:{before:{...state(),tables:[]},after:state(),indexOnly:false,complete:true},upgrade:{before:state(),after:state(),indexOnly:true,complete:true},failure:{noMigrationNeeded:true,failureInjected:false,sqlState:null,dataUnchanged:true,indexesUnchanged:true,complete:true},cleanup:{remaining:0,privateInputsDeleted:true},secretScan:{violations:0}});
test('migration_diagnostic_is_explicit_and_cannot_be_enabled_in_baseline',()=>{
  assert.equal(parseArguments(['validate','--diagnostic','migration','--output','/tmp/migration-unit']).diagnostic,'migration');
  assert.throws(()=>parseArguments(['baseline','--baseline','a'.repeat(40),'--harness','c'.repeat(40),'--diagnostic','migration','--output','/tmp/migration-unit']),/diagnostic/i);
});
test('migration_gate_accepts_actual_empty_upgrade_and_no_index_receipts_only_after_cleanup',()=>{
  const report=verifyMigrationCompletion(valid());assert.equal(report.complete,true);assert.equal(report.migrationRequired,false);
});
test('migration_gate_rejects_schema_data_identity_or_failure_receipt_mismatch',()=>{
  const mutations=[
    r=>{r.identities.candidate.harness='0'.repeat(40);},r=>{r.identities.candidate.harnessFingerprint='0'.repeat(64);},r=>{r.empty.after.modelMatched=false;},r=>{r.upgrade.after.businessFingerprint='0'.repeat(64);},
    r=>{r.upgrade.after.tables[0].dataHash='0'.repeat(64);},r=>{r.upgrade.after.tables[0].rows=2;},r=>{r.upgrade.after.indexes[0].valid=false;},r=>{r.upgrade.after.pendingMigrations=['pending'];},
    r=>{r.upgrade.after.appliedMigrations=['foreign'];},r=>{r.failure.dataUnchanged=false;},r=>{r.failure.noMigrationNeeded=false;},r=>{r.cleanup.remaining=1;},r=>{r.cleanup.privateInputsDeleted=false;},r=>{r.secretScan.violations=1;},r=>{r.empty.complete=false;},r=>{r.upgrade.indexOnly=false;}
  ];
  for(const mutate of mutations){const run=valid();mutate(run);assert.equal(verifyMigrationCompletion(run).complete,false);}
  const changed=valid();changed.upgrade.before.pendingMigrations=['newIndex'];changed.upgrade.after.appliedMigrations.push('newIndex');changed.empty.after.appliedMigrations.push('newIndex');
  const added={...state().indexes[0],name:'IX_organizations_name',columns:['name'],unique:false,definitionHash:'d'.repeat(64)};changed.upgrade.after.indexes.push(added);changed.empty.after.indexes.push(added);
  changed.failure={noMigrationNeeded:false,failureInjected:true,sqlState:'42601',dataUnchanged:true,indexesUnchanged:true,complete:true};assert.equal(verifyMigrationCompletion(changed).complete,true);assert.equal(verifyMigrationCompletion(changed).migrationRequired,true);
  changed.failure.failureInjected=false;assert.equal(verifyMigrationCompletion(changed).complete,false);
});

test('migration_gate_rejects_unproven_or_changed_fixture_restore',()=>{for(const mutate of [r=>delete r.fixtureRestore,r=>r.fixtureRestore.after.tables[0].dataHash='0'.repeat(64),r=>r.fixtureRestore.mutationObserved=false,r=>r.fixtureRestore.receipt.sha256='invalid']){const run=valid();mutate(run);assert.equal(verifyMigrationCompletion(run).diagnosticComplete,false);}});
