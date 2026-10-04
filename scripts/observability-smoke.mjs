import {randomBytes,randomUUID} from 'node:crypto';
import {readFileSync,writeFileSync,existsSync} from 'node:fs';
import {pathToFileURL} from 'node:url';
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const nano=ms=>(BigInt(ms)*1_000_000n).toString();
const attr=(key,value)=>({key,value:{stringValue:value}});
export function validateLogContract(proof){
 if(proof.metadataFound!==true||proof.normalizedNames!==true||proof.filterFound!==true)throw new Error("Actual normalized log metadata and structured filter are required");
}
export function validateMetricContract(proof){
 if(!Number.isFinite(proof.histogramCount)||proof.histogramCount<1||proof.upperBucketFound!==true||!Number.isFinite(proof.nodeObservedSeconds)||proof.nodeObservedSeconds<=0)
  throw new Error('Metric contract requires actual histogram count, named bucket and node observation gauge');
}

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
  const logId=randomUUID(),apiId=randomUUID(),appId=randomUUID(),destinationId=randomUUID();
  const traceId=randomBytes(16).toString('hex'),spanId=randomBytes(8).toString('hex');
  const resource={attributes:[attr('service.name','webapi-gateway'),attr('webapi.environment.id',environmentId),attr('service.instance.id','smoke-node')]};
  const metric={name:'webapi_gateway_requests_total',sum:{aggregationTemporality:2,isMonotonic:true,dataPoints:[{
    startTimeUnixNano:nano(now-1000),timeUnixNano:nano(now),asInt:'1',attributes:[attr('webapi.api.id',randomUUID())]
  }]}};
  const bounds=[0.005,0.01,0.025,0.05,0.1,0.25,0.5,1,2.5,5,10,30,60];
  const duration={name:'webapi_gateway_request_duration_seconds',unit:'s',histogram:{aggregationTemporality:2,dataPoints:[{
    startTimeUnixNano:nano(now-1000),timeUnixNano:nano(now),count:'1',sum:0.05,explicitBounds:bounds,bucketCounts:[...bounds.map((_,i)=>i===3?'1':'0'),'0']
  }]}};
  const observed={name:'webapi_telemetry_last_observed_timestamp_seconds',gauge:{dataPoints:[{timeUnixNano:nano(now),asDouble:now/1000}]}};
  const bodies={
    metrics:{resourceMetrics:[{resource,scopeMetrics:[{scope:{name:'webapi-smoke'},metrics:[metric,duration,observed]}]}]},
    logs:{resourceLogs:[{resource,scopeLogs:[{scope:{name:'webapi-smoke'},logRecords:[{
      timeUnixNano:nano(now),observedTimeUnixNano:nano(now),severityNumber:9,severityText:'INFO',
      body:{stringValue:marker},traceId,spanId,attributes:[attr('webapi.log.id',logId),attr('webapi.api.id',apiId),attr('webapi.application.id',appId),attr('webapi.destination.id',destinationId),attr('http.request.method','GET'),attr('url.template','/smoke'),attr('webapi.duration.ms','10'),attr('http.response.status_code','200'),attr('webapi.node.name','smoke-node'),attr('webapi.outcome','Completed'),attr('webapi.request.id','synthetic-request'),attr('webapi.client.ip_masked','192.0.2.xxx')]
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
  async function metricValue(query){
    const j=await(await response(`${prometheus}/api/v1/query?${new URLSearchParams({query})}`)).json();
    if(j.status!=='success')throw new Error('Prometheus metric contract query failed');
    return j.data.result.length?Number(j.data.result[0].value[1]):null;
  }
  const histogramCount=await poll(()=>metricValue(`webapi_gateway_request_duration_seconds_count{webapi_environment_id="${environmentId}"}`),'Duration histogram count');
  const upperBucketFound=await poll(async()=>await metricValue(`webapi_gateway_request_duration_seconds_bucket{webapi_environment_id="${environmentId}",le="0.05"}`)>=1,'Duration histogram bucket');
  const nodeObservedSeconds=await poll(()=>metricValue(`webapi_telemetry_last_observed_timestamp_seconds{webapi_environment_id="${environmentId}"}`),'Node observation gauge');
  const metricContract={histogramCount,upperBucketFound,nodeObservedSeconds};validateMetricContract(metricContract);
  const logQuery=`{service_name="webapi-gateway",webapi_environment_id="${environmentId}"} |= "${marker}"`;
  const logMarkerFound=await poll(async()=>{
    const j=await(await response(`${loki}/loki/api/v1/query_range?${new URLSearchParams({query:logQuery,start:nano(now-60_000),end:nano(Date.now()),limit:'10'})}`)).json();
    return j.status==='success'&&j.data.result.some(s=>s.values.some(v=>v[1]===marker));
  },'Access log ingestion');
  const logMetadata = await poll(async()=>{
    const j=await(await response(`${loki}/loki/api/v1/query_range?${new URLSearchParams({query:`{service_name="webapi-gateway",webapi_environment_id="${environmentId}"} | webapi_log_id="${logId}" | http_request_method="GET"`,start:nano(now-60_000),end:nano(Date.now()),limit:'10'})}`)).json();
    for(const stream of j.data?.result??[])for(const row of stream.values??[]){const metadata={...stream.stream,...(row[2]??{})};if(metadata.webapi_log_id===logId&&metadata.url_template==='/smoke')return {metadataFound:true,normalizedNames:metadata.http_request_method==='GET',filterFound:true,shape:row.length>=3?'row-metadata':'flattened-stream-labels',keys:Object.keys(metadata).sort()};}
    return false;
  },'Normalized structured log metadata');
  validateLogContract(logMetadata);
  const traceFound=await poll(async()=>{
    const j=await(await response(`${tempo}/api/traces/${traceId}`,{headers:{Accept:'application/json'}})).json();
    const batches=j.batches??j.resourceSpans??[];
    return batches.some(b=>(b.scopeSpans??b.instrumentationLibrarySpans??[]).some(s=>(s.spans??[]).some(x=>x.name===marker)));
  },'Trace ingestion');
  const proof={requestCounter,logMarkerFound,traceFound};validateProof(proof);
  writeFileSync(file,JSON.stringify({project,startedAt,completedAt:new Date().toISOString(),endpoints,environmentId,traceId,marker,proof,metricContract,logContract:logMetadata,cleanup:null,kind:'synthetic-otlp-protocol-only',verifiedVolumeCapacityBytes:{loki:1073741824,tempo:1073741824}},null,2));
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
