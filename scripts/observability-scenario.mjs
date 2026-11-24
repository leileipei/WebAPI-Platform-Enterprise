import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {LocalClient} from './local-client.mjs';

const managementActors = ['e2e-admin', 'e2e-approver', 'e2e-security'];
const fixtureActors = new Set([...managementActors, 'e2e-reader']);

function readActorPassword(directory, project, context, username) {
 assert(fixtureActors.has(username), 'Unknown observability fixture actor');
 const registered = context.passwordFiles?.[username];
 const filename = typeof registered === 'string' ? path.posix.basename(registered) : '';
 assert(typeof registered === 'string' && registered.startsWith('/workspace/') && path.posix.basename(path.posix.dirname(registered)) === project &&
  ['password', 'password-'+username].includes(filename), 'Invalid fixture password file reference');
 const file = path.join(directory, filename);
 assert(fs.lstatSync(file).isFile(), 'Fixture password file must be a regular file');
 const secret = fs.readFileSync(file, 'utf8');
 assert(secret.length > 0, 'Fixture password file is empty');
 return secret;
}
export const sleep=ms=>new Promise(r=>setTimeout(r,ms));
export async function wait(action,condition,seconds=120){const until=Date.now()+seconds*1000;let last;while(Date.now()<until){try{const value=await action();if(condition(value))return value;last=undefined;}catch(e){last=e;}await sleep(500);}throw last||Error('Actual observation did not converge');}
export async function scenario(){
 const directory=process.env.WEBAPI_E2E_DIRECTORY,project=process.env.WEBAPI_E2E_PROJECT;
 assert(project?.startsWith('webapi-enterprise-e2e-observability-')&&directory&&path.basename(directory)===project);
 const context=JSON.parse(fs.readFileSync(directory+'/context.json')),runtime=JSON.parse(fs.readFileSync(directory+'/observability-context.json')),endpoints=runtime.endpoints,credential=fs.readFileSync(directory+'/credential','utf8');
 const clients = managementActors.map(() => new LocalClient(endpoints.control_plane));
 const [admin, approver, security] = clients;
 async function authenticateFixtureActor(client, username) {
  const password = readActorPassword(directory, project, context, username);
  client.cookies.clear();
  const identity = await client.login(username, password);
  assert.equal(identity.mustChangePassword, false, 'Dedicated fixture must complete its initial password change before observation');
  return client;
 }
 const loginActor = username => authenticateFixtureActor(new LocalClient(endpoints.control_plane), username);
 async function loginManagementActors() {
  for (const [index, name] of managementActors.entries()) await authenticateFixtureActor(clients[index], name);
 }
 await loginManagementActors();
 const reauthenticate = async () => {
  await wait(() => fetch(endpoints.control_plane+'/health/live').then(response => response.ok), value => value, 30);
  await loginManagementActors();
 };
 const compose=(...args)=>execFileSync('docker',['compose','-f','deploy/compose.e2e.yml','-f','deploy/compose.observability.yml','-f','deploy/compose.observability-e2e.yml',...args],{env:process.env,encoding:'utf8',stdio:['pipe','pipe','pipe']});
 const sql=query=>compose('exec','-T','postgres','psql','-U','webapi','-d','webapi','-At','-v','ON_ERROR_STOP=1','-c',query).trim();
 const scope={organizationId:context.organizationId,projectId:context.projectId,environmentId:context.environmentId};
 const range=(seconds=60,end=Date.now())=>({...scope,start:new Date(end-seconds*1000).toISOString(),end:new Date(end).toISOString()});
 const query=(path,params={})=>admin.request('/observability/'+path+'?'+new URLSearchParams({...range(),...params}));
 const request=async(node=0,path='/orders',trace,authorized=true,timeoutMs=10000)=>{const response=await fetch((node===0?endpoints.gateway_a:endpoints.gateway_b)+path,{headers:{'X-API-Key':authorized?credential:'synthetic-invalid',...(trace?{traceparent:`00-${trace}-0123456789abcdef-01`}:{})},signal:AbortSignal.timeout(timeoutMs)});const text=await response.text();let value;try{value=JSON.parse(text);}catch{}return{status:response.status,value};};
 const backend=async(path,method='POST')=>{const response=await fetch(endpoints.backend_a+path,{method,signal:AbortSignal.timeout(5000)});assert(response.ok,'Backend test-control request failed '+response.status);return response.headers.get('content-type')?.includes('application/json')?response.json():null;};
 async function list(resource) {
  const items = [];
  const pageSize = 100;
  const separator = resource.includes('?') ? '&' : '?';
  for (let page = 1; page <= 1000; page++) {
   const result = await admin.request(resource+separator+'page='+page+'&pageSize='+pageSize);
   assert.equal(result.page, page, 'Unexpected fixture collection page');
   assert.equal(result.pageSize, pageSize, 'Unexpected fixture collection page size');
   assert(Number.isInteger(result.total) && result.total >= 0 && Array.isArray(result.items), 'Invalid fixture collection');
   items.push(...result.items);
   if (page * pageSize >= result.total) return items;
  }
  assert.fail('Observability fixture collection exceeded its bounded page limit');
 }
 async function publish(backendId, environmentId = context.environmentId, clusterId = context.clusterId) {
  assert(['A', 'B'].includes(backendId), 'Unknown observability fixture backend');
  const versionPath = '/apis/'+context.apiId+'/versions';
  const versions = await admin.request(versionPath);
  const next = Math.max(0, ...versions.map(v => Number(v.version.split('.')[0]) || 0)) + 1;
  const cluster = await admin.request('/clusters/'+clusterId);
  assert.equal(cluster.environmentId, environmentId, 'Fixture cluster has a different environment');
  const destinationName = environmentId === context.environmentId ? 'test-backend' : 'foreign-owned-destination';
  const destinations = cluster.destinations.filter(destination => destination.name === destinationName);
  assert.equal(destinations.length, 1, 'Expected a single owned fixture destination');
  const [destination] = destinations;
  const routePath = '/environments/'+environmentId+'/routes';
  const versionIds = new Set(versions.map(v => v.id));
  const matchingRoutes = (await list(routePath)).filter(route => route.routeName === 'Orders' && versionIds.has(route.apiVersionId));
  assert(matchingRoutes.length <= 1, 'Expected at most one owned Orders route');
  const [route] = matchingRoutes;
  if (route) assert.equal(route.clusterId, clusterId, 'Owned Orders route references a different cluster');

  const version = await admin.request(versionPath, {method:'POST', body:{version:next+'.0.0'}});
  await admin.request('/destinations/'+destination.id, {method:'PUT', headers:{'If-Match':`"${destination.revision}"`},
   body:{name:destination.name,address:`http://backend-${backendId.toLowerCase()}:8080/`,weight:1,enabled:true}});
  const body = {apiVersionId:version.id,routeName:'Orders',path:'/orders/{**path}',methods:['GET'],clusterId,priority:100,enabled:true,timeoutMs:30000};
  if (route) await admin.request('/routes/'+route.id, {method:'PUT',body,headers:{'If-Match':`"${route.revision}"`}});
  else await admin.request(routePath, {method:'POST',body});

  const env = await admin.request('/environments/'+environmentId);
  const selection = {baseConfigVersion:env.desiredConfigVersion??0,versionIds:[version.id]};
  const releases = '/environments/'+environmentId+'/releases';
  const preview = await admin.request(releases+'/preview', {method:'POST',body:selection});
  const release = await admin.request(releases, {method:'POST',body:{...selection,resourceRevisions:preview.resourceRevisions}});
  return publishApproved(release.id);
 }
 async function approve(id) {
  const resource = '/releases/'+id;
  const submitted = await admin.request(resource+'/submit', {method:'POST'});
  assert.equal(submitted.state, 'WaitingApproval');
  assert.match(submitted.candidateHash, /^[a-f0-9]{64}$/, 'Release must have a frozen candidate hash');
  const reviewers = [
   {client:approver, step:1, role:'ApiApprover', comment:'独立观测闭环审核'},
   {client:security, step:2, role:'SecurityReviewer', comment:'独立安全审核'},
  ];
  let decided;
  for (const {client, step, comment} of reviewers) {
   const detail = await client.request(resource);
   assert.equal(detail.state, 'WaitingApproval');
   assert.equal(detail.approvalEligibility?.canAct, true);
   assert.equal(detail.approvalEligibility.currentStepOrder, step);
   assert.equal(detail.candidateHash, submitted.candidateHash);
   decided = await client.request(resource+'/approve', {method:'POST',body:{
    comment, expectedStepOrder:step, expectedCandidateHash:detail.candidateHash,
   }});
  }
  assert.equal(decided.state, 'Ready');
  const approved = decided.approvalSteps.filter(task => task.status === 'Approved');
  assert.deepEqual(approved.map(task => [task.stepOrder, task.roleCode]), reviewers.map(({step, role}) => [step, role]));
  assert(approved.every(task => task.actorId) && new Set(approved.map(task => task.actorId)).size === approved.length,
   'Expected distinct fixture approval actors');
 }
 async function publishApproved(id) {
  await approve(id);
  await admin.request('/releases/'+id+'/publish', {method:'POST'});
  return wait(() => admin.request('/releases/'+id), release => {
   assert.notEqual(release.state, 'Failed');
   return release.state==='Succeeded' && release.targets.length===2 && release.targets.every(target=>target.acknowledged);
  }, 45);
 }
 async function rollback(release, version) {
  const draft = await admin.request('/releases/'+release.id+'/rollback', {method:'POST',body:{targetConfigVersion:version}});
  return publishApproved(draft.id);
 }
 const raw=async(query,time)=>{const response=await fetch(endpoints.prometheus+'/api/v1/query?'+new URLSearchParams({query,...(time?{time:String(time)}:{})}),{signal:AbortSignal.timeout(10000)});assert(response.ok);const result=await response.json();assert.equal(result.status,'success');return result.data.result;};
 const metric=(data,key)=>data.data.kpis.find(v=>v.metric===key);
 const createRule=(name,metric,threshold,forSeconds=4)=>admin.request('/observability/alert-rules',{method:'POST',body:{...scope,name,metric,expression:metric+' > '+threshold,severity:'Warning',enabled:true,forSeconds,targetType:'Environment',targetId:null,windowSeconds:60,notification:{inConsole:true,requestedChannels:[]}}});
 const eventFor=async(rule)=>{const page=await query('alerts',{pageSize:100});return page.items.find(e=>e.ruleId===rule.id&&e.logicRevision===rule.logicRevision);};
 const action=async(event,kind,reason)=>{const current=await admin.request('/observability/alerts/'+event.id);return admin.request('/observability/alerts/'+event.id+'/'+kind.toLowerCase(),{method:'POST',headers:{'If-Match':`"${current.revision}"`},body:{kind,reason,...(kind==='Silence'?{durationSeconds:900}:{})}});};
 return {directory,project,context,endpoints,settings:runtime.settings,admin,clients,loginActor,reauthenticate,list,compose,sql,scope,range,query,request,backend,publish,approve,rollback,raw,metric,createRule,eventFor,action};
}
