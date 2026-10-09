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
import {runWithContainerResources,waitForContainerSampler} from './resources.mjs';
async function fixtureApi({foreign=false}={}){
  const root=await fs.mkdtemp('/private/tmp/webapi-resource-'),owner=await createOwner(root),output=path.join(root,'results');await fs.mkdir(output);
  const containers=Object.keys(quotas).map((service,i)=>({Id:String(i+1).repeat(64),Labels:{'com.docker.compose.project':owner.project,'com.webapi.performance.owner':foreign?'foreign':owner.owner,'com.docker.compose.service':service}}));let statsCalls=0;
  const server=createServer((request,response)=>{
    const url=new URL(request.url,'http://engine');response.setHeader('Content-Type','application/json');
    if(url.pathname.endsWith('/containers/json')){response.end(JSON.stringify(containers));return;}
    const target=containers.find(x=>url.pathname.includes(x.Id));
    if(url.pathname.endsWith('/json')){const quota=quotas[target.Labels['com.docker.compose.service']];response.end(JSON.stringify({Id:target.Id,Image:'sha256:'+'a'.repeat(64),Config:{Labels:target.Labels},HostConfig:{NanoCpus:quota.cpus*1e9,Memory:quota.memoryBytes},State:{Running:true},secret:'canary-not-for-evidence'}));return;}
    statsCalls++;response.writeHead(200);
    for(const seconds of [0,1,2,3,4]){const x=frame(new Date(Date.UTC(2026,9,9)+seconds*1000).toISOString(),seconds*1000000000);x.id=target.Id;x.memory_stats.limit=quotas[target.Labels['com.docker.compose.service']].memoryBytes;const text=JSON.stringify(x)+'\n';for(let offset=0;offset<text.length;offset+=13)response.write(text.slice(offset,offset+13));}
    // A real stream remains open until the operation has completed.
  });const socket=path.join(root,'engine.sock');await new Promise(resolve=>server.listen(socket,resolve));
  return {root,owner,output,server,socket,statsCalls:()=>statsCalls};
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
