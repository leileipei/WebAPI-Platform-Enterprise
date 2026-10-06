import {test} from 'node:test';import assert from 'node:assert/strict';
// Exercise the real transport module, not its implementation's source text.
const {apiRequest,ApiError}=await import('../src/api/transport.mjs');
test('write carries cookie CSRF ETag and stable idempotency; csrf refreshed after login',async()=>{
 const calls=[];globalThis.fetch=async(path,options={})=>{calls.push([path,options]);return new Response(JSON.stringify(path.endsWith('/csrf')?{token:'issued-'+calls.length}:{id:'saved'}),{status:200,headers:{'Content-Type':'application/json','ETag':'"2"'}});};
 await apiRequest('/auth/login',{method:'POST',body:{username:'x',password:'private'}});await apiRequest('/organizations/id',{method:'PUT',body:{name:'draft'},etag:'"1"',idempotencyKey:'stable-retry'});
 const write=calls.find(x=>x[0].endsWith('/organizations/id'))[1];assert.equal(write.credentials,'same-origin');assert.equal(write.headers['If-Match'],'"1"');assert.equal(write.headers['Idempotency-Key'],'stable-retry');assert.equal(write.headers['X-CSRF-Token'],'issued-3');
});
test('412 exposes conflict and trace without automatic destructive retry',async()=>{
 let count=0;globalThis.fetch=async()=>{count++;return new Response(JSON.stringify({status:412,detail:'changed',traceId:'trace-public'}),{status:412});};
 await assert.rejects(apiRequest('/organizations/id',{method:'PUT',body:{name:'retain-me'}}),e=>e instanceof ApiError&&e.status===412&&e.traceId==='trace-public');assert.equal(count,1);
});

test('resource 403 and hidden-resource 404 trigger authority refresh; 412 retains editing authority',async()=>{
 const observed=[],previous=globalThis.dispatchEvent;
 globalThis.dispatchEvent=event=>{observed.push(event.type);return true;};
 try {
  for(const status of [403,404,412]){
   globalThis.fetch=async()=>new Response(JSON.stringify({detail:'resource denied'}),{status});
   await assert.rejects(apiRequest('/environments/id/snapshots/1'),error=>error instanceof ApiError&&error.status===status);
  }
  assert.deepEqual(observed,['permission-refresh','permission-refresh']);
 } finally {globalThis.dispatchEvent=previous;}
});
test('structured contract failures preserve affected pointers for a reviewable repair',async()=>{
 const issues=[{code:'schema_reference_impact',pointer:'/components/schemas/Old',message:'请显式处理引用。',line:2,column:3}];globalThis.fetch=async()=>new Response(JSON.stringify({title:'引用受影响',code:'schema_reference_impact',traceId:'t',issues}),{status:422});
 await assert.rejects(apiRequest('/versions/v/schemas'),error=>{assert.ok(error instanceof ApiError);assert.deepEqual(error.issues,issues);assert.equal(error.code,'schema_reference_impact');return true;});
});
