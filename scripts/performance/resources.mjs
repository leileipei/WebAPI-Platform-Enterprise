const counter=value=>Number.isSafeInteger(value)&&value>=0;
export function parseContainerStats(frame,id){
  if(!/^[a-f0-9]{64}$/.test(id??'')||frame?.id!==id)throw Error('Container stats identity invalid');
  const memory=frame.memory_stats??{},stats=memory.stats??{},cpu=frame.cpu_stats??{},throttle=cpu.throttling_data??{};
  const v2=Object.hasOwn(stats,'anon'),resident=v2?stats.anon:stats.total_rss,cache=v2?stats.inactive_file:stats.total_inactive_file;
  const values=[memory.usage,memory.limit,resident,cache,cpu.cpu_usage?.total_usage,throttle.periods,throttle.throttled_periods,throttle.throttled_time];
  if(!Number.isFinite(Date.parse(frame.read))||!values.every(counter)||memory.limit===0||!counter(frame.pids_stats?.current))throw Error('Container stats counters unavailable or invalid');
  return {time:frame.read,memoryUsageBytes:memory.usage,memoryLimitBytes:memory.limit,workingSetBytes:Math.max(0,memory.usage-cache),residentAnonymousBytes:resident,residentMetric:v2?'cgroup-v2-anon':'cgroup-v1-total-rss',cpuUsageNs:cpu.cpu_usage.total_usage,throttledNs:throttle.throttled_time,periods:throttle.periods,throttledPeriods:throttle.throttled_periods,pids:frame.pids_stats.current};
}
export function summarizeContainerWindow(samples,{start,end,cpus,memoryBytes}={}){
  const started=Date.parse(start),ended=Date.parse(end),missing=[];
  const check=(condition,reason)=>{if(!condition)missing.push(reason);};
  check(Number.isFinite(started)&&ended>started&&Number.isFinite(cpus)&&cpus>0&&counter(memoryBytes)&&memoryBytes>0,'invalid-window-or-quota');
  check(Array.isArray(samples)&&samples.every(s=>Number.isFinite(Date.parse(s.time))&&['memoryUsageBytes','memoryLimitBytes','workingSetBytes','residentAnonymousBytes','cpuUsageNs','throttledNs','periods','throttledPeriods','pids'].every(k=>counter(s[k]))&&['cgroup-v1-total-rss','cgroup-v2-anon'].includes(s.residentMetric)),'invalid-samples');
  const measured=Array.isArray(samples)?samples.filter(s=>Date.parse(s.time)>=started&&Date.parse(s.time)<=ended):[];
  check(measured.length>=2,'insufficient-window-samples');
  check(measured.every(s=>s.memoryLimitBytes===memoryBytes),'wrong-memory-quota');
  const first=measured[0],last=measured.at(-1),firstSampleGapMs=first?Date.parse(first.time)-started:null,lastSampleGapMs=last?ended-Date.parse(last.time):null;
  const intervals=[];
  for(let i=1;i<measured.length;i++){
    const a=measured[i-1],b=measured[i],durationMs=Date.parse(b.time)-Date.parse(a.time),cpuDeltaNs=b.cpuUsageNs-a.cpuUsageNs,throttledDeltaNs=b.throttledNs-a.throttledNs;
    check(durationMs>0&&cpuDeltaNs>=0&&throttledDeltaNs>=0&&b.periods>=a.periods&&b.throttledPeriods>=a.throttledPeriods,'non-monotonic-counter-or-time');
    if(durationMs>0&&cpuDeltaNs>=0&&throttledDeltaNs>=0)intervals.push({durationMs,cpuDeltaNs,throttledDeltaNs,cpuCores:cpuDeltaNs/(durationMs*1e6)});
  }
  const maxGapMs=intervals.length?Math.max(...intervals.map(i=>i.durationMs)):null;
  check(firstSampleGapMs!==null&&lastSampleGapMs!==null&&firstSampleGapMs<=2500&&lastSampleGapMs<=2500&&maxGapMs!==null&&maxGapMs<=2500,'sampling-gap');
  const cpuDurationMs=intervals.reduce((s,i)=>s+i.durationMs,0),cpuNs=intervals.reduce((s,i)=>s+i.cpuDeltaNs,0);
  const peak=field=>measured.length?Math.max(...measured.map(s=>s[field])):null;
  return {complete:missing.length===0,missing:[...new Set(missing)],samples:measured.length,cpuIntervals:intervals.length,firstSampleGapMs,lastSampleGapMs,maxGapMs,peakUsageBytes:peak('memoryUsageBytes'),peakWorkingSetBytes:peak('workingSetBytes'),peakResidentAnonymousBytes:peak('residentAnonymousBytes'),residentMetric:first?.residentMetric??null,peakMemoryMeasurementScope:'sampled-points-inside-window',cpuMeasurementScope:'intervals-wholly-inside-window',cpuDurationMs,cpuNs,meanCpuCores:cpuDurationMs?cpuNs/(cpuDurationMs*1e6):null,peakCpuCores:intervals.length?Math.max(...intervals.map(i=>i.cpuCores)):null,peakCpuQuotaFraction:intervals.length?Math.max(...intervals.map(i=>i.cpuCores))/cpus:null,throttledNs:intervals.reduce((s,i)=>s+i.throttledDeltaNs,0)};
}
import fs from 'node:fs/promises';
import path from 'node:path';
import http from 'node:http';
import {randomUUID} from 'node:crypto';
import {validateOwner} from './owner.mjs';
import {quotas} from './environment.mjs';
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
export async function publishSamplerJson(file,value){
  const pending=file+'.pending-'+randomUUID();
  try{await fs.writeFile(pending,JSON.stringify(value,null,2)+'\n',{flag:'wx'});await fs.rename(pending,file);}
  finally{await fs.rm(pending,{force:true});}
}
const writeJson=publishSamplerJson;
export async function waitForContainerSampler(file){
  const deadline=Date.now()+20000;
  while(Date.now()<deadline){
    let ready;
    try {ready=JSON.parse(await fs.readFile(file,'utf8'));}catch(error){if(error.code!=='ENOENT')throw Error('Container sampler readiness invalid');}
    if(ready){if(ready.schemaVersion!==1||ready.ready!==true)throw Error('Container sampler unavailable');return;}
    await delay(25);
  }
  throw Error('Container sampler readiness timed out');
}
function apiOptions(dockerHost,apiVersion){
  if(!dockerHost?.startsWith('unix:///')||!/^1\.\d+$/.test(apiVersion??''))throw Error('Local Unix Docker API required');
  return {socketPath:dockerHost.slice(7),prefix:'/v'+apiVersion};
}
async function apiJson(connection,url,signal){
  return new Promise((resolve,reject)=>{
    const request=http.get({socketPath:connection.socketPath,path:connection.prefix+url,signal},response=>{
      if(response.statusCode!==200){response.resume();reject(Error('Docker API response unavailable'));return;}
      response.setEncoding('utf8');let body='';
      response.on('data',chunk=>{body+=chunk;if(body.length>2*1024*1024)request.destroy(Error('Docker API response too large'));});
      response.on('end',()=>{try{resolve(JSON.parse(body));}catch{reject(Error('Docker API response invalid'));}});
      response.on('error',()=>reject(Error('Docker API read failed')));
    });
    request.setTimeout(5000,()=>request.destroy(Error('Docker API timeout')));request.on('error',()=>reject(Error('Docker API connection failed')));
  });
}
function subscribeStats(connection,id,entry,signal,faults,privateFrameFile,pendingWrites){
  const streamError=(kind,error)=>{(entry.streamErrors??=[]).push({kind,time:new Date().toISOString(),code:/^[A-Z0-9_]{1,64}$/.test(error?.code??'')?error.code:null});};
  let firstResolve,firstReject;
  const first=new Promise((resolve,reject)=>{firstResolve=resolve;firstReject=reject;});
  const request=http.get({socketPath:connection.socketPath,path:connection.prefix+`/containers/${id}/stats?stream=true`,signal},response=>{
    if(response.statusCode!==200){response.resume();firstReject(Error('Container stats stream unavailable'));return;}
    response.setEncoding('utf8');let pending='';
    response.on('data',chunk=>{
      if(signal.aborted)return;
      let currentFrame;
      try{
        pending+=chunk;if(pending.length>2*1024*1024)throw Error('Container stats frame too large');
        let end;
        while((end=pending.indexOf('\n'))>=0){
          const line=pending.slice(0,end).trim();pending=pending.slice(end+1);if(!line)continue;
          currentFrame=JSON.parse(line);const sample=parseContainerStats(currentFrame,id);
          if(entry.samples.length>=100000)throw Error('Container stats sample limit exceeded');
          entry.samples.push(sample);firstResolve();
        }
      }catch{
        const event={time:new Date().toISOString(),kind:'invalid-stats-stream',frameTime:Number.isFinite(Date.parse(currentFrame?.read))?currentFrame.read:null};
        if(!entry.frameFailures?.length){const save=fs.writeFile(privateFrameFile,JSON.stringify({event,frame:currentFrame??null},null,2)+'\n',{mode:0o600}).then(()=>fs.chmod(privateFrameFile,0o600));save.catch(()=>{});pendingWrites.push(save);}
        (entry.frameFailures??=[]).push(event);faults.push('invalid-stats-stream');firstReject(Error('Container stats stream invalid'));request.destroy();
      }
    });
    response.on('end',()=>{entry.streamEndedAt=new Date().toISOString();if(!entry.samples.length)firstReject(Error('Container stats stream empty'));});
    response.on('error',error=>{if(!signal.aborted){streamError('stats-stream-read-failed',error);firstReject(Error('Container stats read failed'));}});
  });
  request.setTimeout(5000,()=>request.destroy(Error('Container stats timeout')));
  request.on('error',error=>{if(!signal.aborted){streamError('stats-stream-connect-failed',error);firstReject(Error('Container stats connection failed'));}});
  signal.addEventListener('abort',()=>firstReject(Error('Container stats stopped')),{once:true});
  return first;
}
export async function runWithContainerResources({manifest,dockerHost,apiVersion,output,key,resultFile,holdClientUntilSampled=false},operation){
  if(!validateOwner(manifest,{'com.docker.compose.project':manifest.project,'com.webapi.performance.owner':manifest.owner})||!/^[a-zA-Z0-9:-]+$/.test(key??'')||!resultFile||path.basename(resultFile)!==resultFile)throw Error('Invalid container resource operation');
  const connection=apiOptions(dockerHost,apiVersion),controller=new AbortController(),services={},faults=[];
  const readyFile=path.join(output,key+'.resources-ready.json'),reportFile=path.join(output,key+'.resources.json'),pendingWrites=[];
  const finishedFile=path.join(output,key+'.resources-finished.json');
  await fs.rm(readyFile,{force:true});await fs.rm(finishedFile,{force:true});
  if(holdClientUntilSampled)await fs.rm(path.join(output,resultFile),{force:true});
  // The workload blocks on readiness before opening its measured window. It can
  // warm up while the engine streams are established, without host exec polling.
  const startupTimeoutMs=60000;
  const startupInventory={startedAt:new Date().toISOString(),timeoutMs:startupTimeoutMs,polls:0,lastSeenAt:null,lastRunningServices:[]};let actionFailure,actionSettled=false;
  const action=Promise.resolve().then(operation);action.then(()=>{actionSettled=true;},error=>{actionSettled=true;actionFailure={observedAt:new Date().toISOString(),exitCode:Number.isInteger(error.code)?error.code:null,signal:/^SIG[A-Z0-9]+$/.test(error.signal??'')?error.signal:null};});
  let operationError;
  try{
    const filters=encodeURIComponent(JSON.stringify({label:[`com.docker.compose.project=${manifest.project}`,`com.webapi.performance.owner=${manifest.owner}`]}));
    // One-off container creation is outside the measured window. Wait for the
    // actual owned inventory; a failed workload cannot become ready by waiting.
    const deadline=Date.now()+startupTimeoutMs;let containers;
    while(Date.now()<deadline){
      const listed=await apiJson(connection,'/containers/json?all=false&filters='+filters,controller.signal);
      if(!Array.isArray(listed))throw Error('Docker container inventory invalid');
      const found=new Map();
      for(const container of listed){
        const labels=container.Labels,service=labels?.['com.docker.compose.service'];
        if(!validateOwner(manifest,labels))throw Error('Container resource ownership mismatch');
        if(!quotas[service]||found.has(service)||!/^[a-f0-9]{64}$/.test(container.Id??''))throw Error('Container resource inventory invalid');
        found.set(service,container);
      }
      startupInventory.polls++;startupInventory.lastSeenAt=new Date().toISOString();startupInventory.lastRunningServices=[...found.keys()].sort();
      if(found.size===Object.keys(quotas).length){containers=found;break;}
      if(actionSettled)break;
      await delay(50);
    }
    if(!containers)throw Error('Running container resource services missing');
    const firstFrames=[];
    for(const [service,container] of containers){
      const info=await apiJson(connection,`/containers/${container.Id}/json`,controller.signal),quota=quotas[service];
      if(info.Id!==container.Id||!validateOwner(manifest,info.Config?.Labels)||info.Config.Labels['com.docker.compose.service']!==service)throw Error('Container resource ownership mismatch');
      if(info.State?.Running!==true||info.HostConfig?.NanoCpus!==quota.cpus*1e9||info.HostConfig?.Memory!==quota.memoryBytes||!/^sha256:[a-f0-9]{64}$/.test(info.Image??''))throw Error('Actual running container resource quota or image mismatch');
      const entry={containerId:container.Id,imageId:info.Image,cpus:quota.cpus,memoryBytes:quota.memoryBytes,samples:[]};services[service]=entry;
      // Start each stream immediately; its rejection is handled while remaining
      // container identities are inspected, so no rejection escapes the owner.
      const first=subscribeStats(connection,container.Id,entry,controller.signal,faults,path.join(manifest.directory,key+'.'+service+'.invalid-frame.json'),pendingWrites);first.catch(()=>{});firstFrames.push(first);
    }
    await Promise.all(firstFrames);
    await writeJson(readyFile,{schemaVersion:1,ready:true,services:Object.keys(services).sort()});
    if(holdClientUntilSampled){
      // The client has written its measured window but stays alive until every
      // stats subscription is stopped. Docker emits empty stats on client exit.
      while(!actionSettled){
        let measured;
        try{measured=JSON.parse(await fs.readFile(path.join(output,resultFile),'utf8'));}
        catch(error){if(error.code!=='ENOENT'&&!(error instanceof SyntaxError))throw error;}
        if(Number.isFinite(Date.parse(measured?.windowStartedAt))&&Number.isFinite(Date.parse(measured?.windowEndedAt)))break;
        await delay(25);
      }
    }else await action;
  }catch(error){operationError=error;faults.push('resource-operation-failed');await writeJson(readyFile,{schemaVersion:1,ready:false});}
  finally{
    controller.abort();
    if(holdClientUntilSampled)await writeJson(finishedFile,{schemaVersion:1,ready:true});
  }
  try{await action;}catch(error){operationError??=error;faults.push('resource-operation-failed');}
  if((await Promise.allSettled(pendingWrites)).some(r=>r.status==='rejected'))faults.push('invalid-frame-retention-failed');
  let measurement;
  try{measurement=JSON.parse(await fs.readFile(path.join(output,resultFile),'utf8'));}catch{faults.push('measurement-window-unavailable');}
  for(const [service,entry] of Object.entries(services)){
    entry.summary=summarizeContainerWindow(entry.samples,{start:measurement?.windowStartedAt,end:measurement?.windowEndedAt,cpus:entry.cpus,memoryBytes:entry.memoryBytes});
    if(entry.streamEndedAt&&Date.parse(entry.streamEndedAt)<Date.parse(measurement?.windowEndedAt)){faults.push(service==='client'?'client-stream-ended-early':'server-stream-ended-early');entry.summary.complete=false;}
    for(const event of entry.streamErrors??[]){
      const completedClient=service==='client'&&!operationError&&measurement?.complete===true&&entry.summary.complete&&Date.parse(event.time)>=Date.parse(measurement.windowEndedAt);
      event.disposition=completedClient?'after-completed-client-window':'sampling-failure';if(!completedClient)faults.push(event.kind);
    }
  }
  const report={schemaVersion:1,startupInventory,workloadFailure:actionFailure??null,workloadCompleted:!operationError&&measurement?.complete===true,source:'docker-engine-stats-stream',apiVersion,windowStartedAt:measurement?.windowStartedAt??null,windowEndedAt:measurement?.windowEndedAt??null,services,errors:[...new Set(faults)],complete:!faults.length&&Object.keys(services).length===7&&Object.values(services).every(s=>s.summary.complete),potentialClientOrBackendSaturation:['client','backend'].filter(s=>(services[s]?.summary.peakCpuQuotaFraction??0)>=.9),saturationThreshold:.9};
  await writeJson(reportFile,report);
  if(operationError)throw operationError;
  return report;
}

