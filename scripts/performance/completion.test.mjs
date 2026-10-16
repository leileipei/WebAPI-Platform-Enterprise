import test from 'node:test';
import assert from 'node:assert/strict';
import {buildMatrix} from './matrix.mjs';
import {verifyBaselineCompletion} from './completion.mjs';
import {postgresImage} from './template-fixture.mjs';
import {quotas} from './environment.mjs';
import {summarizeContainerWindow} from './resources.mjs';
const hash='a'.repeat(64),source='b'.repeat(40),harness='c'.repeat(40);
const lists=['api','cluster','route','application','policy','user','node','node-event','snapshot','release','alert-rule','alert-event','policy-reference'];
const actors=[['platform-role',200,200],['organization-role',200,200],['project-read-only',200,200],['environment-read-only',200,200],['no-function',200,404],['no-scope',200,404],['inactive-user',401,401],['inactive-ancestor',200,404],['cross-organization-role',200,404]];
function samplesFor(dimensions){return dimensions.flatMap(dim=>[1,50,100].flatMap(pageSize=>['first','middle','last','out-of-range'].flatMap(pageKind=>[1,2,3].map(repeat=>({...dim,pageSize,pageKind,repeat})))));}
function fixture(){
  const scales=[100,1000,10000],state={heapBytes:1,workingSetBytes:1,totalAllocatedBytes:1,pauseTicks:1,cpuTicks:1,collections:[0,0,0]},servers=Object.fromEntries(['gateway-a','gateway-b','controlplane','backend'].map(name=>[name,{...state}]));
  const window={start:'2026-10-09T00:00:00Z',end:'2026-10-09T00:00:20Z'};
  const resourceServices=Object.fromEntries(Object.entries(quotas).map(([name,q])=>{
    const samples=Array.from({length:21},(_,second)=>({time:new Date(Date.UTC(2026,9,9)+second*1000).toISOString(),memoryUsageBytes:1000,memoryLimitBytes:q.memoryBytes,workingSetBytes:800,residentAnonymousBytes:500,residentMetric:'cgroup-v2-anon',cpuUsageNs:second*1000000,throttledNs:0,periods:second,throttledPeriods:0,pids:3}));
    return [name,{containerId:'a'.repeat(64),imageId:'sha256:'+hash,...q,samples,summary:summarizeContainerWindow(samples,{...window,...q})}];
  }));
  const resources={schemaVersion:1,source:'docker-engine-stats-stream',apiVersion:'1.54',complete:true,errors:[],windowStartedAt:window.start,windowEndedAt:window.end,services:resourceServices,potentialClientOrBackendSaturation:[],saturationThreshold:.9};
  const decorate=(scale,samples)=>({scale,windowStartedAt:window.start,windowEndedAt:window.end,containerResources:resources,complete:true,classificationComplete:true,classificationManifest:{hash,sourceCommit:source,unclassifiedHashes:[]},samples:samples.map(s=>({...s,scale,durationMs:1,responseBytes:1,responseHash:hash,sql:{total:1,authorization:1,data:0,unclassified:0,rows:1,binaryColumnBytes:0},rawSql:{total:1,unclassified:1,rows:1,binaryColumnBytes:0},shapes:[{hash,count:1,kind:'reader',classification:'authorization',sourceEvidence:[{commit:source,path:'src/A.cs',line:1}]}]}))});
  return {mode:'baseline',immutable:true,identity:{source,harness,productTree:source,harnessTree:harness,harnessFingerprint:hash,harnessFiles:{'tools/WebApi.PerformanceHost/A.cs':hash}},environment:{verified:true,fingerprint:hash,services:Object.fromEntries(['gateway-a','gateway-b','controlplane','backend','postgres','redis','client'].map(n=>[n,{}])),runtimeState:{'gateway-a':{serverGc:true,processorCount:2},'gateway-b':{serverGc:true,processorCount:2},controlplane:{serverGc:true,processorCount:2}},databaseActual:{maxConnections:'100',sharedBuffers:'512MB',workMem:'16MB',maintenanceWorkMem:'256MB',parallelWorkersPerGather:'0',timezone:'UTC'}},classificationManifestHash:hash,
    cells:buildMatrix({variants:['baseline']}).map(c=>({...c,containerResources:resources,complete:true,expected:c.nodeMode==='dual'?2:1,unexpected:0,timeout:0,incomplete:0,durationMs:20000,warmupMs:10000,windowStartedAt:'2026-10-09T00:00:00Z',windowEndedAt:'2026-10-09T00:00:20Z',nodes:Array.from({length:c.nodeMode==='dual'?2:1},()=>({expected:1,unexpected:0,timeout:0,incomplete:0})),p50:1,p95:1,p99:1,histogram:[[1,c.nodeMode==='dual'?2:1]],samplingComplete:true,servers:{before:servers,after:servers},measuredSamples:[{time:'2026-10-09T00:00:01Z',servers}],client:{peakRssBytes:1,cpuUserUs:1,cpuSystemUs:1,eventLoopP95Ms:1,maxSchedulingDelayMs:1}})),
    gatewayDatasets:scales.map(scale=>({spec:{scale},fingerprint:hash,snapshotBytes:Object.fromEntries(['anonymous','apikey','rs256','es256','no-grant'].map(n=>[n,1])),snapshotHashes:Object.fromEntries(['anonymous','apikey','rs256','es256','no-grant'].map(n=>[n,hash]))})),
    management:scales.map(s=>decorate(s,samplesFor(lists.map(list=>({list}))))),
    scopeManagement:scales.map(s=>decorate(s,actors.flatMap(([actor,a,p])=>[['application',a],['policy',p]].flatMap(([list,status])=>status===200?samplesFor([{actor,list,status}]):[1,2,3].map(repeat=>({actor,list,status,pageSize:1,pageKind:'denied',repeat})))))) ,
    referenceManagement:scales.map(s=>decorate(s,samplesFor(['platform-role','environment-read-only'].map(actor=>({actor}))))),
    micro:scales.map(scale=>({scale,entries:['route','cluster','application','credential','authentication','jwt_mapping','authorization'].flatMap(operation=>['first','middle','last'].map(position=>({mode:'scan',operation,position,iterations:10000,hits:10000,elapsedTicks:1,allocatedBytes:0})))})),
    generation:scales.map(scale=>({scale,emptyHeapBytes:1,singleHeapBytes:2,heldDualHeapBytes:3,releasedHeapBytes:2,newBuild:{allocatedBytes:1,elapsed:'00:00:00.001',heapBytes:1},oldLeaseUsable:true,retiredRemoved:true,retiredCollected:true})),cleanup:{owner:'test-owner',remaining:0,privateInputsDeleted:true},secretScan:{filesScanned:20,violations:0,findings:[]}};
}
test('full_baseline_gate_rejects_smoke_mutable_missing_and_tainted_evidence',()=>{
  assert.equal(verifyBaselineCompletion(fixture()).complete,true);
  const mutations=[
    x=>{x.mode='validate';x.immutable=false;},x=>{x.identity.harness='working-tree-validation';},x=>{x.cells.pop();},
    x=>{x.cells.push({...x.cells[0]});},x=>{x.cells[0].expected=0;},x=>{x.cells[0].durationMs=200;},x=>{x.cells[0].warmupMs=50;},
    x=>{x.cells[0].samplingComplete=false;},x=>{delete x.cells[0].containerResources;},x=>{x.cells[0].containerResources.services.client.samples=[];},x=>{x.cells[0].containerResources.services.client.summary.meanCpuCores=99;},x=>{x.cells[0].containerResources.services.client.memoryBytes=99;},x=>{x.cells[0].measuredSamples=[];},x=>{x.cells[0].nodes[1].expected=0;},x=>{x.cells[0].nodes[0].expected++;},x=>{x.cells[0].expected=Infinity;x.cells[0].histogram=[[1,Infinity]];},
    x=>{x.management.pop();},x=>{delete x.management[0].containerResources;},x=>{x.scopeManagement[0].containerResources.windowStartedAt='2026-10-09T00:00:01Z';},x=>{x.management[0].samples.pop();},x=>{x.scopeManagement[0].samples=x.scopeManagement[0].samples.filter(s=>s.actor!=='inactive-user');},
    x=>{x.referenceManagement[0].samples[0].repeat=2;},x=>{x.management[0].classificationManifest.hash='d'.repeat(64);},
    x=>{x.management[0].samples[0].sql.unclassified=1;},x=>{x.management[0].samples[0].sql.total=2;},x=>{x.management[0].samples[0].sql.rows=0;},x=>{x.management[0].samples[0].sql.binaryColumnBytes=99;},
    x=>{x.gatewayDatasets[2].snapshotBytes['no-grant']=16777217;},x=>{x.micro[1].entries.pop();},x=>{x.generation[2].retiredCollected=false;},
    x=>{x.environment.verified=false;},x=>{x.cleanup.remaining=1;},x=>{x.cleanup.privateInputsDeleted=false;},x=>{x.secretScan.violations=1;}
  ];
  for(const change of mutations){const x=fixture();change(x);const result=verifyBaselineCompletion(x);assert.equal(result.complete,false,change.toString());assert.ok(result.missing.length>0);}
  assert.equal(verifyBaselineCompletion({}).complete,false);
});

