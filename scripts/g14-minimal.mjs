import fs from 'node:fs';import assert from 'node:assert/strict';import {execFileSync} from 'node:child_process';import {LocalClient} from './local-client.mjs';
// G14 minimal merge-gate subset (D1): independent container faults around the
// security/delivery loop, replayed on a disposable E2E environment.
const dir=process.env.WEBAPI_E2E_DIRECTORY,project=process.env.WEBAPI_E2E_PROJECT;
if(!project?.startsWith('webapi-enterprise-e2e-')||!dir?.includes(project))throw new Error('Only a disposable named E2E project may be faulted');
const context=JSON.parse(fs.readFileSync(dir+'/context.json')),password=fs.readFileSync(dir+'/password','utf8'),key=fs.readFileSync(dir+'/credential','utf8'),clients=[];
for(const name of ['e2e-admin','e2e-approver','e2e-security']){const c=new LocalClient('http://127.0.0.1:5092');await c.login(name,context.passwordFiles?.[name]?fs.readFileSync(context.passwordFiles[name].replace('/workspace/'+process.env.WEBAPI_E2E_RELATIVE_DIR,dir),'utf8'):password);clients.push(c);}
const[admin,approver,security]=clients,checks=[];
const compose=(...args)=>execFileSync('docker',['compose','-f','deploy/compose.e2e.yml',...args],{env:process.env,encoding:'utf8',stdio:['pipe','pipe','pipe']});
const sql=text=>compose('exec','-T','postgres','psql','-U','webapi','-d','webapi','-v','ON_ERROR_STOP=1','-t','-c',text).trim();
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const wait=async(fn,seconds=60)=>{const until=Date.now()+seconds*1000;let last;while(Date.now()<until){try{const v=await fn();if(v)return v;}catch(e){last=e;}await sleep(300);}throw last||new Error('Condition did not converge');};
const readRelease=id=>admin.request('/releases/'+id);
const request=async(i=0)=>{const r=await fetch(`http://127.0.0.1:${4186+i}/orders`,{headers:{'X-API-Key':key},signal:AbortSignal.timeout(5000)});return{status:r.status,value:await r.json()};};
const all=async(expected,sequence)=>{for(let i=0;i<2;i++){const r=await request(i);assert.equal(r.status,200);assert.equal(r.value.backendId,expected);assert.equal(r.value.deploymentSequence,sequence);}};
async function draft(backend){const versions=await admin.request(`/apis/${context.apiId}/versions`),n=Math.max(0,...versions.map(v=>Number(v.version.split('.')[0])||0))+1,v=await admin.request(`/apis/${context.apiId}/versions`,{method:'POST',body:{version:n+'.0.0'}}),cluster=await admin.request('/clusters/'+context.clusterId),dst=cluster.destinations[0];await admin.request('/destinations/'+dst.id,{method:'PUT',body:{name:dst.name,address:`http://backend-${backend.toLowerCase()}:8080/`,weight:1,enabled:true},headers:{'If-Match':`"${dst.revision}"`}});const routes=await admin.request(`/environments/${context.environmentId}/routes`),route=routes.items.find(r=>r.path==='/orders');await admin.request('/routes/'+route.id,{method:'PUT',body:{...route,apiVersionId:v.id},headers:{'If-Match':`"${route.revision}"`}});const env=await admin.request('/environments/'+context.environmentId),versionIds=[v.id],preview=await admin.request(`/environments/${context.environmentId}/releases/preview`,{method:'POST',body:{baseConfigVersion:env.desiredConfigVersion||0,versionIds}}),r=await admin.request(`/environments/${context.environmentId}/releases`,{method:'POST',body:{baseConfigVersion:env.desiredConfigVersion||0,versionIds,resourceRevisions:preview.resourceRevisions}});await approve(r.id);return r;}
async function approve(id){await admin.request(`/releases/${id}/submit`,{method:'POST'});await approver.request(`/releases/${id}/approve`,{method:'POST',body:{comment:'G14最小子集审核'}});await security.request(`/releases/${id}/approve`,{method:'POST',body:{comment:'G14独立安全审核'}});}
async function publish(backend){const r=await draft(backend);await admin.request(`/releases/${r.id}/publish`,{method:'POST'});return wait(async()=>{const d=await readRelease(r.id);if(d.state==='Failed')throw new Error('Unexpected failed release: '+(d.errors||[]).map(e=>e.code||e.message||e).join(','));return d.state==='Succeeded'&&d;},90);}
const nodes=()=>admin.request(`/environments/${context.environmentId}/gateway-nodes`);
const pgState=()=>`env|${sql(`SELECT desired_config_version||':'||deployment_sequence FROM environments WHERE id='${context.environmentId}'`)}|cfg|${sql('SELECT version_no||status||snapshot_hash FROM gateway_config_versions ORDER BY version_no DESC LIMIT 6')}`;
try{
 // D1-1: kill gateway-a after its ACK fact is persisted; gateway-b's ACK must still converge the release.
 // The (Publishing, a-ACKed) window can be shorter than one poll when both nodes ACK near-simultaneously;
 // a release that Succeeds before the kill is vacuous for this fault, so retry with a fresh release.
 {
  let done=null;
  for(let attempt=1;attempt<=3&&!done;attempt++){
   const rel=await draft('A');await admin.request(`/releases/${rel.id}/publish`,{method:'POST'});
   const nodeList=(await nodes()).items,na=nodeList.find(n=>n.nodeName==='gateway-a').id;
   let killed=false;
   try{
    await wait(async()=>{const d=await readRelease(rel.id);const t=d.targets.find(x=>x.nodeId===na);return d.state==='Publishing'&&t.acknowledged;},30);
    execFileSync('docker',['kill',project+'-gateway-a-1']);killed=true;
    done=await wait(async()=>{const d=await readRelease(rel.id);if(d.state==='Failed')throw new Error('D1-1 release failed');return d.state==='Succeeded'&&d;},90);
   }catch(e){
    if(killed)throw e;
    const state=(await readRelease(rel.id)).state;
    assert.equal(state,'Succeeded','vacuous D1-1 attempt must be a clean success, got '+state);
   }
   try{execFileSync('docker',['kill',project+'-gateway-a-1']);}catch{}
   compose('start','gateway-a');
   await wait(async()=>{const r=await request(0);return r.status===200;},90);
  }
  assert(done,'D1-1 kill window never caught in 3 attempts');
  assert.equal(done.targets.filter(t=>t.acknowledged).length,2,'both ACK facts must be persisted');
  checks.push({name:'D1_1KilledGatewayAfterAckConvergesViaOtherNode',result:'passed',sequence:done.deploymentSequence});
  compose('start','gateway-a');
  await wait(async()=>{const r=await request(0);return r.status===200&&r.value.deploymentSequence===done.deploymentSequence;},90);
 }
 // D1-2: kill the worker while the release is in flight; recovery must converge and stay idempotent.
 {
  const rel=await draft('B');await admin.request(`/releases/${rel.id}/publish`,{method:'POST'});
  await wait(async()=>{const d=await readRelease(rel.id);return d.state==='Publishing';},90);
  execFileSync('docker',['kill',project+'-worker-1']);
  const done=await wait(async()=>{const d=await readRelease(rel.id);if(d.state==='Failed')throw new Error('D1-2 release failed');return d.state==='Succeeded'&&d;},90);
  compose('start','worker');
  await wait(async()=>{const r=await fetch('http://127.0.0.1:5092/health/live');return r.ok;},60);
  assert.equal(sql(`SELECT count(*) FROM gateway_acks WHERE release_id='${done.id}' AND success=true`),'2','exactly one success ACK fact per node, no duplicates');
  execFileSync('docker',['kill',project+'-gateway-b-1']);compose('start','gateway-b');
  await wait(async()=>{const r=await request(1);return r.status===200&&r.value.deploymentSequence===done.deploymentSequence;},90);
  assert.equal(sql(`SELECT count(*) FROM gateway_acks WHERE release_id='${done.id}' AND success=true`),'2','re-ACK after restart must be an idempotent no-op');
  assert.equal((await readRelease(done.id)).state,'Succeeded');
  await all('B',done.deploymentSequence);
  checks.push({name:'D1_2WorkerKilledMidPublishIdempotentContinuation',result:'passed',sequence:done.deploymentSequence});
 }
 // D1-3: coordinated Redis restart with both gateways online: PG is authoritative, cache never writes back.
 {
  const before=pgState();
  const current=(await (await admin.request(`/environments/${context.environmentId}/releases`)).items).find(r=>r.state==='Succeeded');
  compose('restart','redis');
  await sleep(1500);
  await all('B',current.deploymentSequence);
  await wait(async()=>{const n=(await nodes()).items;return n.every(x=>!x.offline&&x.currentDeploymentSequence===current.deploymentSequence);},90);
  assert.equal(before,pgState(),'PG state must be byte-identical across the coordinated restart');
  await all('B',current.deploymentSequence);
  checks.push({name:'D1_3CoordinatedRedisRestartRebuildsFromPg',result:'passed',sequence:current.deploymentSequence});
 }
}catch(e){fs.mkdirSync('docs/evidence/governance-merge',{recursive:true});fs.writeFileSync('docs/evidence/governance-merge/g14-minimal.json',JSON.stringify({project,checks,failure:e.message,complete:false},null,2));throw e;}finally{try{compose('start','redis','control-plane','worker','gateway-a','gateway-b');}catch{}}
fs.mkdirSync('docs/evidence/governance-merge',{recursive:true});
fs.writeFileSync('docs/evidence/governance-merge/g14-minimal.json',JSON.stringify({checks,complete:true},null,2));
console.log('G14 minimal merge-gate checks passed: '+checks.length);
