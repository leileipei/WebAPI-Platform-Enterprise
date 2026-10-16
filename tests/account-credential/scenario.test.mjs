import {test} from 'node:test';import assert from 'node:assert/strict';import {randomUUID} from 'node:crypto';import {mkdtemp,rm,writeFile} from 'node:fs/promises';import {tmpdir} from 'node:os';import path from 'node:path';
import http from 'node:http';import {ContractClient} from '../../scripts/contracts/scenario.mjs';
const implementation=await import('../../scripts/account-credential/scenario.mjs').catch(e=>{if(e.code!=='ERR_MODULE_NOT_FOUND')throw e;return {};});
const requireApi=name=>{assert.equal(typeof implementation[name],'function',`missing approved ${name} behavior`);return implementation[name];};
function state(){const owner=randomUUID(),project='webapi-enterprise-local-test-'+randomUUID();return{ownerId:owner,projectName:project,ports:{console:55101,gatewayA:55102,gatewayB:55103}};}
const revision='a'.repeat(40),imageId='sha256:'+'b'.repeat(64);
function runtime(s){return['control-plane','worker','console','gateway-a','gateway-b'].map((service,i)=>({Kind:'container',Id:String(i+1).padStart(64,'0'),Image:imageId,State:{Running:true},Config:{Labels:{'com.docker.compose.project':s.projectName,'com.webapi.runtime.owner':s.ownerId,'com.docker.compose.service':service}},HostConfig:{PortBindings:{'8080/tcp':[{HostIp:'127.0.0.1',HostPort:String(service==='console'?s.ports.console:service==='gateway-a'?s.ports.gatewayA:s.ports.gatewayB)}]}},NetworkSettings:{Networks:{[s.projectName+'_default']:{}}}}));}
test('fixed owner identity accepts only same-source running containers on the owner network',()=>{const check=requireApi('assertAccountCredentialRuntime');const s=state();assert.equal(check({state:s,directory:path.join(tmpdir(),s.projectName),release:{sourceRevision:revision,imageId},revision,imageId,resources:runtime(s)}),true);});
test('original runtime and foreign resources cannot be adopted or cleaned',()=>{const check=requireApi('assertAccountCredentialRuntime');const s=state();const base={state:s,directory:path.join(tmpdir(),s.projectName),release:{sourceRevision:revision,imageId},revision,imageId,resources:runtime(s)};for(const mutate of [x=>x.state.projectName='webapi-enterprise-local',x=>x.state.ports.console=4192,x=>x.resources[0].Config.Labels['com.webapi.runtime.owner']=randomUUID(),x=>x.resources[0].Image='sha256:'+'c'.repeat(64),x=>x.resources[0].NetworkSettings.Networks.foreign={}]){const value=structuredClone(base);mutate(value);assert.throws(()=>check(value));}});
test('a restored queue cannot be connected to the original control plane network',()=>{const check=requireApi('validateUsageCloneIsolation');const source=state(),target=state();const proof={sourceOwnerId:source.ownerId,targetOwnerId:target.ownerId,originalProject:source.projectName,targetProject:target.projectName,attachedNetworks:[target.projectName+'_default'],controlPlaneUrl:'http://control-plane:8080',queueAction:'quarantined',originalRequests:0};assert.equal(check(proof),true);for(const patch of [{attachedNetworks:[source.projectName+'_default']},{targetOwnerId:source.ownerId},{queueAction:'replayed'},{originalRequests:1},{controlPlaneUrl:'http://127.0.0.1:4192'}])assert.throws(()=>check({...proof,...patch}));});
test('SDK launch cannot substitute live source or a different context owner',async()=>{const check=requireApi('validateSdkInput');const s=state();const value={revision,imageId,ownerId:s.ownerId,projectName:s.projectName,archiveSha256:'c'.repeat(64),sourceManifest:{sourceRevision:revision,imageId,ownerId:s.ownerId,projectName:s.projectName,archiveSha256:'c'.repeat(64)},context:{sourceRevision:revision,imageId,ownerId:s.ownerId,projectName:s.projectName},network:s.projectName+'_default'};assert.equal(check(value),true);assert.throws(()=>check({...value,context:{...value.context,sourceRevision:'d'.repeat(40)}}));assert.throws(()=>check({...value,network:'webapi-enterprise-local_default'}));});
test('scenario refuses missing material evidence before making runtime mutations',async()=>{const run=requireApi('runAccountCredentialScenario');const directory=await mkdtemp(path.join(tmpdir(),'p4-candidate-no-evidence-'));try{await assert.rejects(run({revision,imageId,directory,fixtureDirectory:path.join(directory,'missing')}),/source|runtime|state|ENOENT|directory/i);}finally{await rm(directory,{recursive:true,force:true});}});

