import test from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {executeComparisonPairs,responseContractHash,verifyManagementParity,verifyPairReceipts} from './comparison.mjs';
import {buildComparisonPairs} from './matrix.mjs';
import {parseArguments} from './sources.mjs';
import {runComparison} from './comparison-runner.mjs';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
const hash='a'.repeat(64);
test('comparison_driver_has_no_unresolved_lexical_names_in_completion_paths',async()=>{
  const {createRequire}=await import('node:module'),repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
  const ts=createRequire(path.join(repo,'console/package.json'))('typescript'),file=path.join(repo,'scripts/performance/comparison-runner.mjs');
  const program=ts.createProgram([file],{allowJs:true,checkJs:true,noEmit:true,target:ts.ScriptTarget.ESNext,module:ts.ModuleKind.NodeNext,moduleResolution:ts.ModuleResolutionKind.NodeNext});
  const unresolved=program.getSemanticDiagnostics(program.getSourceFile(file)).filter(d=>d.code===2304||d.code===18004);
  assert.deepEqual(unresolved.map(d=>ts.flattenDiagnosticMessageText(d.messageText,' ')),[]);
});
test('failed_management_preflight_cannot_start_gateway_measurements',async()=>{
  const runner=await import('./comparison-runner.mjs');assert.equal(typeof runner.afterManagementPreflight,'function');
  await assert.rejects(()=>runner.afterManagementPreflight({preflight:undefined,prepareManagement:async()=>({complete:false}),measureGateway:async()=>{throw Error('Gateway wrongly started');}}),/management preflight incomplete/i);
});
test('management_only_preflight_returns_no_gateway_windows',async()=>{
  const runner=await import('./comparison-runner.mjs');assert.equal(typeof runner.afterManagementPreflight,'function');
  const result=await runner.afterManagementPreflight({preflight:'management',prepareManagement:async()=>({complete:true}),measureGateway:async()=>{throw Error('Gateway wrongly started');}});
  assert.deepEqual(result.cells,[]);assert.equal(result.complete,false);assert.equal(result.managementOnly,true);
});
test('management_preflight_is_explicit_and_cannot_be_a_gateway_performance_run',()=>{
  const fixed=['--baseline','a'.repeat(40),'--candidate','b'.repeat(40),'--harness','c'.repeat(40)];
  assert.equal(parseArguments(['compare',...fixed,'--preflight','management','--output','/tmp/management-preflight']).preflight,'management');
  assert.equal(parseArguments(['validate','--diagnostic','compare',...fixed,'--preflight','management','--output','/tmp/management-preflight']).preflight,'management');
  for(const mode of ['baseline','explain','migration'])assert.throws(()=>parseArguments([mode,...fixed,'--preflight','management','--output','/tmp/management-preflight']),/preflight/i);
  assert.throws(()=>parseArguments(['compare',...fixed,'--preflight','anything','--output','/tmp/management-preflight']),/preflight/i);
  assert.throws(()=>parseArguments(['validate','--diagnostic','compare','--baseline','a'.repeat(40),'--preflight','management','--output','/tmp/management-preflight']),/source|commit/i);
});
test('comparison_execution_is_serial_records_actual_order_and_stops_on_failed_cell',async()=>{
  const pairs=buildComparisonPairs().slice(0,2),events=[];let active=0;
  const run=await executeComparisonPairs({pairs,switchVariant:async c=>{assert.equal(active,0);events.push('switch:'+c.variant);},measure:async c=>{active++;events.push('measure:'+c.variant);await new Promise(r=>setTimeout(r,1));active--;return {...c,expected:1,unexpected:0,timeout:0,incomplete:0,complete:true};}});
  assert.deepEqual(events,['switch:baseline','measure:baseline','switch:candidate','measure:candidate','switch:candidate','measure:candidate','switch:baseline','measure:baseline']);assert.equal(run.complete,true);assert.equal(run.pairs.length,2);
  events.length=0;await assert.rejects(()=>executeComparisonPairs({pairs,switchVariant:async()=>{},measure:async c=>{events.push(c);return {...c,expected:0,unexpected:1,timeout:0,incomplete:0,complete:false};}}),/incomplete/i);assert.equal(events.length,1);
  const bad=structuredClone(pairs);bad[0].cells[1].auth='other';await assert.rejects(()=>executeComparisonPairs({pairs:bad,switchVariant:async()=>{throw Error('Should not run');},measure:async()=>{}}),/pair/i);
});
test('error_contract_hash_excludes_only_dynamic_traceId_and_retains_original_success_bytes',()=>{
  const json=traceId=>Buffer.from(JSON.stringify({status:404,title:'Missing',code:'resource_not_found',traceId}));
  assert.equal(responseContractHash(json('first'),404),responseContractHash(json('second'),404));
  assert.notEqual(responseContractHash(json('first'),404),responseContractHash(Buffer.from(JSON.stringify({status:404,title:'Changed',code:'resource_not_found',traceId:'first'})),404));
  const success=Buffer.from('{"total":1,"items":[{"id":"x"}]}');assert.equal(responseContractHash(success,200),createHash('sha256').update(success).digest('hex'));
  assert.throws(()=>responseContractHash(Buffer.from('{"status":404}'),404),/trace/i);
});
test('management_parity_checks_complete_dto_hash_visible_ids_totals_status_and_all_page_keys',()=>{
  const sample={actor:'platform-role',list:'application',scale:100,pageSize:1,pageKind:'first',page:1,repeat:1,total:1,ids:['x'],status:200,responseBytes:23,responseHash:hash,responseContractHash:hash};
  const a={complete:true,samples:[sample]},b=structuredClone(a);assert.equal(verifyManagementParity(a,b).complete,true);
  for(const mutate of [s=>s.samples[0].responseHash='b'.repeat(64),s=>s.samples[0].ids=['other'],s=>s.samples[0].total=2,s=>s.samples[0].status=401,s=>s.samples[0].page=2,s=>s.samples.push(s.samples[0]),s=>s.complete=false]){const changed=structuredClone(b);mutate(changed);assert.equal(verifyManagementParity(a,changed).complete,false);}
  const denied=structuredClone(a);Object.assign(denied.samples[0],{status:404,total:null,ids:[],responseHash:'b'.repeat(64),responseContractHash:'c'.repeat(64)});const other=structuredClone(denied);other.samples[0].responseHash='d'.repeat(64);assert.equal(verifyManagementParity(denied,other).complete,true);other.samples[0].responseContractHash=hash;assert.equal(verifyManagementParity(denied,other).complete,false);
});

