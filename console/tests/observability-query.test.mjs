import {test} from 'node:test';import assert from 'node:assert/strict';
import {parseObservationSearch,serializeObservationSearch,observationRange,createObservationGate,metricDisplay,chartSegments} from '../src/observability/query-state.mjs';
test('validated URL keeps allowed filters and never contains raw IP',()=>{
 assert.equal(parseObservationSearch('?range=24h').range,'24h');assert.equal(parseObservationSearch('?range=30d&group=Invalid&api=bad&status=script').range,'1h');
 const api='caec1d01-a1cc-44ef-b495-19b8a6f5a9ba';const s=serializeObservationSearch({range:'7d',api,group:'Application',ip:'10.0.0.1',cursor:'signed'});
 assert.ok(!s.includes('10.0.0.1'));assert.equal(parseObservationSearch(s).api,api);assert.equal(parseObservationSearch('?ip=10.0.0.1').ip,undefined);
});
test('scope switch aborts old requests, clears cursor and rejects a late response',()=>{
 const gate=createObservationGate(),first=gate.begin('env-a','first');assert.equal(first.cursor,'first');const second=gate.begin('env-b','foreign-cursor');
 assert.ok(first.signal.aborted);assert.equal(second.cursor,undefined);assert.equal(gate.isCurrent(first),false);assert.equal(gate.isCurrent(second),true);gate.dispose();assert.ok(second.signal.aborted);
});
test('fixed UTC cutoff survives query roundtrip; new duration resets cursor',()=>{
 const end='2026-10-04T04:00:00.000Z',s=parseObservationSearch(serializeObservationSearch({range:'6h',end,page:2,group:'Api'}));
 assert.deepEqual(observationRange(s),{start:'2026-10-03T22:00:00.000Z',end});assert.equal(s.page,2);
 assert.equal(parseObservationSearch('?range=7d&page=-1&end=bad').page,1);
});
test('query updates and Back emit search without changing accepted pathname or guards',async()=>{
 const {bindNavigation,navigate}=await import('../src/navigation.mjs');const target=new EventTarget();target.Event=Event;target.location=new URL('http://qa/observability/metrics');
 target.history={state:null,replaceState(s,_t,url){this.state=s;target.location=new URL(url,target.location);},pushState(s,_t,url){this.state=s;target.location=new URL(url,target.location);}};
 let path,search;const dispose=bindNavigation(target,(p,s)=>{path=p;search=s;});navigate('/observability/metrics?range=24h',false,target);assert.equal(path,'/observability/metrics');assert.equal(search,'?range=24h');
 target.addEventListener('app-navigation',e=>e.preventDefault());navigate('/observability/metrics?range=7d',false,target);assert.equal(search,'?range=24h');dispose();
});
test('unknown values stay blank while known zero keeps its unit',()=>{
 assert.equal(metricDisplay({value:null,unit:'%',state:'Partial'}),'—');assert.equal(metricDisplay({value:0,unit:'%',state:'Available'}),'0.00%');
 assert.equal(metricDisplay({value:0.995,unit:'ratio',state:'Available'}),'99.50%');
});
test('chart leaves a gap across missing samples and caps source rows at 600',()=>{
 const p=[{time:'2026-10-04T00:00:00Z',value:2},{time:'2026-10-04T00:01:00Z',value:null},{time:'2026-10-04T00:02:00Z',value:5}];
 const s=chartSegments(p);assert.equal(s.length,2);assert.equal(s[0].length,1);assert.equal(s[1].length,1);
 assert.ok(chartSegments(Array.from({length:800},(_,i)=>({time:new Date(i*1000).toISOString(),value:i}))).flat().length<=600);
});
test('scope switch removes URL cursor and resets page while preserving time cutoff',async()=>{
 const {resetObservationScopeSearch}=await import('../src/observability/query-state.mjs');
 const result=parseObservationSearch(resetObservationScopeSearch('?range=24h&cursor=old&page=4&end=2026-10-04T04:00:00Z&destination=caec1d01-a1cc-44ef-b495-19b8a6f5a9ba','environment'));
 assert.equal(result.cursor,undefined);assert.equal(result.page,1);assert.equal(result.destination,undefined);assert.equal(result.range,'24h');assert.equal(result.end,'2026-10-04T04:00:00.000Z');
});
