import fs from 'node:fs';import assert from 'node:assert/strict';
import {scenario,sleep,wait} from './observability-scenario.mjs';
const s=await scenario(),checks=[];
const total=rows=>rows.reduce((n,r)=>n+Number(r.value[1]),0);
const matched=metric=>metric+'{webapi_environment_id="'+s.context.environmentId+'"}';
for(const [service,signal,suffix,path]of [['loki','logs','log_records','logs'],['tempo','traces','spans','traces']]){
 const started=Date.now(),gatewayBefore=total(await s.raw(matched('webapi_telemetry_export_failures_total')));
 try{
  s.compose('stop',service);
  // Exceeds Collector's 30s retry budget while successful Gateway OTLP continues.
  const until=Date.now()+42_000;while(Date.now()<until){assert.equal((await s.request(0)).status,200);assert.equal((await s.request(1)).status,200);await sleep(500);}
  const failed=await wait(()=>s.raw('otelcol_exporter_send_failed_'+suffix+'{job="webapi-collector-diagnostics"}'),r=>total(r)>0,15);
  const end=Date.now()-3000,range={...s.range(60,end)};
  s.compose('start',service);await sleep(10_000);
  const result=await wait(()=>s.query(path,range),r=>r.sourceState==='Partial',45);
  assert.equal(result.coverage.complete,false);assert.equal(result.coverage.reason,'collector_'+signal+'_collection_gap');
  const gatewayAfter=total(await s.raw(matched('webapi_telemetry_export_failures_total')));assert.equal(gatewayAfter,gatewayBefore);
  const up=total(await s.raw('up{job="webapi-collector-diagnostics"}'));assert.equal(up,1);
  checks.push({signal,startedAt:new Date(started).toISOString(),range,downstreamFailures:total(failed),gatewayExportFailureDelta:gatewayAfter-gatewayBefore,collectorUp:up,sourceState:result.sourceState,reason:result.coverage.reason});
 }finally{s.compose('start',service);}
}
// A short receiver outage must not hide behind the 45s heartbeat tolerance.
const lossStart=Date.now();try{
 s.compose('stop','collector');const until=Date.now()+8000;while(Date.now()<until){assert.equal((await s.request(0)).status,200);await sleep(100);}
}finally{s.compose('start','collector');}
await wait(()=>s.raw(matched('webapi_telemetry_last_loss_timestamp_seconds')),r=>r.some(x=>x.metric.signal==='logs'&&Number(x.value[1])*1000>=lossStart),30);
await sleep(5000);const range=s.range(60,Date.now()-3000),result=await s.query('logs',range);
assert.equal(result.coverage.complete,false);assert.equal(result.coverage.reason,'known_logs_collection_gap');
checks.push({signal:'logs',shortCollectorStopSeconds:8,range,sourceState:result.sourceState,reason:result.coverage.reason});
fs.writeFileSync('docs/evidence/observability/final-loss-live.json',JSON.stringify({complete:true,project:s.project,classification:'real-gateway-collector-downstream-loss',checks,cleanup:null},null,2));
console.log('Real downstream expiry and first short-loss coverage passed.');
