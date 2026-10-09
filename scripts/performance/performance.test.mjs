import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {validateOwner,createOwner,cleanupOwned} from './owner.mjs';
import {buildMatrix,buildComparisonPairs,measureCell} from './matrix.mjs';
import {summarize,compareResults} from './report.mjs';
import {parseArguments,exportInputs} from './sources.mjs';
import {scanPublicEvidence,removePrivateOwner} from './secrets.mjs';
import {measureManagement} from './management.mjs';
import {verifyEnvironment,quotas} from './environment.mjs';
import {measureScopeManagement} from './scopes.mjs';
import {measureReferenceManagement} from './references.mjs';
import {retainSqlAudit} from './audit.mjs';
import {classifyMeasurement} from './classification.mjs';

const owner='d09a7407-3d06-45be-932b-5037422a89d2';
const manifest={owner,project:`webapi-performance-${owner}`};
test('sql_classification_preserves_every_command_and_requires_source_evidence',()=>{
  const source='a'.repeat(40),other='b'.repeat(40),authHash='c'.repeat(64),dataHash='d'.repeat(64),unknownHash='e'.repeat(64);
  const map={schemaVersion:1,sourceCommits:[source],rules:{[authHash]:{classification:'authorization',reason:'Active actor check',sourceRefs:[{commit:source,path:'src/WebApi.Infrastructure/Security/AuthorizationService.cs',line:13}]},[dataHash]:{classification:'data',reason:'Page resource projection',sourceRefs:[{commit:source,path:'src/WebApi.Infrastructure/Catalog/CatalogService.cs',line:19}]}}};
  const raw={samples:[{sql:{total:6,authorization:0,data:0,unclassified:6,rows:17,binaryColumnBytes:123},shapes:[{hash:authHash,kind:'reader',count:2},{hash:dataHash,kind:'reader',count:3},{hash:unknownHash,kind:'scalar',count:1}]}]};
  const classified=classifyMeasurement(raw,{manifest:map,sourceCommit:source,manifestHash:'f'.repeat(64)});
  assert.deepEqual(classified.samples[0].sql,{total:6,authorization:2,data:3,unclassified:1,rows:17,binaryColumnBytes:123});
  assert.deepEqual(classified.samples[0].rawSql,raw.samples[0].sql);assert.equal(classified.classificationComplete,false);assert.equal(raw.samples[0].sql.unclassified,6);
  assert.throws(()=>classifyMeasurement(raw,{manifest:map,sourceCommit:other,manifestHash:'f'.repeat(64)}),/source/i);
  const broken=structuredClone(raw);broken.samples[0].shapes.pop();assert.throws(()=>classifyMeasurement(broken,{manifest:map,sourceCommit:source,manifestHash:'f'.repeat(64)}),/total|command/i);
  const unsupported=structuredClone(map);unsupported.rules[authHash].sourceRefs[0].commit=other;assert.throws(()=>classifyMeasurement(raw,{manifest:unsupported,sourceCommit:source,manifestHash:'f'.repeat(64)}),/source|evidence/i);
});
test('sql_audit_is_explicitly_private_and_is_never_enabled_for_measurement',async()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'performance-sql-transfer-')),source=path.join(root,'source'),destination=path.join(root,'audit');fs.mkdirSync(source,{mode:0o700});
  const hash='e004ebd5b5532a4b85984a62f8ad48a81aa3460c1ca07701f386135d72cdecf5';fs.writeFileSync(path.join(source,hash+'.sql'),'SELECT 1',{mode:0o600});
  try {
    const result=await retainSqlAudit(source,destination);assert.equal(result.complete,true);assert.equal(result.templates,1);
    assert.equal(fs.statSync(destination).mode&0o777,0o700);assert.equal(fs.statSync(path.join(destination,hash+'.sql')).mode&0o777,0o600);
    assert.equal(fs.readFileSync(path.join(destination,hash+'.sql'),'utf8'),'SELECT 1');
    await assert.rejects(()=>retainSqlAudit(source,destination),/exist/i);
    fs.chmodSync(path.join(source,hash+'.sql'),0o644);await assert.rejects(()=>retainSqlAudit(source,path.join(root,'shared')),/private|permission/i);
    const parsed=parseArguments(['validate','--output','evidence','--sql-audit',path.join(root,'retained')]);assert.equal(parsed['sql-audit'],path.join(root,'retained'));
    assert.throws(()=>parseArguments(['baseline','--baseline','a'.repeat(40),'--harness','b'.repeat(40),'--output','evidence','--sql-audit',path.join(root,'forbidden')]),/audit|measurement/i);
    assert.throws(()=>parseArguments(['validate','--output',path.join(root,'public'),'--sql-audit',path.join(root,'public','private')]),/private|overlap/i);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});
