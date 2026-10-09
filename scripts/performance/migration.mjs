import {verifyFixtureRestore} from './database-fixture.mjs';
const sha=value=>/^[a-f0-9]{64}$/.test(value??'');
const commit=value=>/^[a-f0-9]{40}$/.test(value??'');
const same=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
const sorted=values=>[...values].sort();
const data=state=>state.tables.map(t=>({name:t.name,columns:t.columns,rows:t.rows,dataHash:t.dataHash})).sort((a,b)=>a.name.localeCompare(b.name));
const catalog=state=>state.indexes.map(i=>({...i,bytes:0})).sort((a,b)=>(a.table+':'+a.name).localeCompare(b.table+':'+b.name));
function validState(state){
  return state&&Array.isArray(state.appliedMigrations)&&Array.isArray(state.pendingMigrations)&&typeof state.pendingModelChanges==='boolean'&&typeof state.modelMatched==='boolean'&&sha(state.businessFingerprint)&&Array.isArray(state.tables)&&Array.isArray(state.indexes)&&
    new Set(state.tables.map(t=>t.name)).size===state.tables.length&&state.tables.every(t=>typeof t.name==='string'&&Array.isArray(t.columns)&&Number.isSafeInteger(t.rows)&&t.rows>=0&&Number.isSafeInteger(t.bytes)&&t.bytes>=0&&sha(t.dataHash))&&
    new Set(state.indexes.map(i=>i.table+':'+i.name)).size===state.indexes.length&&state.indexes.every(i=>typeof i.table==='string'&&typeof i.name==='string'&&Array.isArray(i.columns)&&Array.isArray(i.descending)&&i.columns.length===i.descending.length&&i.descending.every(v=>typeof v==='boolean')&&i.valid===true&&i.ready===true&&typeof i.unique==='boolean'&&Number.isSafeInteger(i.bytes)&&i.bytes>=0&&sha(i.definitionHash));
}
export function verifyMigrationCompletion(input){
  const problems=[],check=(condition,message)=>{if(!condition)problems.push(message);};
  const b=input?.identities?.baseline,c=input?.identities?.candidate;
  check(commit(b?.source)&&commit(c?.source)&&commit(b?.productTree)&&commit(c?.productTree)&&sha(b?.harnessFingerprint)&&b.harnessFingerprint===c?.harnessFingerprint&&b.harness===c?.harness,'source/harness identity');
  if(input?.immutable)check(commit(b?.harness)&&commit(c?.harness)&&commit(b?.harnessTree)&&b.harnessTree===c?.harnessTree,'immutable harness identity');
  else check(b?.harness==='working-tree-validation'&&c?.harness==='working-tree-validation','explicit mutable validation');
  check(['baseline','candidate'].every(v=>/^sha256:[a-f0-9]{64}$/.test(input?.images?.[v]?.imageId??'')),'image identity');
  const fixture=input?.fixtureRestore;
  check(fixture?.complete===true&&fixture.mutationObserved===true&&sha(fixture.receipt?.sha256)&&Number.isSafeInteger(fixture.receipt?.bytes)&&fixture.receipt.bytes>0&&verifyFixtureRestore(fixture.before,fixture.after),'same private fixture restoration');
  const empty=input?.empty,upgrade=input?.upgrade,base=input?.baselineState,fault=input?.failure;
  const states=[base,empty?.before,empty?.after,upgrade?.before,upgrade?.after];
  check(states.every(validState),'database inspection');
  let migrationRequired=false;
  if(states.every(validState)){
    check(empty.complete===true&&empty.indexOnly===false&&empty.before.tables.length===0&&empty.after.tables.length>0&&empty.after.modelMatched&&!empty.after.pendingModelChanges&&empty.after.pendingMigrations.length===0,'empty install');
    check(upgrade.complete===true&&upgrade.indexOnly===true&&upgrade.after.modelMatched&&!upgrade.after.pendingModelChanges&&upgrade.after.pendingMigrations.length===0,'index-only upgrade');
    check(base.modelMatched&&base.pendingMigrations.length===0&&!base.pendingModelChanges&&same(sorted(base.appliedMigrations),sorted(upgrade.before.appliedMigrations)),'baseline history');
    check(base.businessFingerprint===upgrade.before.businessFingerprint&&same(data(base),data(upgrade.before))&&same(catalog(base),catalog(upgrade.before)),'same seeded baseline');
    check(upgrade.before.businessFingerprint===upgrade.after.businessFingerprint&&same(data(upgrade.before),data(upgrade.after)),'preserved business data');
    check(same(sorted(upgrade.after.appliedMigrations),sorted([...upgrade.before.appliedMigrations,...upgrade.before.pendingMigrations]))&&same(sorted(empty.after.appliedMigrations),sorted(upgrade.after.appliedMigrations)),'candidate migration history');
    check(same(catalog(empty.after),catalog(upgrade.after)),'empty/upgrade index catalog');
    const previous=new Map(upgrade.before.indexes.map(i=>[i.table+':'+i.name,{...i,bytes:0}])),next=new Map(upgrade.after.indexes.map(i=>[i.table+':'+i.name,{...i,bytes:0}]));
    check([...previous].every(([key,value])=>same(value,next.get(key))),'existing indexes preserved');
    const additions=[...next.keys()].filter(k=>!previous.has(k));migrationRequired=upgrade.before.pendingMigrations.length>0;
    check(migrationRequired?additions.length>0:additions.length===0,'new index evidence');
  }
  check(fault?.complete===true&&fault.dataUnchanged===true&&fault.indexesUnchanged===true,'failure rollback');
  check(migrationRequired?fault?.noMigrationNeeded===false&&fault.failureInjected===true&&fault.sqlState==='42601':fault?.noMigrationNeeded===true&&fault.failureInjected===false&&fault.sqlState===null,'actual failure/no-migration receipt');
  check(input?.cleanup?.remaining===0&&input.cleanup.privateInputsDeleted===true,'owner cleanup');check(input?.secretScan?.violations===0,'public secret scan');
  return {complete:problems.length===0&&input.immutable===true,diagnosticComplete:problems.length===0,migrationRequired,problems};
}
