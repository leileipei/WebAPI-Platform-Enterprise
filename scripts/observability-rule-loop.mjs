import fs from 'node:fs';import assert from 'node:assert/strict';import {scenario,wait,sleep} from './observability-scenario.mjs';
export async function ruleLoop(s){
 const checks=[],started=Date.now();let pumping=true,mode='failure',pumpError;
 const pump=(async()=>{let node=0;while(pumping){try{for(const path of mode==='failure'?['/orders','/orders/__test/slow','/orders/__test/5xx']:['/orders']){const r=await s.request(node,path);assert.equal(r.status,path.endsWith('5xx')?503:200);}node=1-node;}catch(e){pumpError=e;}await sleep(300);}})();
 try{
  // A real 60s bounded window, not a substituted protocol fixture. Domain tests retain 300/600/120s.
  await sleep(62_000);assert.equal(pumpError,undefined);
  const metrics=await s.query('metrics',{groupBy:'Api',sortBy:'latency_p95_ms'});assert.equal(metrics.sourceState,'Available');assert.equal(metrics.coverage.complete,true);assert.equal(s.metric(metrics,'error_5xx_ratio').state,'Available');assert(Math.abs(s.metric(metrics,'error_5xx_ratio').value-1/3)<0.12,'60s estimated 5xx ratio must match actual balanced traffic within 0.12');assert(s.metric(metrics,'latency_p95_ms').value>1000);
  for(const key of ['request_rps','success_ratio','error_4xx_ratio','error_5xx_ratio','latency_p50_ms','latency_p95_ms','latency_p99_ms'])assert(metrics.data.trends[key].some(p=>p.value!==null));
  checks.push({name:'Actual60SecondCompleteCoverageAndEstimatedRates',status:'passed',ratio:s.metric(metrics,'error_5xx_ratio').value,ratioAbsoluteTolerance:0.12,p95:s.metric(metrics,'latency_p95_ms').value,samples:s.metric(metrics,'request_count').sampleCount});
  const rules=[];for(const [name,metric,threshold,originalForSeconds]of [['错误率持续告警','error_5xx_ratio',0.05,300],['慢调用持续告警','latency_p95_ms',1000,600],['后端健康持续告警','unhealthy_destinations',0,120]]){const rule=await s.createRule(name+' · '+Date.now().toString(36),metric,threshold);rules.push({...rule,originalForSeconds});}
  let error=await wait(()=>s.eventFor(rules[0]),e=>e?.status==='Open'&&e.evaluationState==='Known');let slow=await wait(()=>s.eventFor(rules[1]),e=>e?.status==='Open'&&e.evaluationState==='Known');assert(Date.parse(error.startedAt)-Date.parse(error.conditionStartedAt)>=4000);
  error=await s.action(error,'Ack');assert.equal(error.status,'Ack');slow=await s.action(slow,'Silence','隔离闭环维护');assert.equal(slow.status,'Silenced');assert(Date.parse(slow.silencedUntil)-Date.parse(slow.transitions.at(-1).occurredAt)>=899000);
  // Real active YARP probes observe a failing backend health endpoint.
  await s.backend('/__test/health/unhealthy');
  const unhealthy=await wait(()=>s.eventFor(rules[2]),e=>e?.status==='Open'&&e.evaluationState==='Known');assert.equal(unhealthy.lastValue,1);
  await s.backend('/__test/health/healthy');mode='healthy';pumpError=undefined;
  const recovered=[];for(const rule of rules){const e=await wait(()=>s.eventFor(rule),e=>e?.status==='Resolved'&&e.resolveReason==='Recovered',150);assert(e.transitions.some(t=>t.reason==='Recovered'));assert(Date.parse(e.startedAt)-Date.parse(e.conditionStartedAt)>=4000);const audit=await s.admin.request('/audit-logs?'+new URLSearchParams({resourceId:e.id,pageSize:100}));assert(audit.items.length>=2,'Real trigger and recovery audit facts required');recovered.push({eventId:e.id,ruleId:rule.id,definition:rule.definition,originalForSeconds:rule.originalForSeconds,e2eForSeconds:4,conditionStartedAt:e.conditionStartedAt,startedAt:e.startedAt,resolvedAt:e.resolvedAt,transitions:e.transitions.map(t=>({toStatus:t.toStatus,reason:t.reason})),auditCount:audit.items.length});}
  checks.push({name:'ThreeActualMetricRulesTriggerAckSilenceRecoverAndAudit',status:'passed',rules:recovered});
  const current=await s.query('metrics');assert.equal(s.metric(current,'unhealthy_destinations').value,0);checks.push({name:'ActualYarpHealthyGaugeRecovers',status:'passed',value:0});
  for(const rule of rules)await s.admin.request('/observability/alert-rules/'+rule.id+'/disable',{method:'POST',headers:{'If-Match':`"${rule.revision}"`}});
  fs.writeFileSync(s.directory+'/rules.json',JSON.stringify(rules.map(r=>({id:r.id,definition:r.definition})),null,2));
  const evidence={complete:true,project:s.project,classification:'actual-business-gateway-three-source-and-independent-workers',settings:s.settings,windowSeconds:60,elapsedSeconds:Math.round((Date.now()-started)/1000),checks};fs.writeFileSync('docs/evidence/observability/rule-loop.json',JSON.stringify(evidence,null,2));console.log('Real continuous-rule and recovery checks passed.');return checks;
 }catch(e){fs.writeFileSync('docs/evidence/observability/rule-loop.json',JSON.stringify({complete:false,project:s.project,settings:s.settings,checks,failure:e.message},null,2));throw e;}finally{pumping=false;await pump;try{await s.backend('/__test/health/healthy');}catch{}}
}
if(process.argv[1]?.endsWith('observability-rule-loop.mjs'))await ruleLoop(await scenario());
