import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID} from 'node:crypto';
import {prepareMigrationEvidenceVolume,markMigrationEvidenceVolumeReady,migrationEvidenceMayBeCreated} from '../../scripts/runtime/migration-evidence-volume.mjs';
import {inspectResources,ownedDocker} from '../../scripts/runtime/docker.mjs';

const state={projectName:'webapi-owned-test',ownerId:'b1234567-1234-4123-8123-123456789abc'};
const volumeName=state.projectName+'_migration-evidence';
const nonce='a'.repeat(64);

function fixture({existing=false, mismatch=false, collision=false}={}) {
  const calls=[];
  const labels={'com.docker.compose.project':state.projectName,'com.docker.compose.volume':'migration-evidence',
    'com.webapi.runtime.owner': mismatch ? 'other-owner' : state.ownerId};
  let volumes=existing?[{Kind:'volume',Name:volumeName,Driver:'local',Scope:'local',Options:null,Labels:labels}]:[];
  const inspect=async()=>volumes;
  const run=async args=>{
    calls.push(args);
    if(args[0]==='volume'&&args[1]==='create') {
      const created={Kind:'volume',Name:volumeName,Driver:'local',Scope:'local',Options:null,
        Labels:{...labels,'com.webapi.migration.evidence-nonce':collision?'wrong':nonce}};
      volumes=[created];
      return {exitCode:0,stdout:volumeName+'\n',stderr:''};
    }
    if(args[0]==='volume'&&args[1]==='inspect') return {exitCode:0,stdout:JSON.stringify(volumes),stderr:''};
    throw new Error('Unexpected Docker call');
  };
  return {calls,inspect,run,nonce:()=>nonce,loseVolume:()=>{volumes=[];},replaceNonce:()=>{volumes[0].Labels['com.webapi.migration.evidence-nonce']='b'.repeat(64);}};
}

async function retainedDirectory(){
  const dir=path.resolve('.superpowers/sdd/2026-11-04-migration-budget-trace/volume-marker-fixtures',randomUUID());
  await fs.mkdir(dir,{recursive:true,mode:0o700});
  console.log('retained-volume-marker-fixture='+dir);
  return dir;
}

test('volume creation persists intent and a lost pending volume is never silently recreated',async()=>{
  const directory=await retainedDirectory(),f=fixture();
  assert.equal(await prepareMigrationEvidenceVolume(state,{...f,allowCreate:true,directory}),true);
  const marker=JSON.parse(await fs.readFile(path.join(directory,'migration-evidence-provision.json'),'utf8'));
  assert.equal(marker.phase,'Intent');assert.equal(marker.ownerId,state.ownerId);assert.equal(marker.nonce,nonce);
  f.loseVolume();
  await assert.rejects(prepareMigrationEvidenceVolume(state,{...f,allowCreate:true,directory}),/pending|intent|missing|review/i);
  assert.equal(f.calls.filter(a=>a[1]==='create').length,1);
});

test('an existing volume without a persisted creation identity is never adopted',async()=>{
  const directory=await retainedDirectory(),f=fixture({existing:true});
  await assert.rejects(prepareMigrationEvidenceVolume(state,{...f,allowCreate:true,directory}),/identity|record|evidence|untracked/i);
  assert.deepEqual(f.calls,[]);
});

test('new named volume is verified with a fresh creation identity',async()=>{
  const directory=await retainedDirectory(),f=fixture();
  assert.equal(await prepareMigrationEvidenceVolume(state,{...f,allowCreate:true,directory}),true);
  assert.equal(f.calls.filter(a=>a[1]==='create').length,1);
  await markMigrationEvidenceVolumeReady(state,{...f,directory});
  assert.equal(await prepareMigrationEvidenceVolume(state,{...f,allowCreate:false,directory}),false);
});

test('an existing owned volume is reused without modification',async()=>{
  const directory=await retainedDirectory(),f=fixture();
  await prepareMigrationEvidenceVolume(state,{...f,allowCreate:true,directory});
  await markMigrationEvidenceVolumeReady(state,{...f,directory});
  f.calls.length=0;
  assert.equal(await prepareMigrationEvidenceVolume(state,{...f,allowCreate:false,directory}),false);
  assert.deepEqual(f.calls,[]);
});

test('ready evidence blocks a missing or replaced volume without recreating it',async()=>{
  for(const mode of ['missing','replacement']){
    const directory=await retainedDirectory(),f=fixture();
    await prepareMigrationEvidenceVolume(state,{...f,allowCreate:true,directory});
    await markMigrationEvidenceVolumeReady(state,{...f,directory});
    if(mode==='missing')f.loseVolume();else f.replaceNonce();
    await assert.rejects(prepareMigrationEvidenceVolume(state,{...f,allowCreate:true,directory}),/missing|identity|nonce/i);
    assert.equal(f.calls.filter(a=>a[1]==='create').length,1);
  }
});

test('missing existing volume and unowned volumes fail closed',async()=>{
  const missing=fixture(),directory=await retainedDirectory();
  await assert.rejects(prepareMigrationEvidenceVolume(state,{...missing,allowCreate:false,directory}));
  assert.deepEqual(missing.calls,[]);
  const foreign=fixture({existing:true,mismatch:true});
  await assert.rejects(prepareMigrationEvidenceVolume(state,{...foreign,allowCreate:true,directory}));
  assert.deepEqual(foreign.calls,[]);
});

test('a conflicting creation identity is never adopted or removed',async()=>{
  const directory=await retainedDirectory(),f=fixture({collision:true});
  await assert.rejects(prepareMigrationEvidenceVolume(state,{...f,allowCreate:true,directory}));
  assert.equal(f.calls.filter(a=>a[1]==='create').length,1);
  assert.equal(f.calls.some(a=>a[1]==='rm'),false);
});

test('creation is allowed only for a new installation or verified older release',async()=>{
  assert.equal(await migrationEvidenceMayBeCreated({...state,initialized:false,releaseId:null}),true);
  const initialized={...state,initialized:true,releaseId:'a'.repeat(40)};
  assert.equal(await migrationEvidenceMayBeCreated(initialized,{readPrior:async()=>JSON.stringify({volumes:{pg:{}}})}),true);
  assert.equal(await migrationEvidenceMayBeCreated(initialized,{readPrior:async()=>JSON.stringify({volumes:{'migration-evidence':{}}})}),false);
  await assert.rejects(migrationEvidenceMayBeCreated(initialized,{readPrior:async()=>{throw new Error('missing previous release')}}));
  await assert.rejects(migrationEvidenceMayBeCreated({...initialized,releaseId:'not-a-revision'},{readPrior:async()=>'{"volumes":{}}'}));
});

test('an unlabelled exact evidence volume prevents any compose mutation',async()=>{
  let mutations=0;
  const run=async args=>{
    if(args[0]==='compose'){mutations++;return {stdout:'',exitCode:0};}
    if(args[0]==='volume'&&args[1]==='inspect'&&args.includes(volumeName))
      return {stdout:JSON.stringify([{Name:volumeName,Labels:{}}]),stderr:'',exitCode:0};
    return {stdout:args.includes('inspect')?'[]':'',stderr:'',exitCode:0};
  };
  await assert.rejects(ownedDocker(state,['compose','up'],{inspect:s=>inspectResources(s,run),run}));
  assert.equal(mutations,0);
});
