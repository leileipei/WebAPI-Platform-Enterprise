import fs from 'node:fs';import assert from 'node:assert/strict';import {randomUUID} from 'node:crypto';import {scenario,wait,sleep} from './observability-scenario.mjs';
const s=await scenario(),checks=[];
const pass=(name,details={})=>{checks.push({name,status:'passed',...details});console.log(name+' passed.');};
const live=async(expected)=>{for(let n=0;n<2;n++){const r=await s.request(n);assert.equal(r.status,200);if(expected)assert.equal(r.value.backendId,expected);}};
async function unavailable(path){await assert.rejects(()=>s.query(path),e=>e.status===503);}
try{
 const a=await s.publish('A');await live('A');
 // No invented API field: map actual management destination ID in the frozen candidate.
 const trace=randomUUID().replaceAll('-','');await s.backend('/__test/hold-next');const old=s.request(0,'/orders',trace,true,30000);await wait(()=>fetch(s.endpoints.backend_a+'/__test/hold-state').then(r=>r.json()),x=>x.started,10);
 const b=await s.publish('B');await live('B');await s.backend('/__test/release');const original=await old;assert.equal(original.value.backendId,'A');assert.equal(original.value.deploymentSequence,a.deploymentSequence);
 const log=await wait(()=>s.query('logs',{traceId:trace}),x=>x.data.items.length===1);const oldLog=log.data.items[0],destination=a.frozenCandidate.clusters.flatMap(c=>c.destinations).find(d=>d.id===oldLog.destinationId);assert(destination);const destinationBackend=new URL(destination.address).hostname==='backend-a'?'A':'B';assert.equal(destinationBackend,'A');assert.equal(oldLog.deploymentSequence,a.deploymentSequence);pass('OldARequestAcrossBSwitchKeepsActualADestinationAndTelemetry',{destinationId:destination.id,destinationBackend,sequence:oldLog.deploymentSequence});
 const rollback=await s.rollback(b,a.targetConfigVersion);assert(rollback.deploymentSequence>b.deploymentSequence);await live('A');pass('ActualApprovedRollbackSurvivesObservationStack',{sequence:rollback.deploymentSequence});
 for(const[name,path]of [['loki','logs'],['tempo','traces'],['prometheus','metrics']]){
  s.compose('stop',name);try{await unavailable(path);await live('A');const r=await s.publish('B');await live('B');assert(r.targets.every(t=>t.acknowledged));}finally{s.compose('start',name);}
  const recovered=await wait(()=>s.query(path),()=>true,60);assert(recovered);await s.publish('A');pass(name+'StopAndRecoveryKeepProxyAndApprovedPublishing');
 }
 // Collector down does not block proxy. A burst fills the real bounded queue.
 const outageStart=Date.now();s.compose('stop','collector');
 try{
  for(let batch=0;batch<10;batch++){const results=await Promise.all(Array.from({length:20},(_,n)=>s.request(n%2)));assert(results.every(r=>r.status===200));}
  await live('A');const published=await s.publish('B');await live('B');assert(published.targets.every(t=>t.acknowledged));
  await sleep(Math.max(0,50_000-(Date.now()-outageStart)));
  const stale=await s.query('metrics');assert.equal(stale.sourceState,'Partial');assert.equal(s.metric(stale,'request_rps').value,null);
 }finally{s.compose('start','collector');}
 await wait(()=>s.raw(`sum(webapi_telemetry_dropped_total{webapi_environment_id="${s.context.environmentId}"})`),rows=>rows.some(row=>Number(row.value[1])>0),60);
 const resumed=await wait(()=>s.raw(`webapi_telemetry_last_observed_timestamp_seconds{webapi_environment_id="${s.context.environmentId}"}`),rows=>rows.length===2&&rows.every(row=>Number(row.value[1])>outageStart/1000+45),60);
 const end=Date.now(),window=Math.ceil((end-outageStart)/1000)+15;
 const gap=await s.query('metrics',s.range(window,end));assert.equal(gap.sourceState,'Partial');assert.equal(gap.coverage.complete,false);assert.equal(gap.coverage.reason,'time_window_coverage_incomplete');assert(resumed.length===2);pass('CollectorOutageQueueFullProxySuccessAndRealMiddleGap',{successfulBurstRequests:200,windowSeconds:window,reason:gap.coverage.reason});
 // One actual node stops while the other continues serving and reporting.
 s.compose('stop','gateway-b');try{await sleep(48_000);const partial=await s.query('metrics');assert.equal(partial.sourceState,'Partial');assert(partial.coverage.missingNodes.includes('gateway-b'));assert.equal((await s.request(0)).status,200);}finally{s.compose('start','gateway-b');}
 await wait(()=>s.request(1),r=>r.status===200,30);pass('SingleNodeCollectionExpiryAndActualProcessRecovery');
 // Capture a real in-flight lease, kill its process, and let the other process claim after PG expiry.
 s.compose('stop','worker','worker-b');const rule=await s.createRule('真实租约接管','request_rps',1000000);s.compose('pause','prometheus');s.compose('start','worker');
 let leased;try{
  leased=await wait(async()=>s.sql(`SELECT lease_owner || '|' || lease_token FROM alert_evaluation_states WHERE rule_id='${rule.id}' AND lease_until>clock_timestamp()`),text=>!!text,15);const [owner,token]=leased.split('|');s.compose('kill','-s','KILL','worker');s.compose('start','worker-b');
  const takeover=await wait(async()=>s.sql(`SELECT lease_owner || '|' || lease_token FROM alert_evaluation_states WHERE rule_id='${rule.id}' AND lease_until>clock_timestamp() AND lease_token>${Number(token)}`),text=>!!text&&text.split('|')[0]!==owner,45);assert(Number(takeover.split('|')[1])>Number(token));pass('ActualWorkerKillPgLeaseExpiresSecondWorkerTakesOver',{oldToken:Number(token),newToken:Number(takeover.split('|')[1])});
 }finally{s.compose('unpause','prometheus');s.compose('start','worker','worker-b');}
 await wait(async()=>{assert.equal((await s.request(0)).status,200);return s.admin.request('/observability/alert-rules/'+rule.id);},r=>r.evaluationState==='Known',100);
 // CP and Redis offline; actual gateway restart must use LKG without a source management shortcut.
 const expected=(await s.request(0)).value;s.compose('stop','worker','worker-b','control-plane','redis');
 try{s.compose('restart','gateway-a');await wait(()=>s.request(0),r=>r.status===200&&r.value.backendId===expected.backendId&&r.value.deploymentSequence===expected.deploymentSequence,30);await live(expected.backendId);}finally{s.compose('start','redis','control-plane','worker','worker-b');}
 await wait(()=>s.admin.request('/environments/'+s.context.environmentId),()=>true,30);pass('ActualGatewayLkgRestartWithControlPlaneRedisOffline');
 fs.writeFileSync('docs/evidence/observability/faults.json',JSON.stringify({complete:true,project:s.project,architecture:'Linux '+s.compose('exec','-T','gateway-a','uname','-m').trim(),settings:s.settings,checks},null,2));
}catch(e){fs.writeFileSync('docs/evidence/observability/faults.json',JSON.stringify({complete:false,project:s.project,checks,failure:e.message},null,2));throw e;}finally{try{s.compose('unpause','prometheus');}catch{}try{s.compose('start','collector','prometheus','loki','tempo','gateway-b','redis','control-plane','worker','worker-b');}catch{}try{await s.backend('/__test/release');}catch{}}