test('published console endpoint must belong to the inspected owner, not merely its state file',()=>{const check=requireApi('assertAccountCredentialRuntime'),s=state(),resources=runtime(s);resources.find(x=>x.Config.Labels['com.docker.compose.service']==='console').HostConfig.PortBindings['8080/tcp'][0].HostPort='4192';assert.throws(()=>check({state:s,directory:path.join(tmpdir(),s.projectName),release:{sourceRevision:revision,imageId},revision,imageId,resources}));});

// Actual loopback HTTP protocol checks. This minimal server implements the public
// UserDto / ScopeGrantDto[] / password-reset wire contract, not a live CP acceptance.
async function accountProtocol(action){
 const userId=randomUUID(),roleId=randomUUID(),scope={organizationId:randomUUID(),projectId:randomUUID()},requests=[],password=' Temporary contract password Aa1! ';
 const server=http.createServer(async(req,res)=>{
  const chunks=[];for await(const bytes of req)chunks.push(bytes);const raw=Buffer.concat(chunks).toString();const body=raw?JSON.parse(raw):undefined;
  const item={method:req.method,path:req.url,body,etag:req.headers['if-match'],csrf:req.headers['x-csrf-token'],key:req.headers['idempotency-key']};requests.push(item);
  res.setHeader('Content-Type','application/json');let value;
  if(req.url==='/api/v1/auth/csrf'&&req.method==='GET')value={token:'protocol-csrf'};
  else if(req.url==='/api/v1/users'&&req.method==='POST')value={id:userId,username:body.username,revision:1,mustChangePassword:true};
  else if(req.url==='/api/v1/roles'&&req.method==='GET')value=[{id:roleId,code:'ApiDeveloper',isSystem:true}];
  else if(req.url===`/api/v1/users/${userId}/roles`&&req.method==='PUT')value={id:userId,username:'fixture',revision:2,mustChangePassword:true};
  else if(req.url===`/api/v1/users/${userId}/scopes`&&req.method==='PUT')value=[{scope,accessMode:'read'}];
  else if(req.url===`/api/v1/users/${userId}/password-reset`&&req.method==='POST')value={id:userId,revision:8,mustChangePassword:true};
  else{res.statusCode=404;value={code:'not_found',message:'Unknown public account endpoint'};}
  res.end(JSON.stringify(value));
 });
 await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
 try{return await action({client:new ContractClient('http://127.0.0.1:'+server.address().port),userId,roleId,scope,password,requests});}
 finally{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
}
test('HTTP account preparation retains UserDto identity when scopes PUT returns ScopeGrantDto array',async()=>{
 await accountProtocol(async({client,userId,roleId,scope,password,requests})=>{
  const user=await requireApi('createScenarioAccount')(client,scope,{username:'fixture',initialPassword:password});
  assert.equal(user.id,userId);assert.equal(user.mustChangePassword,true);
  const writes=requests.filter(r=>r.method!=='GET');assert.equal(writes.length,3);
  assert.equal(writes[0].body.password,password);assert.deepEqual(writes[1].body,{roleIds:[roleId]});assert.equal(writes[1].etag,'"1"');
  assert.deepEqual(writes[2].body,{scopes:[{scope,accessMode:'read'}]});assert.equal(writes[2].etag,'"2"');
  for(const write of writes){assert.equal(write.csrf,'protocol-csrf');assert.match(write.key,/^[a-f0-9-]{36}$/);}
 });
});
test('HTTP password reset uses the public endpoint, revision and unmodified temporary password',async()=>{
 await accountProtocol(async({client,userId,password,requests})=>{
  const result=await requireApi('resetScenarioPassword')(client,{id:userId,revision:7},password);assert.equal(result.mustChangePassword,true);
  const reset=requests.find(r=>r.method==='POST');assert.equal(reset.path,`/api/v1/users/${userId}/password-reset`);assert.equal(reset.etag,'"7"');assert.deepEqual(reset.body,{temporaryPassword:password});assert.equal(reset.csrf,'protocol-csrf');assert.match(reset.key,/^[a-f0-9-]{36}$/);
 });
});
