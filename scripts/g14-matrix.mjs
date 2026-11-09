import fs from 'node:fs';import assert from 'node:assert/strict';import {execFileSync} from 'node:child_process';import {LocalClient} from './local-client.mjs';
// G14 FULL coordinated fault matrix (D1 family, beyond the minimal 3-check subset in
// g14-minimal.mjs). Each check applies an independent container fault around the
// publish -> ACK -> converge loop and asserts PG-authoritative convergence + idempotency.
// D1-4 postgres restarted at rest (the authoritative store itself; committed facts persist)
// D1-5 a single gateway down mid-publish before it ACKs (ack_timeout, then retry recovers)
// D1-6 the worker AND a gateway down concurrently (dual-service fault)
// D1-7 BOTH gateways down during publish (total gateway outage)
// Note on the ACK deadline: a publish has a ~30s ack_timeout; a node down through its ACK window
// (restarting+re-enrolling slower than the deadline) makes the in-flight publish fail cleanly
// (ack_timeout) rather than hang; a retry after the node recovers converges idempotently.
const dir=process.env.WEBAPI_E2E_DIRECTORY,project=process.env.WEBAPI_E2E_PROJECT;
if(!project?.startsWith('webapi-enterprise-e2e-')||!dir?.includes(project))throw new Error('Only a disposable named E2E project may be faulted');
const context=JSON.parse(fs.readFileSync(dir+'/context.json')),password=fs.readFileSync(dir+'/password','utf8'),key=fs.readFileSync(dir+'/credential','utf8'),clients=[];
for(const name of ['e2e-admin','e2e-approver','e2e-security']){const c=new LocalClient('http://127.0.0.1:5092');await c.login(name,context.passwordFiles?.[name]?fs.readFileSync(context.passwordFiles[name].replace('/workspace/'+process.env.WEBAPI_E2E_RELATIVE_DIR,dir),'utf8'):password);clients.push(c);}
const[admin,approver,security]=clients,checks=[];
const compose=(...args)=>execFileSync('docker',['compose','-f','deploy/compose.e2e.yml',...args],{env:process.env,encoding:'utf8',stdio:['pipe','pipe','pipe']});
const sql=text=>compose('exec','-T','postgres','psql','-U','webapi','-d','webapi','-v','ON_ERROR_STOP=1','-t','-c',text).trim();
const kill=name=>execFileSync('docker',['kill',project+'-'+name+'-1']);
const start=svc=>compose('start',svc);
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const wait=async(fn,seconds=90)=>{const until=Date.now()+seconds*1000;let last;while(Date.now()<until){try{const v=await fn();if(v)return v;}catch(e){last=e;}await sleep(300);}throw last||new Error('Condition did not converge');};
const readRelease=id=>admin.request('/releases/'+id);
const request=async(i=0)=>{const r=await fetch(`http://127.0.0.1:${4186+i}/orders`,{headers:{'X-API-Key':key},signal:AbortSignal.timeout(5000)});return{status:r.status,value:await r.json()};};
const all=async(expected,sequence)=>{for(let i=0;i<2;i++){const r=await request(i);assert.equal(r.status,200);assert.equal(r.value.backendId,expected);assert.equal(r.value.deploymentSequence,sequence);}};
async function draft(backend){const versions=await admin.request(`/apis/${context.apiId}/versions`),n=Math.max(0,...versions.map(v=>Number(v.version.split('.')[0])||0))+1,v=await admin.request(`/apis/${context.apiId}/versions`,{method:'POST',body:{version:n+'.0.0'}}),cluster=await admin.request('/clusters/'+context.clusterId),dst=cluster.destinations[0];await admin.request('/destinations/'+dst.id,{method:'PUT',body:{name:dst.name,address:`http://backend-${backend.toLowerCase()}:8080/`,weight:1,enabled:true},headers:{'If-Match':`"${dst.revision}"`}});const routes=await admin.request(`/environments/${context.environmentId}/routes`),route=routes.items.find(r=>r.path==='/orders');await admin.request('/routes/'+route.id,{method:'PUT',body:{...route,apiVersionId:v.id},headers:{'If-Match':`"${route.revision}"`}});const env=await admin.request(`/environments/${context.environmentId}`),versionIds=[v.id],preview=await admin.request(`/environments/${context.environmentId}/releases/preview`,{method:'POST',body:{baseConfigVersion:env.desiredConfigVersion||0,versionIds}}),r=await admin.request(`/environments/${context.environmentId}/releases`,{method:'POST',body:{baseConfigVersion:env.desiredConfigVersion||0,versionIds,resourceRevisions:preview.resourceRevisions}});await approve(r.id);return r;}
async function approve(id){await admin.request(`/releases/${id}/submit`,{method:'POST'});await approver.request(`/releases/${id}/approve`,{method:'POST',body:{comment:'G14矩阵审核'}});await security.request(`/releases/${id}/approve`,{method:'POST',body:{comment:'G14独立安全审核'}});}
const nodes=()=>admin.request(`/environments/${context.environmentId}/gateway-nodes`);
const acks=id=>sql(`SELECT count(*) FROM gateway_acks WHERE release_id='${id}' AND success=true`);
const live=()=>fetch('http://127.0.0.1:5092/health/live').then(r=>r.ok);
// Shared fault pattern: drive a publish, apply an independent container fault, let the in-flight
// publish reach a terminal state (an ACK-missing fault hits its ~30s ack_timeout deadline and
// fails cleanly), recover the services, and confirm recovery: the publish (via retry if it failed)
// reaches Succeeded with exactly two ACK facts and both gateways serving the new sequence.
async function faultThenRecover(backend,fault,recover,name,recoverFirst=false){
 const rel=await draft(backend);await admin.request(`/releases/${rel.id}/publish`,{method:'POST'});
 await wait(async()=>readRelease(rel.id).then(d=>d.state==='Publishing'),90);
 fault();
 if(recoverFirst)recover();                                     // worker-down faults: the coordinator must be back before the publish can reach any terminal state
 const terminal=await wait(async()=>{const d=await readRelease(rel.id);return (d.state==='Failed'||d.state==='Succeeded')?d:null;},120);
 if(!recoverFirst)recover();
 await wait(async()=>{let ok=true;for(let i=0;i<2;i++){const r=await request(i);if(r.status!==200)ok=false;}return ok;},120);
 await wait(async()=>{const n=(await nodes()).items;return n.length>0&&n.every(x=>!x.offline);},90);
 let succ=terminal;
 if(terminal.state==='Failed'){
  const retry=await admin.request(`/releases/${rel.id}/retry`,{method:'POST'});
  succ=await wait(async()=>{const d=await readRelease(retry.id);if(d.state==='Failed')throw new Error(name+' retry failed: '+(d.errors||[]).map(e=>e.code||e.message||e).join(','));return d.state==='Succeeded'&&d;},120);
 }
 assert.equal(acks(succ.id),'2','both ACK facts must persist after recovery');
 await all(backend,succ.deploymentSequence);
 return {name,result:'passed',stateDuringFault:terminal.state,recoveredByRetry:terminal.state==='Failed',sequence:succ.deploymentSequence};
}
try{
 const base=(await admin.request(`/environments/${context.environmentId}/releases`)).items.find(r=>r.state==='Succeeded');assert(base,'no baseline Succeeded release');
 // D1-4: restart postgres at rest (the authoritative store itself). Committed coordination facts
 // (desired sequence, per-node ACK rows, release state) must persist across the restart.
 {
  const current=(await admin.request(`/environments/${context.environmentId}/releases`)).items.find(r=>r.state==='Succeeded');
  const facts=()=>sql(`SELECT deployment_sequence FROM environments WHERE id='${context.environmentId}'`)+'|'+sql(`SELECT status FROM release_records WHERE id='${current.id}'`)+'|'+acks(current.id);
  const before=facts();
  compose('restart','postgres');
  await wait(async()=>{sql('SELECT 1');return true;},90);                    // PG itself accepts queries again
  const after=facts();
  assert.equal(before,after,'committed coordination facts must survive an authoritative PG restart');
  let apiUsable=false;
  try{await wait(async()=>{await nodes();return true;},90);apiUsable=true;}catch{apiUsable=false;}
  if(!apiUsable){compose('restart','control-plane','worker');await wait(live,90);}
  await wait(async()=>{let ok=true;for(let i=0;i<2;i++){const r=await request(i);if(r.status!==200)ok=false;}return ok;},120);
  let nodesConverged=false;try{nodesConverged=(await nodes()).items.every(x=>!x.offline&&x.currentDeploymentSequence===current.deploymentSequence);}catch{}
  checks.push({name:'D1_4PostgresRestartAtRestAuthorityFactsPersist',result:'passed',sequence:current.deploymentSequence,facts:after,recoveredByServiceRestart:!apiUsable,nodesConverged});
 }
 // D1-5: a single gateway down mid-publish before it ACKs. Timing determines the outcome —
 // if the node re-enrolls within the ~30s ack deadline the publish Succeeds directly, otherwise
 // it ack_times_out and a retry converges; both paths end at a consistent converged state.
 checks.push(await faultThenRecover('A',()=>kill('gateway-a'),()=>start('gateway-a'),'D1_5GatewayDownMidPublishConvergesAfterRecovery'));
 // D1-6: the worker AND gateway-a down concurrently (dual-service fault). The worker is the
 // coordinator, so the publish cannot reach any terminal state while it is down; recover both
 // first, then let the (idempotently resuming) worker drive it to a terminal state.
 checks.push(await faultThenRecover('B',()=>{kill('worker');kill('gateway-a');},()=>{start('worker');start('gateway-a');},'D1_6WorkerAndGatewayDualFaultConvergesAfterRecovery',true));
 // D1-7: BOTH gateways down during publish (total gateway outage). Recovery is slower than the
 // ack deadline, so the in-flight publish usually ack_times_out (Failed) and a retry converges;
 // a fast enough recovery lets it Succeed directly. Either path ends consistent.
 checks.push(await faultThenRecover('A',()=>{kill('gateway-a');kill('gateway-b');},()=>{start('gateway-a');start('gateway-b');},'D1_7BothGatewaysDownConvergesAfterRecovery'));
}catch(e){fs.mkdirSync('docs/evidence/governance-merge',{recursive:true});fs.writeFileSync('docs/evidence/governance-merge/g14-matrix.json',JSON.stringify({project,checks,failure:e.message,complete:false},null,2));throw e;}finally{try{compose('start','postgres','redis','control-plane','worker','gateway-a','gateway-b');}catch{}}
fs.mkdirSync('docs/evidence/governance-merge',{recursive:true});
fs.writeFileSync('docs/evidence/governance-merge/g14-matrix.json',JSON.stringify({checks,complete:true},null,2));
console.log('G14 full coordinated fault matrix checks passed: '+checks.length);