test('environment_checks_actual_quotas_ownership_and_variant_identity',()=>{
  const containers=Object.entries(quotas).map(([service,quota])=>({labels:{'com.docker.compose.project':manifest.project,'com.webapi.performance.owner':owner,'com.docker.compose.service':service},nanoCpus:quota.cpus*1e9,memoryBytes:quota.memoryBytes,imageId:'sha256:'+'a'.repeat(64),user:'0'}));
  const result=verifyEnvironment(manifest,containers,{architecture:'aarch64',cpus:18});assert.equal(result.verified,true);
  const candidate=structuredClone(containers);candidate[0].imageId='sha256:'+'b'.repeat(64);
  assert.equal(verifyEnvironment(manifest,candidate,{architecture:'aarch64',cpus:18}).fingerprint,result.fingerprint);
  candidate[0].memoryBytes=123;assert.throws(()=>verifyEnvironment(manifest,candidate,{}),/quota/i);
  assert.throws(()=>verifyEnvironment(manifest,containers.slice(1),{}),/missing/i);
  candidate[0].labels['com.webapi.performance.owner']='foreign';assert.throws(()=>verifyEnvironment(manifest,candidate,{}),/owner/i);
});
test('owner_mismatch_never_deletes',()=>{
  assert.equal(validateOwner(manifest,{'com.docker.compose.project':manifest.project,'com.webapi.performance.owner':'other'}),false);
  assert.equal(validateOwner(manifest,{'com.docker.compose.project':manifest.project,'com.webapi.performance.owner':owner}),true);
  assert.equal(validateOwner({...manifest,project:'webapi-enterprise-local'},{'com.docker.compose.project':'webapi-enterprise-local','com.webapi.performance.owner':owner}),false);
});
test('build_failure_cleanup_needs_no_compose_and_never_deletes_foreign_resources',async()=>{
  const commands=[];
  const run=async(name,args)=>{commands.push([name,...args]);return {stdout:''};};
  const receipt=await cleanupOwned(manifest,undefined,run);assert.equal(receipt.remaining,0);
  assert.equal(commands.some(c=>c.includes('compose')),false);
  const foreignRun=async(name,args)=>{
    if(args[0]==='ps')return {stdout:'foreign-container'};
    if(args[0]==='container'&&args[1]==='inspect')return {stdout:JSON.stringify([{Config:{Labels:{'com.docker.compose.project':manifest.project,'com.webapi.performance.owner':'foreign'}}}])};
    throw Error('Unexpected destructive command');
  };
  await assert.rejects(()=>cleanupOwned(manifest,undefined,foreignRun),/ownership/i);
});
test('owner_secrets_have_private_modes_and_standard_base64_hmac',async()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'performance-owner-'));
  try {
    const owned=await createOwner(root);
    assert.equal(fs.statSync(owned.directory).mode&0o777,0o700);
    const secret=fs.readFileSync(path.join(owned.directory,'login-hmac'),'utf8');
    assert.match(secret,/^[A-Za-z0-9+/]{43}=$/);assert.equal(Buffer.from(secret,'base64').length,32);
    for(const name of fs.readdirSync(owned.directory))assert.equal(fs.statSync(path.join(owned.directory,name)).mode&0o777,0o600);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});