test('comparison_selfcheck_is_explicit_shortened_validation_and_not_immutable_measurement',()=>{assert.equal(parseArguments(['validate','--diagnostic','compare','--output','/tmp/comparison-unit']).diagnostic,'compare');assert.throws(()=>parseArguments(['baseline','--baseline','a'.repeat(40),'--harness','b'.repeat(40),'--diagnostic','compare','--output','/tmp/comparison-unit']),/diagnostic/i);});

test('immutable_comparison_rejects_missing_commit_before_owner_or_output_creation',async()=>{const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');await assert.rejects(()=>runComparison({mode:'compare',baseline:'f'.repeat(40),candidate:'e'.repeat(40),harness:'d'.repeat(40),output:'/tmp/unused-webapi-comparison-output'},repo),/immutable.*commit/i);});

test('pair_completion_requires_actual_scheduled_order_nonoverlap_and_every_measured_key',()=>{
 const scheduled=buildComparisonPairs(),cells=[],pairs=scheduled.map((p,index)=>({pairId:p.pairId,order:p.cells.map(c=>c.variant),complete:true,attempts:p.cells.map((c,side)=>{const start=Date.UTC(2026,9,9)+(index*2+side)*40000;const windowStartedAt=new Date(start+10000).toISOString(),windowEndedAt=new Date(start+30000).toISOString();cells.push({...c,windowStartedAt,windowEndedAt});return {key:[c.variant,c.scale,c.auth,c.concurrency,c.repeat,c.nodeMode].join(':'),variant:c.variant,switchingStartedAt:new Date(start-1000).toISOString(),dispatchStartedAt:new Date(start).toISOString(),returnedAt:new Date(start+32000).toISOString(),windowStartedAt,windowEndedAt};})}));
 const input={pairs,cells};assert.equal(verifyPairReceipts(input).complete,true);
 for(const mutate of [r=>r.pairs.pop(),r=>r.cells.pop(),r=>r.pairs[0].order.reverse(),r=>r.pairs[0].attempts[1].switchingStartedAt=r.pairs[0].attempts[0].dispatchStartedAt,r=>r.cells[0].windowStartedAt='invalid',r=>r.pairs[0].attempts[0].windowEndedAt=r.pairs[0].attempts[0].windowStartedAt,r=>r.cells.push(r.cells[0])]){const run=structuredClone(input);mutate(run);assert.equal(verifyPairReceipts(run).complete,false);}
});

test('diagnostics_that_do_not_capture_sql_reject_audit_option_instead_of_ignoring_it',()=>{for(const diagnostic of ['compare','migration'])assert.throws(()=>parseArguments(['validate','--diagnostic',diagnostic,'--sql-audit','/tmp/private-sql-fixture','--output','/tmp/public-comparison-unit']),/audit/i);});
