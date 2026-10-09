import test from 'node:test';
import assert from 'node:assert/strict';
import {parseContainerStats,summarizeContainerWindow} from './resources.mjs';
const id='a'.repeat(64);
function frame(time='2026-10-09T00:00:01Z',cpu=1000000000){return {id,read:time,memory_stats:{usage:1000,limit:2000,stats:{anon:500,inactive_file:200}},cpu_stats:{cpu_usage:{total_usage:cpu},throttling_data:{periods:10,throttled_periods:2,throttled_time:100}},pids_stats:{current:3},secret:'canary-not-for-evidence'};}
test('docker_stats_retains_raw_counter_units_without_exporting_unselected_fields',()=>{
  const value=parseContainerStats(frame(),id);
  assert.deepEqual(value,{time:'2026-10-09T00:00:01Z',memoryUsageBytes:1000,memoryLimitBytes:2000,workingSetBytes:800,residentAnonymousBytes:500,residentMetric:'cgroup-v2-anon',cpuUsageNs:1000000000,throttledNs:100,periods:10,throttledPeriods:2,pids:3});
  const v1=frame();v1.memory_stats.stats={total_rss:450,total_inactive_file:100};assert.equal(parseContainerStats(v1,id).workingSetBytes,900);assert.equal(parseContainerStats(v1,id).residentMetric,'cgroup-v1-total-rss');
  for(const bad of [(()=>{const x=frame();x.id='b'.repeat(64);return x;})(),(()=>{const x=frame();delete x.memory_stats.stats.anon;return x;})(),(()=>{const x=frame();x.cpu_stats.cpu_usage.total_usage=-1;return x;})()])assert.throws(()=>parseContainerStats(bad,id),/invalid|identity|unavailable/i);
});
test('resource_window_excludes_warmup_and_drain_and_requires_continuous_in_window_intervals',()=>{
  const samples=[-1,1,2,3,5].map((seconds,i)=>{const x=parseContainerStats(frame(new Date(Date.UTC(2026,9,9)+seconds*1000).toISOString(),i*1000000000),id);x.workingSetBytes=seconds<0||seconds>4?1999:1000+seconds;return x;});
  const result=summarizeContainerWindow(samples,{start:'2026-10-09T00:00:00Z',end:'2026-10-09T00:00:04Z',cpus:2,memoryBytes:2000});
  assert.equal(result.complete,true);assert.equal(result.samples,3);assert.equal(result.peakWorkingSetBytes,1003);assert.equal(result.meanCpuCores,1);assert.equal(result.peakCpuQuotaFraction,.5);assert.equal(result.cpuIntervals,2);
  assert.equal(result.firstSampleGapMs,1000);assert.equal(result.lastSampleGapMs,1000);assert.equal(result.maxGapMs,1000);assert.equal(result.cpuMeasurementScope,'intervals-wholly-inside-window');
  assert.equal(summarizeContainerWindow(samples,{start:'2026-10-09T00:00:00Z',end:'2026-10-09T00:00:04Z',cpus:2,memoryBytes:9999}).complete,false);
  assert.equal(summarizeContainerWindow(samples.filter((_,i)=>i!==2&&i!==3),{start:'2026-10-09T00:00:00Z',end:'2026-10-09T00:00:04Z',cpus:2,memoryBytes:2000}).complete,false);
  const reset=structuredClone(samples);reset[2].cpuUsageNs=0;assert.equal(summarizeContainerWindow(reset,{start:'2026-10-09T00:00:00Z',end:'2026-10-09T00:00:04Z',cpus:2,memoryBytes:2000}).complete,false);
});
import {createServer} from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import {quotas} from './environment.mjs';
import {createOwner} from './owner.mjs';
import {runWithContainerResources,waitForContainerSampler,verifyContainerResources} from './resources.mjs';
async function fixtureApi({foreign=false,missingClient=false,invalidClientFrame=false,clockMs=Date.UTC(2026,9,9)}={}){
  const root=await fs.mkdtemp('/private/tmp/webapi-resource-'),owner=await createOwner(root),output=path.join(root,'results');await fs.mkdir(output);
  const containers=Object.keys(quotas).map((service,i)=>({Id:String(i+1).repeat(64),Labels:{'com.docker.compose.project':owner.project,'com.webapi.performance.owner':foreign?'foreign':owner.owner,'com.docker.compose.service':service}}));let statsCalls=0;const responses=new Map();
  const server=createServer((request,response)=>{
    const url=new URL(request.url,'http://engine');response.setHeader('Content-Type','application/json');
    if(url.pathname.endsWith('/containers/json')){response.end(JSON.stringify(missingClient?containers.filter(c=>c.Labels['com.docker.compose.service']!=='client'):containers));return;}
    const target=containers.find(x=>url.pathname.includes(x.Id));
    if(url.pathname.endsWith('/json')){const quota=quotas[target.Labels['com.docker.compose.service']];response.end(JSON.stringify({Id:target.Id,Image:'sha256:'+'a'.repeat(64),Config:{Labels:target.Labels},HostConfig:{NanoCpus:quota.cpus*1e9,Memory:quota.memoryBytes},State:{Running:true},secret:'canary-not-for-evidence'}));return;}
    statsCalls++;response.writeHead(200);responses.set(target.Labels['com.docker.compose.service'],response);
    for(const seconds of [0,1,2,3,4]){const x=frame(new Date(clockMs+seconds*1000).toISOString(),seconds*1000000000);x.id=target.Id;x.memory_stats.limit=quotas[target.Labels['com.docker.compose.service']].memoryBytes;if(invalidClientFrame&&target.Labels['com.docker.compose.service']==='client')x.memory_stats.limit=0;const text=JSON.stringify(x)+'\n';for(let offset=0;offset<text.length;offset+=13)response.write(text.slice(offset,offset+13));}
    // A real stream remains open until the operation has completed.
  });const socket=path.join(root,'engine.sock');await new Promise(resolve=>server.listen(socket,resolve));
  return {root,owner,output,server,socket,streamsClosed:()=>[...responses.values()].every(r=>r.destroyed),injectInvalidClient:()=>{const c=containers.find(x=>x.Labels['com.docker.compose.service']==='client'),x=frame();x.id=c.Id;x.memory_stats.limit=0;responses.get('client').write(JSON.stringify(x)+'\n');},statsCalls:()=>statsCalls,disconnect:service=>responses.get(service).destroy(),end:service=>responses.get(service).end()};
}
test('unix_engine_stream_binds_all_actual_owner_containers_and_exports_only_selected_metrics',async()=>{
  const f=await fixtureApi(),key='test-cell';
  try {
    const result=await runWithContainerResources({manifest:f.owner,dockerHost:'unix://'+f.socket,apiVersion:'1.54',output:f.output,key,resultFile:'cell.json'},async()=>{
      await waitForContainerSampler(path.join(f.output,key+'.resources-ready.json'));
      await fs.writeFile(path.join(f.output,'cell.json'),JSON.stringify({windowStartedAt:'2026-10-09T00:00:00Z',windowEndedAt:'2026-10-09T00:00:04Z'}));
    });
    assert.equal(result.complete,true);assert.equal(Object.keys(result.services).length,7);assert.equal(result.services.client.summary.cpuIntervals,4);assert.equal(f.statsCalls(),7);
    const report=await fs.readFile(path.join(f.output,key+'.resources.json'),'utf8');assert.equal(report.includes('canary-not-for-evidence'),false);assert.equal(report.includes('Config'),false);
  }finally{f.server.closeAllConnections();await new Promise(resolve=>f.server.close(resolve));await fs.rm(f.root,{recursive:true,force:true});}
});
test('foreign_container_is_rejected_before_any_stats_stream_is_read',async()=>{
  const f=await fixtureApi({foreign:true}),key='foreign-test';
  try {
    await assert.rejects(()=>runWithContainerResources({manifest:f.owner,dockerHost:'unix://'+f.socket,apiVersion:'1.54',output:f.output,key,resultFile:'cell.json'},()=>waitForContainerSampler(path.join(f.output,key+'.resources-ready.json'))),/ownership/i);
    assert.equal(f.statsCalls(),0);
  }finally{f.server.closeAllConnections();await new Promise(resolve=>f.server.close(resolve));await fs.rm(f.root,{recursive:true,force:true});}
});
test('readiness_publisher_does_not_expose_truncated_json_to_concurrent_readers',async()=>{
  const root=await fs.mkdtemp('/private/tmp/webapi-readiness-'),file=path.join(root,'ready.json');
  try {
    await fs.writeFile(file,JSON.stringify({schemaVersion:1,ready:true}));
    // Publish a larger replacement through the same real write path used by the
    // monitor; readers must see either complete old JSON or complete new JSON.
    const {publishSamplerJson}=await import('./resources.mjs');
    let done=false,failures=0;
    const reader=(async()=>{while(!done){try{JSON.parse(await fs.readFile(file,'utf8'));}catch{failures++;}}})();
    for(let i=0;i<20;i++)await publishSamplerJson(file,{schemaVersion:1,ready:true,padding:'x'.repeat(256*1024)});
    done=true;await reader;assert.equal(failures,0);
  }finally{await fs.rm(root,{recursive:true,force:true});}
});