test('matrix_has_all_required_cells',()=>{
  const cells=buildMatrix(); assert.equal(cells.length,288);
  assert.equal(new Set(cells.map(c=>JSON.stringify(c))).size,288);
  assert.equal(cells.filter(c=>c.nodeMode==='dual'&&c.scale===10000&&c.auth==='rs256').length,18);
  assert.equal(cells.filter(c=>c.nodeMode==='gateway-a').length,24);
  assert.equal(cells.filter(c=>c.auth==='es256').length,6);
});
test('comparison_pairs_cover_all_cells_and_reverse_order_across_repeats',()=>{
  const pairs=buildComparisonPairs();assert.equal(pairs.length,144);
  const key=c=>[c.variant,c.scale,c.auth,c.concurrency,c.repeat,c.nodeMode].join(':');
  assert.deepEqual(pairs.flatMap(p=>p.cells).map(key).sort(),buildMatrix().map(key).sort());
  const topologies=new Map();
  for(const pair of pairs){assert.equal(pair.cells.length,2);const [first,second]=pair.cells;assert.notEqual(first.variant,second.variant);assert.deepEqual({...first,variant:null},{...second,variant:null});const topology=[first.scale,first.auth,first.concurrency,first.nodeMode].join(':');const group=topologies.get(topology)??[];group.push({repeat:first.repeat,first:first.variant});topologies.set(topology,group);}
  for(const group of topologies.values()){group.sort((a,b)=>a.repeat-b.repeat);assert.deepEqual(group.map(g=>g.repeat),[1,2,3]);assert.notEqual(group[0].first,group[1].first);assert.notEqual(group[1].first,group[2].first);}
  assert.equal(pairs[0].cells[0].variant,'baseline');assert.equal(pairs[1].cells[0].variant,'candidate');
});
test('percentiles_use_complete_window',()=>{
  const s=summarize([10,20,30,40,50],{durationMs:1000,expected:5});
  assert.equal(s.p50,30);assert.equal(s.p95,50);assert.equal(s.p99,50);assert.equal(s.rps,5);
});
test('interrupted_cell_is_incomplete',async()=>{
  const signal=AbortSignal.abort();
  const result=await measureCell({urls:['http://127.0.0.1:1'],requests:[{path:'/',expected:200}],concurrency:1,warmupMs:0,measureMs:10,signal});
  assert.equal(result.complete,false);assert.equal(result.incomplete,1);
});
test('different_harness_or_dataset_rejects_comparison',()=>{
  assert.throws(()=>compareResults({harness:'h',dataset:'a',cells:[]},{harness:'h',dataset:'b',cells:[]}),/identity/);
});
test('comparison_requires_the_same_pinned_sql_classification_input',()=>{
  const run={harness:'h',dataset:'d',environment:'e',classificationManifestHash:'c'.repeat(64),cells:[]};
  assert.throws(()=>compareResults(run,{...run,classificationManifestHash:'d'.repeat(64)}),/classification|identity/i);
  assert.throws(()=>compareResults({...run,classificationManifestHash:undefined},{...run,classificationManifestHash:undefined}),/classification|identity/i);
  assert.throws(()=>compareResults({...run,classificationManifestHash:'invalid'},{...run,classificationManifestHash:'invalid'}),/classification|identity/i);
});
test('missing_cell_prevents_complete',()=>{
  const result=compareResults({harness:'h',dataset:'d',classificationManifestHash:'c'.repeat(64),cells:[]},{harness:'h',dataset:'d',classificationManifestHash:'c'.repeat(64),cells:[]});
  assert.equal(result.complete,false);assert.equal(result.requiredCells,288);
});
test('zero_samples_and_duplicate_cells_cannot_close_the_matrix',()=>{
  const run=variant=>({harness:'h',dataset:'d',environment:'e',classificationManifestHash:'c'.repeat(64),cells:buildMatrix({variants:[variant]}).map(c=>({...c,complete:true,expected:1,unexpected:0,timeout:0,incomplete:0}))});
  const baseline=run('baseline'),candidate=run('candidate');
  assert.equal(compareResults(baseline,candidate).complete,true);
  candidate.cells[0].expected=0;assert.equal(compareResults(baseline,candidate).complete,false);
  candidate.cells[0].expected=1;candidate.cells.push({...candidate.cells[0]});
  assert.throws(()=>compareResults(baseline,candidate),/duplicate/i);
});
test('expected_401_counts_completed_not_failure',()=>{
  const s=summarize([1,2],{durationMs:1000,expected:2,expectedStatus:401});
  assert.equal(s.expected,2);assert.equal(s.unexpected,0);assert.equal(s.rps,2);
});
test('requests_finishing_after_window_are_drained_and_not_false_failures',async()=>{
  const server=createServer((_,response)=>setTimeout(()=>{response.writeHead(401);response.end('expected');},60));
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  try{
    const result=await measureCell({urls:[`http://127.0.0.1:${server.address().port}`],requests:[{path:'/',expected:401}],concurrency:1,warmupMs:0,measureMs:30});
    assert.equal(result.complete,true);assert.equal(result.timeout,0);assert.equal(result.incomplete,0);
    assert.equal(result.pendingAtCutoff,1);assert.equal(result.drained,1);assert.equal(result.expected,0);
    assert.equal(result.p95,null);
    assert.equal(Date.parse(result.windowEndedAt)-Date.parse(result.windowStartedAt),30);
    assert.ok(Date.parse(result.windowDrainedAt)>Date.parse(result.windowEndedAt));
    assert.equal(result.client.cpuMeasurementScope,'measurement-and-drain');
  }finally{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
});
test('dual_nodes_both_receive_requests_even_at_total_concurrency_one',async()=>{
  const servers=[createServer((_,r)=>r.end('node-a')),createServer((_,r)=>r.end('node-b'))];
  await Promise.all(servers.map(server=>new Promise(resolve=>server.listen(0,'127.0.0.1',resolve))));
  try {
    const result=await measureCell({urls:servers.map(server=>`http://127.0.0.1:${server.address().port}`),requests:[{path:'/',expected:200}],concurrency:1,warmupMs:0,measureMs:100});
    assert.equal(result.unexpected,0);assert.equal(result.nodes.length,2);assert.ok(result.nodes.every(node=>node.expected>0),JSON.stringify(result.nodes));
    assert.equal(result.nodes.reduce((sum,node)=>sum+node.expected,0),result.expected);
  }finally{for(const server of servers)server.closeAllConnections();await Promise.all(servers.map(server=>new Promise(resolve=>server.close(resolve))));}
});
test('server_counter_hooks_bracket_only_the_measured_window',async()=>{
  const events=[];const server=createServer((_,response)=>response.end('ok'));await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  try {
    const result=await measureCell({urls:[`http://127.0.0.1:${server.address().port}`],requests:[{path:'/',expected:200}],concurrency:1,warmupMs:10,measureMs:25,onMeasurementStart:async()=>events.push('start'),onMeasurementEnd:async()=>events.push('end')});
    assert.deepEqual(events,['start','end']);assert.equal(result.warmupMs,10);assert.ok(result.expected>0);assert.ok(Date.parse(result.windowStartedAt)<=Date.parse(result.windowDrainedAt));
  }finally{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
});
test('invalid_explicit_test_subnet_is_rejected_before_docker',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'performance-network-')),marker=path.join(root,'docker-was-called');
  fs.writeFileSync(path.join(root,'docker'),`#!/bin/sh\ntouch '${marker}'\nexit 99\n`,{mode:0o700});
  try{
    const result=spawnSync('bash',[fileURLToPath(new URL('../check-contracts.sh',import.meta.url)),'integration'],{env:{...process.env,PATH:root+path.delimiter+process.env.PATH,WEBAPI_NODE:process.execPath,WEBAPI_CONTRACT_TEST_SUBNET:'0.0.0.0/0'},encoding:'utf8'});
    assert.equal(fs.existsSync(marker),false);assert.equal(result.status,3);assert.match(result.stderr,/subnet/i);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});