test('comparison_variant_gate_retains_complete_matrix_and_requires_candidate_indexed_micro',()=>{const candidate=fixture();candidate.mode='compare';candidate.variant='candidate';candidate.cells.forEach(c=>c.variant='candidate');candidate.micro.forEach(m=>m.entries.forEach(e=>e.mode='indexed'));assert.equal(verifyBaselineCompletion(candidate).complete,true);candidate.micro[0].entries[0].mode='scan';assert.equal(verifyBaselineCompletion(candidate).complete,false);candidate.variant='unknown';assert.equal(verifyBaselineCompletion(candidate).complete,false);});

test('combined gate requires target/template/role/backend plus all legacy evidence',()=>{
 const good=fixture();good.experiment='current-business-combined-v1';
 // A role label and otherwise-valid legacy fixture do not prove the new experiment.
 assert.equal(verifyBaselineCompletion(good).complete,false);
 for(const missing of ['target','template','role','backend','window']){const input=fixture();input.experiment='current-business-combined-v1';input.combinedProof={sourceDiffVerified:true,sourceDiffSha256:hash};if(missing==='window')input.cells.pop();if(missing==='backend')input.micro.forEach(m=>m.entries.forEach(e=>e.backend='runtime-frozen-dictionary-v1'));assert.equal(verifyBaselineCompletion(input).complete,false,missing);}
});