async function disconnectedFixture({service='client',early=false,eof=false}={}){
 const clockMs=Date.now()-(early?3000:10000),f=await fixtureApi({clockMs}),key='disconnect';
 try{
  return await runWithContainerResources({manifest:f.owner,dockerHost:'unix://'+f.socket,apiVersion:'1.54',output:f.output,key,resultFile:'cell.json'},async()=>{
   await waitForContainerSampler(path.join(f.output,key+'.resources-ready.json'));
   await fs.writeFile(path.join(f.output,'cell.json'),JSON.stringify({complete:true,windowStartedAt:new Date(clockMs).toISOString(),windowEndedAt:new Date(clockMs+4000).toISOString()}));
   (eof?f.end:f.disconnect)(service);await new Promise(resolve=>setTimeout(resolve,50));
  });
 }finally{f.server.closeAllConnections();await new Promise(resolve=>f.server.close(resolve));await fs.rm(f.root,{recursive:true,force:true});}
}
test('completed_oneoff_client_disconnect_after_window_keeps_complete_raw_metrics_and_records_event',async()=>{
 const result=await disconnectedFixture();assert.ok(Object.values(result.services).every(s=>s.summary.complete));assert.equal(result.complete,true);assert.equal(result.errors.length,0);assert.ok(result.services.client.streamErrors.length>0);assert.ok(result.services.client.streamErrors.every(e=>e.disposition==='after-completed-client-window'));assert.equal(verifyContainerResources(result,{start:result.windowStartedAt,end:result.windowEndedAt}),true);const tampered=structuredClone(result);tampered.services.client.streamErrors[0].time=result.windowStartedAt;assert.equal(verifyContainerResources(tampered,{start:result.windowStartedAt,end:result.windowEndedAt}),false);
});
test('client_disconnect_inside_window_and_server_disconnect_even_after_window_remain_failures',async()=>{
 for(const options of [{early:true},{service:'gateway-a'}]){const result=await disconnectedFixture(options);assert.equal(result.complete,false);assert.ok(result.errors.length>0);}
});
test('normal_client_eof_before_measured_window_end_is_not_a_complete_window',async()=>{const result=await disconnectedFixture({early:true,eof:true});assert.equal(result.complete,false);});

