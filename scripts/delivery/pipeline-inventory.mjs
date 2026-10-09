import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';import {deliverySql} from '../gateway-policies/delivery.mjs';
export const pipelineAddedDefaults={release_promotions:{gate_origin:'ProjectConnection',pipeline_run_stage_id:null,stage_attempt_id:null},release_verifications:{pipeline_run_stage_id:null,stage_attempt_id:null,profile_hash:null},release_test_acceptances:{pipeline_run_stage_id:null,stage_attempt_id:null,profile_hash:null},project_delivery_policies:{active_pipeline_version_id:null}};
// Existing maintenance rules: observed liveness and evaluation leases are dynamic;
// configuration and actual deployment version/sequence remain protected.
export const pipelineDynamicColumns={gateway_nodes:['instance_id','app_version','status','last_heartbeat_at','metadata'],alert_evaluation_states:['phase','pending_since','last_evaluated_slot','last_success_at','last_condition','last_event_id','next_occurrence_no','evaluation_state','suppressed_at','lease_owner','lease_until','lease_token','revision'],alert_events:['status','message','resolved_at','last_observed_at','last_value','last_condition','evaluation_state','revision'],outbox_messages:['processed_at','lease_until','attempts']};
function canonical(value){if(Array.isArray(value))return value.map(canonical);if(value&&typeof value==='object')return Object.fromEntries(Object.keys(value).sort().map(k=>[k,canonical(value[k])]));return value;}
export function canonicalPipelineRow(table,row,columns){
 for(const name of columns)assert(Object.hasOwn(row,name),'Protected column disappeared: '+table+'.'+name);
 for(const name of Object.keys(row).filter(k=>!columns.includes(k))){assert(Object.hasOwn(pipelineAddedDefaults[table]??{},name),'Unexpected added column: '+table+'.'+name);assert.deepEqual(row[name],pipelineAddedDefaults[table][name],'Migration changed prior delivery linkage: '+table+'.'+name);}
 return canonical(Object.fromEntries(columns.filter(k=>!pipelineDynamicColumns[table]?.includes(k)).map(k=>[k,row[k]])));
}
const receiptFields=['code','role_id','permission_id','user_id','actor_id','gateway_node_id','event_type','action','trace_id','resource_type','resource_id','operation','key','created_at','state','MigrationId','ProductVersion'];
export function hashPipelineTables(schema,data,before=null){
 const result={};for(const [table,s]of Object.entries(schema)){
  assert(/^[a-zA-Z0-9_]+$/.test(table),'Unsafe table');assert(s.primaryKey?.length,'Table has no actual primary key: '+table);const columns=before?.[table]?.columns??s.columns;
  assert(columns.every(k=>s.columns.includes(k)),'Protected schema lost column: '+table);
  for(const name of s.columns.filter(k=>!columns.includes(k)))assert(Object.hasOwn(pipelineAddedDefaults[table]??{},name),'Unexpected added column: '+table+'.'+name);
  if(before?.[table])assert.deepEqual(s.primaryKey,before[table].primaryKey,'Primary key changed: '+table);
  const rows={},metadata={};for(const row of data[table]??[]){const key=JSON.stringify(s.primaryKey.map(k=>{assert(Object.hasOwn(row,k),'Primary key missing');return row[k];}));assert(!Object.hasOwn(rows,key),'Duplicate primary key');rows[key]=createHash('sha256').update(JSON.stringify(canonicalPipelineRow(table,row,columns))).digest('hex');metadata[key]=Object.fromEntries(receiptFields.filter(k=>Object.hasOwn(row,k)).map(k=>[k,row[k]]));}
  result[table]={columns,allColumns:s.columns,primaryKey:s.primaryKey,ignoredColumns:pipelineDynamicColumns[table]??[],rows:canonical(rows),metadata:canonical(metadata)};
 }return result;
}
export async function capturePipelineTables(state,before=null,{sql=deliverySql}={}){
 const schema=JSON.parse(await sql(state,"SELECT COALESCE(jsonb_object_agg(t.table_name,jsonb_build_object('columns',t.columns,'primaryKey',p.columns)),'{}'::jsonb) FROM (SELECT table_name,jsonb_agg(column_name ORDER BY ordinal_position) AS columns FROM information_schema.columns WHERE table_schema='public' GROUP BY table_name) t LEFT JOIN (SELECT tc.table_name,jsonb_agg(kcu.column_name ORDER BY kcu.ordinal_position) AS columns FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu ON tc.constraint_name=kcu.constraint_name AND tc.table_schema=kcu.table_schema AND tc.table_name=kcu.table_name WHERE tc.table_schema='public' AND tc.constraint_type='PRIMARY KEY' GROUP BY tc.table_name) p ON p.table_name=t.table_name;"));
 const names=Object.keys(schema).sort();assert(names.length,'No protected database tables');for(const name of names)assert(/^[a-zA-Z0-9_]+$/.test(name),'Unsafe table');
 const query=names.map(name=>`SELECT jsonb_build_object('table','${name}','rows',COALESCE(jsonb_agg(to_jsonb(t)),'[]'::jsonb)) AS item FROM "${name}" t`).join(' UNION ALL '),observed=JSON.parse(await sql(state,'SELECT jsonb_agg(item) FROM ('+query+') all_tables;'));
 return hashPipelineTables(schema,Object.fromEntries(observed.map(t=>[t.table,t.rows])),before);
}