test('measurements_require_pinned_sources_before_side_effects',()=>{
  assert.throws(()=>parseArguments(['baseline','--output','evidence']),/immutable/i);
  assert.throws(()=>parseArguments(['compare','--baseline','main','--candidate','HEAD','--harness','main','--output','evidence']),/immutable/i);
  const parsed=parseArguments(['validate','--output','evidence']);assert.equal(parsed.mode,'validate');
});
test('harness_overlay_never_replaces_baseline_product_source',async()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'performance-source-')),repo=path.join(root,'repo'),output=path.join(root,'build');fs.mkdirSync(repo);
  const git=(...args)=>{const result=spawnSync('git',args,{cwd:repo,encoding:'utf8'});assert.equal(result.status,0,result.stderr);return result.stdout.trim();};
  try {
    git('init','-q');fs.mkdirSync(path.join(repo,'src'));fs.writeFileSync(path.join(repo,'src','product.txt'),'baseline-product');git('add','src');git('-c','user.name=Performance Test','-c','user.email=performance@invalid.test','commit','-qm','baseline fixture');const baseline=git('rev-parse','HEAD');
    fs.mkdirSync(path.join(repo,'tools','WebApi.PerformanceHost'),{recursive:true});fs.writeFileSync(path.join(repo,'tools','WebApi.PerformanceHost','harness.txt'),'fixed-harness');fs.writeFileSync(path.join(repo,'src','product.txt'),'must-not-overlay');git('add','src','tools');git('-c','user.name=Performance Test','-c','user.email=performance@invalid.test','commit','-qm','harness fixture');const harness=git('rev-parse','HEAD');
    const result=await exportInputs({repo,source:baseline,harness,directory:output});
    assert.equal(fs.readFileSync(path.join(output,'src','product.txt'),'utf8'),'baseline-product');assert.equal(fs.readFileSync(path.join(output,'tools','WebApi.PerformanceHost','harness.txt'),'utf8'),'fixed-harness');
    assert.equal(result.source,baseline);assert.equal(result.harness,harness);assert.equal(result.productTree,git('rev-parse',baseline+':src'));
    assert.match(result.harnessFingerprint,/^[a-f0-9]{64}$/);
    assert.match(result.harnessFiles['tools/WebApi.PerformanceHost/harness.txt'],/^[a-f0-9]{64}$/);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});