test('resource_diagnostic_requires_three_distinct_full_windows_and_cleanup_without_accepting_performance_baseline',async()=>{
  const resources=await import('./resources.mjs');
  assert.equal(typeof resources.verifyResourceDiagnosticCompletion,'function');
  const cells=[1,2,3].map(repeat=>{
    const start=new Date(Date.UTC(2026,9,9)+repeat*30000).toISOString(),end=new Date(Date.parse(start)+20000).toISOString(),services={};
    for(const [name,quota] of Object.entries(quotas)){
      const samples=Array.from({length:21},(_,i)=>{const sample=parseContainerStats(frame(new Date(Date.parse(start)+i*1000).toISOString(),i*1000000),id);sample.memoryLimitBytes=quota.memoryBytes;return sample;});
      services[name]={containerId:id,imageId:'sha256:'+'b'.repeat(64),...quota,samples,summary:summarizeContainerWindow(samples,{start,end,...quota})};
    }
    return {variant:'validation',scale:10000,auth:'api-key',concurrency:32,repeat,nodeMode:'dual',complete:true,expected:2,unexpected:0,timeout:0,incomplete:0,warmupMs:10000,durationMs:20000,windowStartedAt:start,windowEndedAt:end,nodes:[{expected:1,unexpected:0,timeout:0,incomplete:0},{expected:1,unexpected:0,timeout:0,incomplete:0}],containerResources:{schemaVersion:1,source:'docker-engine-stats-stream',apiVersion:'1.45',complete:true,errors:[],windowStartedAt:start,windowEndedAt:end,services,potentialClientOrBackendSaturation:[],saturationThreshold:.9}};
  });
  const input={cells,cleanup:{remaining:0,privateInputsDeleted:true},secretScan:{violations:0,filesScanned:1,findings:[]}};
  assert.equal(resources.verifyResourceDiagnosticCompletion(input),true);
  const noMapping=structuredClone(input);noMapping.cells.forEach(c=>c.auth='no-mapping');assert.equal(resources.verifyResourceDiagnosticCompletion(noMapping),false);assert.equal(resources.verifyResourceDiagnosticCompletion({...noMapping,auth:'no-mapping'}),true);
  for(const mutate of [x=>x.cells.pop(),x=>x.cells[1].repeat=1,x=>x.cells[0].timeout=1,x=>x.cells[0].nodes[1].expected=0,x=>x.cells[0].containerResources.complete=false,x=>x.cells[0].durationMs=2500,x=>x.cleanup.privateInputsDeleted=false,x=>x.secretScan.violations=1,x=>x.cells[1].windowStartedAt=x.cells[0].windowStartedAt]){
    const broken=structuredClone(input);mutate(broken);assert.equal(resources.verifyResourceDiagnosticCompletion(broken),false);
  }
});

