import {performance} from 'node:perf_hooks';
import {createHash} from 'node:crypto';
import {createManagementClient} from './management.mjs';
import {collectExplain} from './explain.mjs';
import {responseContractHash} from './comparison.mjs';
export async function measureScopeManagement({baseUrl,token,password,manifest,validation=false,explain=false}){
  const samples=[];let required=0;
  for(const actor of manifest.actors){
    const {request,loginStatus}=await createManagementClient({baseUrl,token,password,username:actor.username});
    if(loginStatus!==actor.loginStatus)throw Error('Scope actor login contract mismatch: '+actor.kind);
    const lists=[['application','/api/v1/applications',actor.applicationStatus,actor.applicationIds],['policy',`/api/v1/organizations/${actor.organizationId}/policies`,actor.policyStatus,actor.policyIds]];
    for(const [list,url,status,ids] of lists){
      const pages=status===200?[1,50,100].flatMap(pageSize=>{
        const last=Math.max(1,Math.ceil(ids.length/pageSize));
        return [['first',1],['middle',Math.floor(last/2)+1],['last',last],['out-of-range',last+1]].map(([pageKind,page])=>({pageKind,page,pageSize}));
      }):[{pageKind:'denied',page:1,pageSize:1}];
      for(const {pageKind,page,pageSize} of pages)for(let repeat=1;repeat<=(validation?1:3);repeat++){
        required++;const start=performance.now(),response=await request(url+`?page=${page}&pageSize=${pageSize}`),bytes=Buffer.from(await response.arrayBuffer()),durationMs=performance.now()-start;
        if(response.status!==status)throw Error(`Scope ${actor.kind}/${list} returned unexpected ${response.status}`);
        let total=null,actualIds=[];
        if(status===200){
          const value=JSON.parse(bytes);total=value.total;actualIds=value.items?.map(item=>item.id);
          const expected=ids.slice((page-1)*pageSize,page*pageSize);
          if(total!==ids.length||!actualIds||JSON.stringify(actualIds)!==JSON.stringify(expected))throw Error('Scope visible IDs/Total mismatch: '+actor.kind+'/'+list);
        }
        const id=response.headers.get('X-Performance-Request-Id');if(!id)throw Error('Scope SQL request association missing');
        const sqlResponse=await request('/__perf/sql/'+id);if(!sqlResponse.ok)throw Error('Scope SQL probe unavailable');const measured=await sqlResponse.json();
        samples.push({actor:actor.kind,list,scale:manifest.scale,pageKind,page,pageSize,repeat,status,total,ids:actualIds,responseContractHash:responseContractHash(bytes,status),responseBytes:bytes.length,responseHash:createHash('sha256').update(bytes).digest('hex'),durationMs,sql:measured.stats,shapes:measured.shapes,...explain?{explain:await collectExplain(request,id,measured)}:{}});
      }
    }
  }
  return {scale:manifest.scale,businessFingerprint:manifest.businessFingerprint,samples,requiredSamples:required,classificationComplete:samples.every(s=>s.sql.unclassified===0),complete:samples.length===required&&required>0};
}
