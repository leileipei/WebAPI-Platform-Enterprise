import test from 'node:test';
import assert from 'node:assert/strict';
import {collectExplain,verifyExplainCompletion} from './explain.mjs';
import {parseArguments} from './sources.mjs';
const hash='a'.repeat(64),other='b'.repeat(64),id='c'.repeat(32);
const plan=()=>({planningMs:0.2,executionMs:1.1,plan:{nodeType:'Index Scan',relation:'organizations',index:'IX_organizations_code',metrics:{'Actual Rows':1,'Actual Loops':1,'Shared Hit Blocks':2},children:[]}});
const sql={stats:{total:3},shapes:[{hash,kind:'reader',count:3,classification:'data'}]};
test('explain_completion_requires_every_scene_and_actual_cleanup_receipt',()=>{
  const scene=count=>({scale:100,complete:true,samples:Array.from({length:count},()=>({explain:{complete:true}}))});
  const input={scales:[100],ordinary:[scene(156)],scope:[scene(150)],reference:[scene(24)],cleanup:{remaining:0,resources:[{kind:'container'}],privateInputsDeleted:true},secretScan:{violations:0}};
  assert.equal(verifyExplainCompletion(input),true);
  for(const change of [i=>{i.cleanup.remaining=1;},i=>{i.cleanup.privateInputsDeleted=false;},i=>{i.secretScan.violations=1;},i=>{i.ordinary[0].samples.pop();},i=>{i.scope[0].samples[0].explain.complete=false;},i=>{i.reference=[];},i=>{i.scales=[100,10000];}]){const broken=structuredClone(input);change(broken);assert.equal(verifyExplainCompletion(broken),false);}
});
test('diagnostic_validation_is_explicit_and_cannot_enable_explain_in_baseline',()=>{
  assert.equal(parseArguments(['validate','--output','/tmp/explain-unit','--diagnostic','explain']).diagnostic,'explain');
  assert.throws(()=>parseArguments(['baseline','--baseline','a'.repeat(40),'--harness','b'.repeat(40),'--output','/tmp/explain-unit','--diagnostic','explain']),/diagnostic/i);
  assert.throws(()=>parseArguments(['validate','--output','/tmp/explain-unit','--diagnostic','unknown']),/diagnostic/i);
});
test('explain_collector_keeps_real_plan_shape_association_and_repeated_counts',async()=>{
  const calls=[];const result=await collectExplain(async target=>{calls.push(target);return {ok:true,json:async()=>({explained:1,repeated:2,queries:[{hash,plan:plan()}]})};},id,sql);
  assert.deepEqual(calls,['/__perf/explain/'+id]);assert.equal(result.complete,true);assert.equal(result.explained,1);assert.equal(result.repeated,2);assert.equal(result.queries[0].hash,hash);assert.equal(result.skippedCommands,0);assert.equal(result.measurementScope,'explain-diagnostic-not-performance');
});
test('explain_supports_existing_ef_truncated_index_names',async()=>{
  const evidence=plan();evidence.plan.index='IX_alert_evaluation_states_rule_id_logic_revision_environment_~';
  const result=await collectExplain(async()=>({ok:true,json:async()=>({explained:1,repeated:2,queries:[{hash,plan:evidence}]})}),id,sql);
  assert.equal(result.complete,true);assert.equal(result.queries[0].plan.plan.index,evidence.plan.index);
});
test('explain_collector_fails_closed_on_untrusted_plan_fields_and_shape_counts',async()=>{
  const valid={explained:1,repeated:2,queries:[{hash,plan:plan()}]},changes=[
    p=>{p.queries[0].hash=other;},p=>{p.explained=2;},p=>{p.repeated=3;},p=>{p.queries[0].plan.plan.filter='private-secret-canary';},
    p=>{p.queries[0].plan.plan.nodeType='private-secret-canary';},p=>{p.queries[0].plan.plan.metrics['Actual Rows']=-1;},
    p=>{p.queries[0].plan.plan.metrics['Index Cond']='private-secret-canary';},p=>{p.queries.push(structuredClone(p.queries[0]));p.explained++;},
    p=>{p.queries[0].plan.executionMs=Infinity;},p=>{p.queries[0].plan.plan.children=new Array(513).fill(plan().plan);}
  ];
  for(const change of changes){const packet=structuredClone(valid);change(packet);await assert.rejects(()=>collectExplain(async()=>({ok:true,json:async()=>packet}),id,sql),/explain/i);}
  await assert.rejects(()=>collectExplain(async()=>({ok:false}),id,sql),/explain/i);
  const missingShape={stats:{total:4},shapes:[...sql.shapes,{hash:other,kind:'reader',count:1}]};
  await assert.rejects(()=>collectExplain(async()=>({ok:true,json:async()=>valid}),id,missingShape),/explain/i);
});