test('missing_client_retains_selected_startup_inventory_and_failure_code_without_exception_text',async()=>{
 const f=await fixtureApi({missingClient:true}),key='missing-client';
 try{
  await assert.rejects(()=>runWithContainerResources({manifest:f.owner,dockerHost:'unix://'+f.socket,apiVersion:'1.54',output:f.output,key,resultFile:'cell.json'},async()=>{throw Object.assign(Error('private-canary-failure'),{code:9,signal:null});}),/missing/i);
  const report=JSON.parse(await fs.readFile(path.join(f.output,key+'.resources.json'),'utf8'));
  assert.equal(report.complete,false);assert.equal(report.startupInventory.lastRunningServices.length,6);assert.equal(report.startupInventory.lastRunningServices.includes('client'),false);assert.ok(report.startupInventory.polls>1);assert.equal(report.workloadFailure.exitCode,9);assert.equal(JSON.stringify(report).includes('private-canary-failure'),false);
 }finally{f.server.closeAllConnections();await new Promise(resolve=>f.server.close(resolve));await fs.rm(f.root,{recursive:true,force:true});}
});

test('invalid_stats_frame_is_retained_privately_with_service_time_metadata',async()=>{
 const f=await fixtureApi({invalidClientFrame:true}),key='invalid-frame';
 try{
  await assert.rejects(()=>runWithContainerResources({manifest:f.owner,dockerHost:'unix://'+f.socket,apiVersion:'1.54',output:f.output,key,resultFile:'cell.json'},()=>waitForContainerSampler(path.join(f.output,key+'.resources-ready.json'))),/invalid/i);
  const report=JSON.parse(await fs.readFile(path.join(f.output,key+'.resources.json'),'utf8'));assert.equal(report.complete,false);assert.ok(report.services.client.frameFailures[0].time);assert.equal(JSON.stringify(report).includes('canary-not-for-evidence'),false);
  const file=path.join(f.owner.directory,key+'.client.invalid-frame.json');assert.equal((await fs.stat(file)).mode&0o777,0o600);assert.equal(JSON.parse(await fs.readFile(file,'utf8')).frame.memory_stats.limit,0);
 }finally{f.server.closeAllConnections();await new Promise(resolve=>f.server.close(resolve));await fs.rm(f.root,{recursive:true,force:true});}
});

test('client_completion_barrier_closes_stats_before_allowing_workload_exit',async()=>{
 const f=await fixtureApi(),key='held-client';
 try{
  const report=await runWithContainerResources({manifest:f.owner,dockerHost:'unix://'+f.socket,apiVersion:'1.54',output:f.output,key,resultFile:'cell.json',holdClientUntilSampled:true},async()=>{
   await waitForContainerSampler(path.join(f.output,key+'.resources-ready.json'));
   await fs.writeFile(path.join(f.output,'cell.json'),JSON.stringify({complete:true,windowStartedAt:'2026-10-09T00:00:00Z',windowEndedAt:'2026-10-09T00:00:04Z'}));
   await waitForContainerSampler(path.join(f.output,key+'.resources-finished.json'));assert.equal(f.streamsClosed(),true);
  });assert.equal(report.complete,true);assert.equal(report.workloadCompleted,true);
 }finally{f.server.closeAllConnections();await new Promise(resolve=>f.server.close(resolve));await fs.rm(f.root,{recursive:true,force:true});}
});

test('client_completion_barrier_still_rejects_invalid_frame_before_sampler_stop',async()=>{
 const f=await fixtureApi(),key='held-client-invalid';
 try{
  const report=await runWithContainerResources({manifest:f.owner,dockerHost:'unix://'+f.socket,apiVersion:'1.54',output:f.output,key,resultFile:'cell.json',holdClientUntilSampled:true},async()=>{
   await waitForContainerSampler(path.join(f.output,key+'.resources-ready.json'));f.injectInvalidClient();await new Promise(r=>setTimeout(r,50));
   await fs.writeFile(path.join(f.output,'cell.json'),JSON.stringify({complete:true,windowStartedAt:'2026-10-09T00:00:00Z',windowEndedAt:'2026-10-09T00:00:04Z'}));await waitForContainerSampler(path.join(f.output,key+'.resources-finished.json'));
  });assert.equal(report.complete,false);assert.ok(report.errors.includes('invalid-stats-stream'));assert.equal(report.workloadCompleted,true);
 }finally{f.server.closeAllConnections();await new Promise(resolve=>f.server.close(resolve));await fs.rm(f.root,{recursive:true,force:true});}
});
