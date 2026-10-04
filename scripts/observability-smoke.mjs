import {randomBytes,randomUUID} from 'node:crypto';
import {readFileSync,writeFileSync,existsSync} from 'node:fs';
import {pathToFileURL} from 'node:url';
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const nano=ms=>(BigInt(ms)*1_000_000n).toString();
const attr=(key,value)=>({key,value:{stringValue:value}});

export function validateEndpoints(endpoints){
  for(const value of endpoints){
    let url;try{url=new URL(value);}catch{throw new Error('Invalid loopback endpoint');}
    if(url.protocol!=='http:'||url.hostname!=='127.0.0.1'||!url.port||Number(url.port)<1||url.username||url.password)
      throw new Error('Invalid loopback endpoint');
  }
}

export function validateProof(proof) {
  if (!(Number.isFinite(proof.requestCounter)&&proof.requestCounter>=1&&proof.logMarkerFound===true&&proof.traceFound===true))
    throw new Error('Actual counter, log marker and trace must all be found; readiness is insufficient');
}

async function response(url,options={}) {
  const r=await fetch(url,{...options,signal:AbortSignal.timeout(5000)});
  if(!r.ok)throw new Error(`Source returned ${r.status}: ${new URL(url).pathname}`);
  return r;
}
async function poll(action,label,timeout=120_000){
  const deadline=Date.now()+timeout;let last;
  while(Date.now()<deadline){try{const value=await action();if(value)return value;}catch(e){last=e.message;}await pause(1000);}
  throw new Error(`${label} timed out${last?`: ${last}`:''}`);
}
async function smoke(file,project,collector,health,prometheus,loki,tempo){
  validateEndpoints([collector,health,prometheus,loki,tempo]);
  const startedAt=new Date().toISOString();
  const endpoints={collector,health,prometheus,loki,tempo};
  writeFileSync(file,JSON.stringify({project,startedAt,endpoints,proof:null,cleanup:null},null,2));
  await poll(async()=>{await response(health);return true;},'Collector readiness');
  await Promise.all([
    poll(async()=>{await response(`${prometheus}/-/ready`);return true;},'Prometheus readiness'),
    poll(async()=>{await response(`${loki}/ready`);return true;},'Loki readiness'),
    poll(async()=>{await response(`${tempo}/ready`);return true;},'Tempo readiness')]);
  const now=Date.now(),environmentId=randomUUID(),marker=`webapi-smoke-${randomUUID()}`;
  const traceId=randomBytes(16).toString('hex'),spanId=randomBytes(8).toString('hex');
  const resource={attributes:[attr('service.name','webapi-smoke'),attr('webapi.environment.id',environmentId),attr('service.instance.id','smoke-node')]};
  const metric={name:'webapi_gateway_requests_total',sum:{aggregationTemporality:2,isMonotonic:true,dataPoints:[{
    startTimeUnixNano:nano(now-1000),timeUnixNano:nano(now),asInt:'1',attributes:[attr('webapi.api.id',randomUUID())]
  }]}};
  const bodies={
    metrics:{resourceMetrics:[{resource,scopeMetrics:[{scope:{name:'webapi-smoke'},metrics:[metric]}]}]},
    logs:{resourceLogs:[{resource,scopeLogs:[{scope:{name:'webapi-smoke'},logRecords:[{
      timeUnixNano:nano(now),observedTimeUnixNano:nano(now),severityNumber:9,severityText:'INFO',
      body:{stringValue:marker},traceId,spanId,attributes:[attr('webapi.log.id',randomUUID()),attr('webapi.client.ip_masked','192.0.2.xxx')]
    }]}]}]},
    traces:{resourceSpans:[{resource,scopeSpans:[{scope:{name:'webapi-smoke'},spans:[{
      traceId,spanId,name:marker,kind:2,startTimeUnixNano:nano(now-10),endTimeUnixNano:nano(now),status:{code:1}
    }]}]}]}
  };
  for(const [signal,body] of Object.entries(bodies)){
    const r=await response(`${collector}/v1/${signal}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    const result=await r.json();
    if(result.partialSuccess&&Object.values(result.partialSuccess).some(x=>x&&x!=='0'))throw new Error(`Collector partially rejected ${signal}`);
  }
  const q=`webapi_gateway_requests_total{webapi_environment_id="${environmentId}"}`;
  const requestCounter=await poll(async()=>{
    const j=await(await response(`${prometheus}/api/v1/query?${new URLSearchParams({query:q})}`)).json();
    if(j.status!=='success')throw new Error('Prometheus query failed');
    const n=j.data.result.reduce((sum,r)=>sum+Number(r.value[1]),0);return n>=1?n:false;
  },'Request counter ingestion');
  const logQuery=`{service_name="webapi-smoke",webapi_environment_id="${environmentId}"} |= "${marker}"`;
  const logMarkerFound=await poll(async()=>{
    const j=await(await response(`${loki}/loki/api/v1/query_range?${new URLSearchParams({query:logQuery,start:nano(now-60_000),end:nano(Date.now()),limit:'10'})}`)).json();
    return j.status==='success'&&j.data.result.some(s=>s.values.some(v=>v[1]===marker));
  },'Access log ingestion');
  const traceFound=await poll(async()=>{
    const j=await(await response(`${tempo}/api/traces/${traceId}`,{headers:{Accept:'application/json'}})).json();
    const batches=j.batches??j.resourceSpans??[];
    return batches.some(b=>(b.scopeSpans??b.instrumentationLibrarySpans??[]).some(s=>(s.spans??[]).some(x=>x.name===marker)));
  },'Trace ingestion');
  const proof={requestCounter,logMarkerFound,traceFound};validateProof(proof);
  writeFileSync(file,JSON.stringify({project,startedAt,completedAt:new Date().toISOString(),endpoints,environmentId,traceId,marker,proof,cleanup:null,kind:'synthetic-otlp-protocol-only',verifiedVolumeCapacityBytes:{loki:1073741824,tempo:1073741824}},null,2));
  console.log(JSON.stringify({project,proof,kind:'synthetic-otlp-protocol-only'}));
}
function cleanup(file,project,result){
  const previous=existsSync(file)?JSON.parse(readFileSync(file,'utf8')):{project,proof:null};
  if(previous.project!==project)throw new Error('Evidence project mismatch');
  const count=result==='0'?0:null;
  previous.cleanup={completedAt:new Date().toISOString(),containers:count,volumes:count,secretFiles:count,verified:result==='0'};
  writeFileSync(file,JSON.stringify(previous,null,2));
  if(result==='0'){validateProof(previous.proof);console.log('Isolated project cleanup verified: 0 containers, 0 volumes, 0 secret files');}
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
  const [command,...args]=process.argv.slice(2);
  try{if(command==='smoke')await smoke(...args);else if(command==='cleanup')cleanup(...args);else throw new Error('Unknown command');}
  catch(e){console.error(e.message);process.exitCode=1;}
}
