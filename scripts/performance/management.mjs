import {createHash} from 'node:crypto';
import {performance} from 'node:perf_hooks';
import {summarize} from './report.mjs';
import {collectExplain} from './explain.mjs';
export function resourceId(seed,kind,index){const h=createHash('sha256').update(`${seed}:${kind}:${index}`).digest().subarray(0,16).toString('hex');return `${h.slice(0,8).match(/../g).reverse().join('')}-${h.slice(8,12).match(/../g).reverse().join('')}-${h.slice(12,16).match(/../g).reverse().join('')}-${h.slice(16,20)}-${h.slice(20)}`;}
export async function createManagementClient({baseUrl,token,password,username="performance_admin"}){
  const cookies=new Map();
  async function request(relative,{method='GET',body,csrf}={}){
    const headers={'X-Performance-Token':token};if(cookies.size)headers.Cookie=[...cookies].map(([name,value])=>`${name}=${value}`).join('; ');
    if(body!==undefined)headers['Content-Type']='application/json';if(csrf)headers['X-CSRF-Token']=csrf;
    const response=await fetch(new URL(relative,baseUrl),{method,headers,body:body===undefined?undefined:JSON.stringify(body),redirect:'manual',signal:AbortSignal.timeout(120000)});
    for(const cookie of response.headers.getSetCookie()){const pair=cookie.split(';')[0],at=pair.indexOf('=');cookies.set(pair.slice(0,at),pair.slice(at+1));}
    return response;
  }
  const csrf=await request('/api/v1/auth/csrf');if(!csrf.ok)throw Error('Real CSRF request failed');
  const login=await request('/api/v1/auth/login',{method:'POST',body:{username,password},csrf:(await csrf.json()).token});
  const problem=login.ok?{}:await login.json().catch(()=>({}));
  return {request,loginStatus:login.status,problemCode:problem.code??"unknown"};
}
export async function measureManagement({baseUrl,token,password,scale,seed,validation=false,explain=false}){
  const {request,loginStatus,problemCode}=await createManagementClient({baseUrl,token,password});
  if(loginStatus!==200)throw Error(`Real performance login failed: ${loginStatus} ${problemCode}`);
  const org=resourceId(seed,'organization',0),project=resourceId(seed,'project',0),env=resourceId(seed,'environment',0),node=resourceId(seed,'node',0),policy=resourceId(seed,'policy',0);
  const scope=`organizationId=${org}&projectId=${project}&environmentId=${env}`;
  const lists=[['api',`/api/v1/projects/${project}/apis`],['cluster',`/api/v1/environments/${env}/clusters`],['route',`/api/v1/environments/${env}/routes`],['application','/api/v1/applications'],['policy',`/api/v1/organizations/${org}/policies`],['user','/api/v1/users'],['node',`/api/v1/environments/${env}/gateway-nodes`],['node-event',`/api/v1/gateway-nodes/${node}/events`],['snapshot',`/api/v1/environments/${env}/snapshots`],['release',`/api/v1/environments/${env}/releases`],['alert-rule','/api/v1/observability/alert-rules/?'+scope],['alert-event','/api/v1/observability/alerts/?'+scope],['policy-reference',`/api/v1/policies/${policy}/references`]];
  const samples=[];
  for(const [list,url] of lists)for(const pageSize of [1,50,100]){
    const expectedTotal=list==='policy-reference'?scale+1:scale,last=Math.ceil(expectedTotal/pageSize);
    const pages=[['first',1],['middle',Math.floor(last/2)+1],['last',last],['out-of-range',last+1]];
    for(const [pageKind,page] of pages)for(let repeat=1;repeat<=(validation?1:3);repeat++){
      const target=url+(url.includes('?')?'&':'?')+`page=${page}&pageSize=${pageSize}`;
      const start=performance.now();const response=await request(target);const bytes=Buffer.from(await response.arrayBuffer());const durationMs=performance.now()-start;
      if(!response.ok)throw Error(`Management list ${list} returned unexpected ${response.status}`);
      const value=JSON.parse(bytes),expectedCount=Math.min(pageSize,Math.max(0,expectedTotal-(page-1)*pageSize));
      if(value.total!==expectedTotal||!Array.isArray(value.items)||value.items.length!==expectedCount)throw Error(`Management page contract mismatch: ${list}`);
      const id=response.headers.get('X-Performance-Request-Id');if(!id)throw Error('Actual SQL request association missing');
      const sqlResponse=await request('/__perf/sql/'+id);if(!sqlResponse.ok)throw Error('Actual SQL stats unavailable');const measured=await sqlResponse.json();
      const ids=value.items.map(item=>item.id??item.configVersion??(item.referenceKind?[item.referenceKind,item.environmentId,item.routeId,item.configVersion,item.name].join(':'):null));if(ids.some(id=>id===null))throw Error('List item identity unavailable');
      if(list==='release'&&value.items.some(item=>item.frozenCandidate?.versions?.length!==8||item.approvalSteps?.length!==2||item.targets?.length!==6))throw Error('Full release DTO missing relations');
      samples.push({list,scale,pageKind,page,pageSize,repeat,total:value.total,ids,responseBytes:bytes.length,responseHash:createHash('sha256').update(bytes).digest('hex'),durationMs,sql:measured.stats,shapes:measured.shapes,...explain?{explain:await collectExplain(request,id,measured)}:{}});
    }
  }
  return {scale,seed,samples,classificationComplete:samples.every(s=>s.sql.unclassified===0),summary:explain?null:lists.map(([list])=>({list,...summarize(samples.filter(s=>s.list===list).map(s=>s.durationMs))})),...explain?{measurementScope:'explain-diagnostic-not-performance'}:{},complete:samples.length===lists.length*3*4*(validation?1:3)};
}