test('evidence_secret_scan_reports_count_without_exposing_the_secret',async()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'performance-secret-scan-')),priv=path.join(root,'private'),pub=path.join(root,'public');fs.mkdirSync(priv);fs.mkdirSync(pub);
  try {
    const secret='secret-canary-not-for-evidence';fs.writeFileSync(path.join(priv,'postgres-password'),secret);fs.writeFileSync(path.join(pub,'report.json'),JSON.stringify({leaked:secret}));
    const result=await scanPublicEvidence(pub,priv);assert.ok(result.violations>0);assert.equal(JSON.stringify(result).includes(secret),false);
    fs.writeFileSync(path.join(pub,'report.json'),'{}');assert.equal((await scanPublicEvidence(pub,priv)).violations,0);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});
test('private_cleanup_requires_matching_owner_receipt_and_directory',async()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'performance-private-cleanup-'));const owned=await createOwner(root);
  try {
    await assert.rejects(()=>removePrivateOwner(owned,{owner:'foreign',project:owned.project,remaining:0}),/owner/i);assert.equal(fs.existsSync(owned.directory),true);
    await removePrivateOwner(owned,{owner:owned.owner,project:owned.project,remaining:0});assert.equal(fs.existsSync(owned.directory),false);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});
test('management_probe_covers_pages_and_retains_real_sql_counts',async()=>{
  const paths=[];const server=createServer(async(request,response)=>{
    const url=new URL(request.url,'http://fixture');response.setHeader('Content-Type','application/json');
    if(url.pathname==='/api/v1/auth/csrf'){response.setHeader('Set-Cookie','fixture=csrf; Path=/; HttpOnly');response.end('{"token":"csrf"}');return;}
    if(url.pathname==='/api/v1/auth/login'){assert.match(request.headers.cookie,/fixture=csrf/);response.setHeader('Set-Cookie','session=valid; Path=/; HttpOnly');response.end('{}');return;}
    if(url.pathname.startsWith('/__perf/sql/')){response.end('{"stats":{"total":42,"authorization":0,"data":0,"unclassified":42,"rows":100},"shapes":[]}');return;}
    assert.match(request.headers.cookie,/session=valid/);paths.push(url.pathname);
    const total=url.pathname.endsWith('/references')?101:100;const page=Number(url.searchParams.get('page')),size=Number(url.searchParams.get('pageSize'));
    const count=Math.min(size,Math.max(0,total-(page-1)*size));response.setHeader('X-Performance-Request-Id','request-'+paths.length);
    response.end(JSON.stringify({total,page,pageSize:size,items:Array.from({length:count},(_,i)=>({id:`id-${(page-1)*size+i}`,...(url.pathname.endsWith('/releases')?{frozenCandidate:{versions:Array(8).fill({})},approvalSteps:Array(2).fill({}),targets:Array(6).fill({})}:{})}))}));
  });await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  try {
    const result=await measureManagement({baseUrl:`http://127.0.0.1:${server.address().port}`,token:'private-token',password:'private-password',scale:100,seed:20261009,validation:true});
    assert.equal(result.samples.length,13*3*4);assert.equal(new Set(result.samples.map(s=>s.list)).size,13);assert.ok(result.samples.some(s=>s.pageKind==='out-of-range'&&s.ids.length===0));
    assert.equal(result.samples[0].sql.total,42);assert.equal(result.samples[0].sql.unclassified,42);assert.equal(JSON.stringify(result).includes('private-password'),false);
  }finally{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
});
test('scope_probe_checks_exact_visible_ids_and_negative_statuses',async()=>{
  const actors=[
    {kind:'organization-role',username:'org',organizationId:'organization',loginStatus:200,applicationStatus:200,applicationIds:['a','b'],policyStatus:200,policyIds:['p']},
    {kind:'no-function',username:'denied',organizationId:'organization',loginStatus:200,applicationStatus:200,applicationIds:[],policyStatus:404,policyIds:[]},
    {kind:'inactive-user',username:'inactive',organizationId:'organization',loginStatus:401,applicationStatus:401,applicationIds:[],policyStatus:401,policyIds:[]}
  ];let cursor=0;
  const server=createServer(async(request,response)=>{
    const url=new URL(request.url,'http://fixture');response.setHeader('Content-Type','application/json');
    if(url.pathname.endsWith('/csrf')){response.end('{"token":"csrf"}');return;}
    if(url.pathname.endsWith('/login')){let raw='';for await(const chunk of request)raw+=chunk;const actor=actors.find(a=>a.username===JSON.parse(raw).username);response.statusCode=actor.loginStatus;response.setHeader('Set-Cookie','actor='+actor.username+'; Path=/');response.end('{}');return;}
    if(url.pathname.startsWith('/__perf/sql/')){response.end('{"stats":{"total":2,"unclassified":2,"rows":1},"shapes":[]}');return;}
    const actor=actors.find(a=>request.headers.cookie?.includes('actor='+a.username));
    const application=url.pathname.endsWith('/applications'),status=application?actor.applicationStatus:actor.policyStatus;response.statusCode=status;response.setHeader('X-Performance-Request-Id','scope-'+(++cursor));
    if(status!==200){response.end(JSON.stringify({status,title:'Fixture denied',code:status===401?'authentication_required':'resource_not_found',traceId:'scope-'+cursor}));return;}
    const ids=application?actor.applicationIds:actor.policyIds,page=Number(url.searchParams.get('page')),size=Number(url.searchParams.get('pageSize'));
    response.end(JSON.stringify({total:ids.length,items:ids.slice((page-1)*size,page*size).map(id=>({id}))}));
  });await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  try {
    const result=await measureScopeManagement({baseUrl:`http://127.0.0.1:${server.address().port}`,token:'private',password:'password-canary',manifest:{scale:100,businessFingerprint:'scope',actors},validation:true});
    assert.equal(result.complete,true);assert.equal(result.samples.length,39);assert.ok(result.samples.some(s=>s.status===404));assert.ok(result.samples.some(s=>s.status===401));
    assert.equal(JSON.stringify(result).includes('password-canary'),false);
  }finally{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
});
test('reference_probe_checks_every_dto_field_and_exact_page_order',async()=>{
  const expected=Array.from({length:15},(_,i)=>({referenceKind:'Working',environmentId:'environment',apiId:'api-'+i,routeId:'route-'+i,configVersion:null,sourceRevision:1,name:'route'+i}));let corrupt=false,missingField=false;
  const server=createServer((request,response)=>{
    const url=new URL(request.url,'http://fixture');response.setHeader('Content-Type','application/json');
    if(url.pathname.endsWith('/csrf')){response.end('{"token":"csrf"}');return;}
    if(url.pathname.endsWith('/login')){response.end('{}');return;}
    if(url.pathname.startsWith('/__perf/sql/')){response.end('{"stats":{"total":5,"unclassified":5,"rows":15},"shapes":[]}');return;}
    const page=Number(url.searchParams.get('page')),size=Number(url.searchParams.get('pageSize'));const items=expected.slice((page-1)*size,page*size).map(row=>({...row,sourceRevision:corrupt?999:row.sourceRevision}));if(missingField)for(const row of items)delete row.configVersion;
    response.setHeader('X-Performance-Request-Id','reference-request');response.end(JSON.stringify({total:expected.length,items}));
  });await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  try {
    const options={baseUrl:`http://127.0.0.1:${server.address().port}`,token:'private',password:'password-canary',manifest:{scale:100,policyId:'policy',businessFingerprint:'reference',actors:[{kind:'platform-role',username:'admin',expected}]},validation:true};
    const result=await measureReferenceManagement(options);assert.equal(result.complete,true);assert.equal(result.samples.length,12);assert.ok(result.samples.some(s=>s.pageKind==='out-of-range'&&s.items.length===0));
    corrupt=true;await assert.rejects(()=>measureReferenceManagement(options),/DTO|reference/i);
    corrupt=false;missingField=true;await assert.rejects(()=>measureReferenceManagement(options),/DTO|reference/i);
  }finally{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
});

test('resource_diagnostic_is_explicit_and_cannot_export_sql_audit',()=>{
  assert.equal(parseArguments(['validate','--diagnostic','resources','--output','resource-diagnostic']).diagnostic,'resources');
  assert.throws(()=>parseArguments(['validate','--diagnostic','resources','--output','public','--sql-audit','private']),/audit/i);
  assert.throws(()=>parseArguments(['baseline','--diagnostic','resources','--baseline','a'.repeat(40),'--harness','b'.repeat(40),'--output','public']),/diagnostic/i);
});

test('failed_workload_details_are_private_and_keep_the_original_error',async()=>{
 const support=await import('./runner-support.mjs');assert.equal(typeof support.runPrivateWorkload,'function');
 const directory=fs.mkdtempSync(path.join(os.tmpdir(),'workload-failure-'));const failure=Object.assign(Error('private-canary-message'),{code:4,signal:null,stdout:'private-canary-stdout',stderr:'private-canary-stderr'});
 try{
  await assert.rejects(()=>support.runPrivateWorkload({directory},'cell-key',async()=>{throw failure;}),e=>e===failure);
  const file=path.join(directory,'cell-key.workload-failure.json'),data=JSON.parse(fs.readFileSync(file,'utf8'));assert.equal(data.exitCode,4);assert.equal(data.stderr,'private-canary-stderr');assert.equal(fs.statSync(file).mode&0o777,0o600);
 }finally{fs.rmSync(directory,{recursive:true,force:true});}
});
test('resource_diagnostic_auth_is_scoped_and_explicit',()=>{
 assert.equal(parseArguments(['validate','--diagnostic','resources','--diagnostic-auth','no-mapping','--output','diagnostic'])['diagnostic-auth'],'no-mapping');
 assert.throws(()=>parseArguments(['validate','--diagnostic-auth','no-mapping','--output','diagnostic']),/diagnostic/i);
 assert.throws(()=>parseArguments(['validate','--diagnostic','resources','--diagnostic-auth','unsupported','--output','diagnostic']),/diagnostic/i);
});

test('startup_failure_diagnostics_validate_owner_before_logs_and_export_only_selected_state',async()=>{
 const support=await import('./runner-support.mjs');assert.equal(typeof support.retainOwnerFailureDiagnostics,'function');
 const directory=fs.mkdtempSync(path.join(os.tmpdir(),'owner-failure-')),id='a'.repeat(64),labels={'com.docker.compose.project':manifest.project,'com.webapi.performance.owner':manifest.owner,'com.docker.compose.service':'gateway-a'};let foreign=false,logs=0;
 const run=async args=>{
  if(args[0]==='compose')return {stdout:id+'\n',stderr:''};
  if(args[0]==='inspect')return {stdout:JSON.stringify({id,labels:{...labels,'com.webapi.performance.owner':foreign?'foreign':manifest.owner},running:false,exitCode:137,oomKilled:true,restartCount:0})+'\n',stderr:''};
  if(args[0]==='logs'){logs++;return {stdout:'private-log-canary',stderr:''};}throw Error('Unexpected command');
 };
 try{
  const result=await support.retainOwnerFailureDiagnostics({...manifest,directory},'compose.yml',{},run);
  assert.deepEqual(result.containers,[{service:'gateway-a',running:false,exitCode:137,oomKilled:true,restartCount:0}]);assert.equal(JSON.stringify(result).includes('private-log-canary'),false);assert.equal(fs.statSync(path.join(directory,'failure-gateway-a-'+id+'.log')).mode&0o777,0o600);
  foreign=true;logs=0;await assert.rejects(()=>support.retainOwnerFailureDiagnostics({...manifest,directory},'compose.yml',{},run),/owner/i);assert.equal(logs,0);
 }finally{fs.rmSync(directory,{recursive:true,force:true});}
});
