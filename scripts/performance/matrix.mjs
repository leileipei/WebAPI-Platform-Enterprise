import {performance,monitorEventLoopDelay} from 'node:perf_hooks';
import {summarizeHistogram} from './report.mjs';
export function buildComparisonPairs(){
  return buildMatrix({variants:['baseline']}).map((cell,index)=>{
    const variants=(index+cell.repeat-1)%2===0?['baseline','candidate']:['candidate','baseline'];
    return {pairId:[cell.scale,cell.auth,cell.concurrency,cell.repeat,cell.nodeMode].join(':'),cells:variants.map(variant=>({...cell,variant}))};
  });
}
export function buildMatrix({variants=['baseline','candidate']}={}){
  const cells=[];
  for(const variant of variants)for(let repeat=1;repeat<=3;repeat++){
    for(const scale of [100,1000,10000])for(const auth of ['anonymous','api-key','missing-key','rs256'])for(const concurrency of [1,32,128])
      cells.push({variant,scale,auth,concurrency,repeat,nodeMode:'dual'});
    for(const auth of ['es256','wrong-secret','no-mapping','no-grant'])cells.push({variant,scale:10000,auth,concurrency:32,repeat,nodeMode:'dual'});
    for(const nodeMode of ['gateway-a','gateway-b'])for(const auth of ['anonymous','api-key','missing-key','rs256'])
      cells.push({variant,scale:10000,auth,concurrency:32,repeat,nodeMode});
  }
  return cells;
}
export async function measureCell({urls,requests,concurrency,warmupMs=10000,measureMs=20000,signal,requestTimeoutMs=5000,onMeasurementStart,onMeasurementEnd}){
  if(!urls?.length||!requests?.length||!Number.isInteger(concurrency)||concurrency<1||concurrency>128||measureMs<=0)throw Error('Invalid cell');
  const run=async duration=>{
    const startedUtc=Date.now();
    const start=performance.now(),end=start+duration,histogram=new Map(),statuses={},nodes=urls.map(()=>({expected:0,unexpected:0,timeout:0,incomplete:0}));
    const counts={expected:0,unexpected:0,timeout:0,incomplete:0};let cursor=0,pendingAtCutoff=0,drained=0,drainedUnexpected=0;
    const delays=monitorEventLoopDelay({resolution:10});delays.enable();const cpuStart=process.cpuUsage();
    let peakRssBytes=process.memoryUsage().rss;const sampler=setInterval(()=>{peakRssBytes=Math.max(peakRssBytes,process.memoryUsage().rss);},100);sampler.unref();
    await Promise.all(Array.from({length:concurrency},async(_,worker)=>{
      if(signal?.aborted){counts.incomplete++;nodes[worker%urls.length].incomplete++;return;}
      while(performance.now()<end&&!signal?.aborted){
        const dispatch=cursor++,request=requests[dispatch%requests.length],node=dispatch%urls.length,t0=performance.now();
        const timeoutSignal=AbortSignal.timeout(requestTimeoutMs);
        try{
          const response=await fetch(new URL(request.path,urls[node]),{headers:request.headers??{},signal:signal?AbortSignal.any([signal,timeoutSignal]):timeoutSignal,redirect:'manual'});
          await response.arrayBuffer();const t1=performance.now();
          if(t1>end){pendingAtCutoff++;drained++;if(response.status!==request.expected){drainedUnexpected++;counts.unexpected++;nodes[node].unexpected++;}continue;}
          statuses[response.status]=(statuses[response.status]??0)+1;
          const expected=response.status===request.expected;
          const classification=expected?'expected':'unexpected';counts[classification]++;nodes[node][classification]++;
          const upper=Math.ceil((t1-t0)*10)/10;histogram.set(upper,(histogram.get(upper)??0)+1);
        }catch{
          const classification=signal?.aborted?'incomplete':'timeout';counts[classification]++;nodes[node][classification]++;
          if(performance.now()>=end)pendingAtCutoff++;
        }
      }
    }));
    clearInterval(sampler);delays.disable();const cpu=process.cpuUsage(cpuStart),wallMs=performance.now()-start;
    const samples=[...histogram.entries()].sort((a,b)=>a[0]-b[0]);
    return {...summarizeHistogram(samples,counts,duration),histogram:samples,statuses,nodes,warmupMs,durationMs:duration,wallMs,pendingAtCutoff,drained,drainedUnexpected,
      windowStartedAt:new Date(startedUtc).toISOString(),windowEndedAt:new Date(startedUtc+duration).toISOString(),windowDrainedAt:new Date().toISOString(),
      client:{peakRssBytes,cpuUserUs:cpu.user,cpuSystemUs:cpu.system,cpuCores:(cpu.user+cpu.system)/(wallMs*1000),cpuMeasurementScope:'measurement-and-drain',eventLoopP95Ms:delays.percentile(95)/1e6,maxSchedulingDelayMs:delays.max/1e6},complete:!signal?.aborted};
  };
  if(warmupMs>0)await run(warmupMs);
  await onMeasurementStart?.();
  try {return await run(measureMs);}
  finally{await onMeasurementEnd?.();}
}