function combinedFixture(){
 const x=fixture();x.experiment='current-business-combined-v1';x.cleanup.owner='d09a7407-3d06-45be-932b-5037422a89d2';
 const candidateSource='760493da5a5bc0f0674e7e6398260c8a5ac4a6c0';
 const proof={role:'baseline',modelMatched:false,experimentRoleVerified:true,missingApprovedIndexes:['ix_api_routes_environment_order_path_id','ix_policies_organization_name_id','ix_alert_rules_organization_name_id','ix_alert_events_organization_project_started_id'],contractSha256:hash,metadataSha256:hash,indexesSha256:hash,complete:true};
 const item=key=>({target:{complete:true,checkedBy:'migration-current-contract',schemaHash:hash},restored:true,receipt:{schemaVersion:1,owner:x.cleanup.owner,key,sourceProduct:candidateSource,postgresImage,contractSha256:hash,metadataSha256:hash,privateContractSha256:hash},roleProof:structuredClone(proof)});
 x.combinedProof={sourceDiffVerified:true,sourceDiffSha256:hash,baselineSource:source,candidateSource,candidateProductTree:'1aa393776ac219f43c7a9de48a63632dce1c82bf',harness,harnessFingerprint:hash,fixtures:['ordinary','scope','reference'].flatMap(scene=>[100,1000,10000].map(scale=>({...item(scene+'-'+scale),scene,scale}))),gateway:item('gateway-controlplane-100')};
 x.micro.forEach(m=>m.entries.forEach(e=>e.backend='runtime-linear-map-v1'));return x;
}
test('combined proof has a positive case; each required proof independently blocks completion',()=>{assert.equal(verifyBaselineCompletion(combinedFixture()).complete,true);for(const change of [x=>delete x.combinedProof.fixtures[0].target,x=>delete x.combinedProof.fixtures[0].receipt,x=>delete x.combinedProof.fixtures[0].roleProof,x=>delete x.micro[0].entries[0].backend,x=>x.cells.pop(),x=>x.combinedProof.baselineSource=x.combinedProof.candidateSource]){const x=combinedFixture();change(x);assert.equal(verifyBaselineCompletion(x).complete,false,change.toString());}});