export function verifyContainerResources(report,{start,end}={}){
  try{
    if(report?.schemaVersion!==1||report.source!=='docker-engine-stats-stream'||!/^1\.\d+$/.test(report.apiVersion??'')||report.complete!==true||!Array.isArray(report.errors)||report.errors.length||report.windowStartedAt!==start||report.windowEndedAt!==end||report.saturationThreshold!==.9||!Array.isArray(report.potentialClientOrBackendSaturation))return false;
    const services=report.services??{};
    if(Object.keys(services).length!==Object.keys(quotas).length)return false;
    for(const [name,quota] of Object.entries(quotas)){
      const entry=services[name];
      if(!entry||!/^[a-f0-9]{64}$/.test(entry.containerId??'')||!/^sha256:[a-f0-9]{64}$/.test(entry.imageId??'')||entry.cpus!==quota.cpus||entry.memoryBytes!==quota.memoryBytes)return false;
      if(entry.frameFailures!==undefined&&(!Array.isArray(entry.frameFailures)||entry.frameFailures.length))return false;
      const calculated=summarizeContainerWindow(entry.samples,{start,end,...quota});
      if(!calculated.complete||JSON.stringify(calculated)!==JSON.stringify(entry.summary))return false;
      if(entry.streamEndedAt&&Date.parse(entry.streamEndedAt)<Date.parse(end))return false;
      if(entry.streamErrors!==undefined&&(!Array.isArray(entry.streamErrors)||entry.streamErrors.some(e=>name!=='client'||report.workloadCompleted!==true||!['stats-stream-read-failed','stats-stream-connect-failed'].includes(e.kind)||!Number.isFinite(Date.parse(e.time))||Date.parse(e.time)<Date.parse(end)||e.code!==null&&!/^[A-Z0-9_]{1,64}$/.test(e.code??'')||e.disposition!=='after-completed-client-window')))return false;
    }
    const nearQuota=['client','backend'].filter(name=>services[name].summary.peakCpuQuotaFraction>=.9);
    return JSON.stringify(nearQuota)===JSON.stringify(report.potentialClientOrBackendSaturation);
  }catch{return false;}
}


