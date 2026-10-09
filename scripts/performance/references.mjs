import {performance} from 'node:perf_hooks';
import {createHash} from 'node:crypto';
import {createManagementClient} from './management.mjs';
import {collectExplain} from './explain.mjs';
const fields=['referenceKind','environmentId','apiId','routeId','configVersion','sourceRevision','name'];
const value=row=>fields.map(field=>row[field]??null);
export async function measureReferenceManagement({baseUrl,token,password,manifest,validation=false,explain=false}){
  const samples=[];
  for(const actor of manifest.actors){
    const {request,loginStatus}=await createManagementClient({baseUrl,token,password,username:actor.username});if(loginStatus!==200)throw Error('Reference actor login failed');
    for(const pageSize of [1,50,100]){
      const last=Math.max(1,Math.ceil(actor.expected.length/pageSize));
      for(const [pageKind,page] of [['first',1],['middle',Math.floor(last/2)+1],['last',last],['out-of-range',last+1]])for(let repeat=1;repeat<=(validation?1:3);repeat++){
        const start=performance.now(),response=await request(`/api/v1/policies/${manifest.policyId}/references?page=${page}&pageSize=${pageSize}`),bytes=Buffer.from(await response.arrayBuffer()),durationMs=performance.now()-start;
        if(response.status!==200)throw Error('Reference query returned unexpected '+response.status);
        const result=JSON.parse(bytes),expected=actor.expected.slice((page-1)*pageSize,page*pageSize);
        if(Array.isArray(result.items)&&result.items.some(row=>fields.some(field=>!Object.hasOwn(row,field))))throw Error('Reference DTO field missing: '+actor.kind);
        if(result.total!==actor.expected.length||!Array.isArray(result.items)||JSON.stringify(result.items.map(value))!==JSON.stringify(expected.map(value))){
          const actual=Array.isArray(result.items)?result.items.map(value):[],wanted=expected.map(value),different=wanted.findIndex((row,index)=>JSON.stringify(row)!==JSON.stringify(actual[index]));
          throw Error('Reference DTO/order/Total mismatch: '+JSON.stringify({actor:actor.kind,page,pageSize,total:result.total,expectedTotal:actor.expected.length,index:different,actual:actual[different],expected:wanted[different]}));
        }
        const id=response.headers.get('X-Performance-Request-Id');if(!id)throw Error('Reference SQL association missing');
        const sqlResponse=await request('/__perf/sql/'+id);if(!sqlResponse.ok)throw Error('Reference SQL probe unavailable');const measured=await sqlResponse.json();
        samples.push({actor:actor.kind,scale:manifest.scale,pageKind,page,pageSize,repeat,total:result.total,items:result.items,responseBytes:bytes.length,responseHash:createHash('sha256').update(bytes).digest('hex'),durationMs,sql:measured.stats,shapes:measured.shapes,...explain?{explain:await collectExplain(request,id,measured)}:{}});
      }
    }
  }
  return {scale:manifest.scale,businessFingerprint:manifest.businessFingerprint,sourceCounts:{working:manifest.working,frozenRelease:manifest.frozenRelease,runtime:manifest.runtime},candidateBytes:manifest.candidateBytes,runtimeBytes:manifest.runtimeBytes,samples,classificationComplete:samples.every(s=>s.sql.unclassified===0),complete:samples.length===manifest.actors.length*12*(validation?1:3)&&samples.length>0};
}