test('combined gate validates exact index names and complete gateway template proof',()=>{for(const change of [x=>x.combinedProof.fixtures[0].roleProof.missingApprovedIndexes=['wrong1','wrong2','wrong3','wrong4'],x=>delete x.combinedProof.gateway.receipt,x=>delete x.combinedProof.gateway.target.schemaHash,x=>x.combinedProof.gateway.roleProof.complete=false]){const x=combinedFixture();change(x);assert.equal(verifyBaselineCompletion(x).complete,false,change.toString());}});

test('combined completion binds full template receipt to owner source and exact scene scale',()=>{
 const changes=[x=>delete x.combinedProof.fixtures[0].receipt.schemaVersion,x=>x.combinedProof.fixtures[0].receipt.schemaVersion=2,x=>x.combinedProof.fixtures[0].receipt.owner='other-owner',x=>delete x.cleanup.owner,x=>x.combinedProof.fixtures[0].receipt.sourceProduct='e'.repeat(40),x=>x.combinedProof.fixtures[0].receipt.postgresImage='postgres:latest',x=>delete x.combinedProof.fixtures[0].receipt.privateContractSha256,x=>x.combinedProof.fixtures[0].receipt.privateContractSha256='bad',x=>{[x.combinedProof.fixtures[0].receipt,x.combinedProof.fixtures[1].receipt]=[x.combinedProof.fixtures[1].receipt,x.combinedProof.fixtures[0].receipt];},x=>x.combinedProof.gateway.receipt=x.combinedProof.fixtures[0].receipt,x=>x.combinedProof.candidateSource='e'.repeat(40)];
 assert.equal(verifyBaselineCompletion(combinedFixture()).complete,true);
 for(const change of changes){const x=combinedFixture();change(x);assert.equal(verifyBaselineCompletion(x).complete,false,change.toString());}
 const candidate=combinedFixture();candidate.mode='compare';candidate.variant='candidate';candidate.identity.source=candidate.combinedProof.candidateSource;candidate.cells.forEach(c=>c.variant='candidate');candidate.micro.forEach(m=>m.entries.forEach(e=>{e.mode='indexed';e.backend='runtime-frozen-dictionary-v1';}));for(const p of [...candidate.combinedProof.fixtures,candidate.combinedProof.gateway])Object.assign(p.roleProof,{role:'candidate',modelMatched:true,missingApprovedIndexes:[]});candidate.management.forEach(m=>{m.classificationManifest.sourceCommit=candidate.identity.source;m.samples.forEach(s=>s.shapes.forEach(shape=>shape.sourceEvidence.forEach(ref=>ref.commit=candidate.identity.source)));});for(const name of ['scopeManagement','referenceManagement'])candidate[name].forEach(m=>{m.classificationManifest.sourceCommit=candidate.identity.source;m.samples.forEach(s=>s.shapes.forEach(shape=>shape.sourceEvidence.forEach(ref=>ref.commit=candidate.identity.source)));});assert.equal(verifyBaselineCompletion(candidate).complete,true);
});