export function verifyResourceDiagnosticCompletion({cells=[],auth='api-key',cleanup,secretScan}={}){
  if(!['anonymous','api-key','missing-key','rs256','es256','wrong-secret','no-mapping','no-grant'].includes(auth)||cells.length!==3||cleanup?.remaining!==0||cleanup?.privateInputsDeleted!==true||secretScan?.violations!==0||!(secretScan.filesScanned>0)||!Array.isArray(secretScan.findings)||secretScan.findings.length)return false;
  let previousEnd=-Infinity;
  return cells.every((c,index)=>{
    const start=Date.parse(c.windowStartedAt),end=Date.parse(c.windowEndedAt);
    const valid=c.variant==='validation'&&c.scale===10000&&c.auth===auth&&c.concurrency===32&&c.repeat===index+1&&c.nodeMode==='dual'&&c.complete===true&&c.expected>0&&c.unexpected===0&&c.timeout===0&&c.incomplete===0&&c.warmupMs===10000&&c.durationMs===20000&&end-start===20000&&start>=previousEnd&&Array.isArray(c.nodes)&&c.nodes.length===2&&c.nodes.every(n=>n.expected>0&&n.unexpected===0&&n.timeout===0&&n.incomplete===0)&&c.nodes.reduce((total,n)=>total+n.expected,0)===c.expected&&verifyContainerResources(c.containerResources,{start:c.windowStartedAt,end:c.windowEndedAt});
    previousEnd=end;return valid;
  });
}
