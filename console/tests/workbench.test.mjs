import {test,after} from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
const model = await import('../src/workbench/model.mjs').catch(e=>{if(e.code==='ERR_MODULE_NOT_FOUND')return {};throw e;});
const scope={organizationId:'org',projectId:'project',environmentId:'env'};
const end='2026-10-06T10:00:00.000Z';
// These cases catch wrong scope/query, unauthorized reads, coupled error handling and stale values.
test('workbench uses project API total and current environment 24h metrics with top API ranking',()=>{
 assert.equal(typeof model.workbenchPlan,'function');
 const plan=model.workbenchPlan(scope,()=>true,end);
 assert.equal(plan.api.path,'/projects/project/apis?page=1&pageSize=1');
 const query=new URL('http://local'+plan.metrics.path).searchParams;
 assert.equal(query.get('environmentId'),'env');assert.equal(query.get('allAccessibleEnvironments'),'false');
 assert.equal(query.get('start'),'2026-10-05T10:00:00.000Z');assert.equal(query.get('end'),end);
 assert.equal(query.get('groupBy'),'Api');assert.equal(query.get('pageSize'),'5');
 assert.equal(new URL('http://local'+plan.alerts.path).searchParams.get('status'),'Open');
 assert.equal(plan.releases.path,'/environments/env/releases?page=1&pageSize=50');
});
test('workbench checks project API permission independently from environment and skips denied reads',async()=>{
 assert.equal(typeof model.workbenchPlan,'function');assert.equal(typeof model.loadWorkbench,'function');
 const plan=model.workbenchPlan(scope,(code,s)=>code==='api.read'&&!s.environmentId,end);
 assert.equal(plan.api.state,'loading');assert.equal(plan.metrics.state,'denied');
 assert.equal(plan.releases.state,'denied');
 const results=[];await model.loadWorkbench(plan,async path=>{assert.equal(path,'/projects/project/apis?page=1&pageSize=1');return {items:[],total:87,page:1,pageSize:1};},new AbortController().signal,(key,result)=>results.push([key,result]));
 assert.equal(results.length,1);assert.equal(results[0][1].data.total,87);
});
test('no workspace cannot trigger global or empty-id reads',()=>{
 assert.equal(typeof model.workbenchPlan,'function');
 assert.ok(Object.values(model.workbenchPlan({},()=>true,end)).every(x=>x.state==='empty'));
});
let server;after(()=>new Promise(resolve=>server?server.close(resolve):resolve()));
test('one failing source does not erase successful modules and HTTP access loss clears its data',async()=>{
 assert.equal(typeof model.loadWorkbench,'function');
 server=createServer((req,res)=>{res.setHeader('Content-Type','application/json');if(req.url.startsWith('/observability/metrics')){res.statusCode=503;res.end('{}');}else if(req.url.startsWith('/environments/env/gateway-nodes')){res.statusCode=404;res.end('{}');}else res.end(JSON.stringify({items:[],total:0,page:1,pageSize:50}));});
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const result={};await model.loadWorkbench(model.workbenchPlan(scope,()=>true,end),async(path,{signal})=>{const r=await fetch('http://127.0.0.1:'+server.address().port+path,{signal});if(!r.ok)throw Object.assign(new Error('unavailable'),{status:r.status});return r.json();},new AbortController().signal,(key,value)=>result[key]=value);
 assert.equal(result.api.state,'ready');assert.equal(result.api.data.total,0);
 assert.equal(result.metrics.state,'error');assert.equal(result.nodes.state,'denied');assert.equal(result.nodes.data,undefined);
 assert.equal(result.releases.state,'ready');
});
test('an aborted scope request cannot publish late results even if transport ignores abort',async()=>{
 assert.equal(typeof model.loadWorkbench,'function');
 const controller=new AbortController();let resolve;const writes=[];
 const pending=model.loadWorkbench({api:{state:'loading',path:'/old'}},()=>new Promise(r=>resolve=r),controller.signal,(...args)=>writes.push(args));
 controller.abort();resolve({total:99});await pending;assert.equal(writes.length,0);
});
test('missing, stale, unavailable and non-finite metrics cannot become healthy-looking numbers',()=>{
 assert.equal(typeof model.workbenchMetric,'function');
 for(const state of ['Unavailable','Stale','NoData','NotApplicable'])assert.equal(model.workbenchMetric({state:'ready',data:{sourceState:state,data:{kpis:[{metric:'success_ratio',value:1,unit:'ratio',state:'Available'}]}}},'success_ratio'),null);
 assert.equal(model.workbenchMetric({state:'ready',data:{sourceState:'Partial',data:{kpis:[{metric:'success_ratio',value:.98,unit:'ratio',state:'Available'}]}}},'success_ratio').value,.98);
 assert.equal(model.workbenchMetric({state:'error'},'success_ratio'),null);
});
