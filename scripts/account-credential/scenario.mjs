import fs from 'node:fs/promises';import path from 'node:path';import assert from 'node:assert/strict';import {randomBytes,randomUUID} from 'node:crypto';
import {assertFixedAccountSources} from './fixed-source.mjs';
import {validateCloneOwnership} from '../contracts/evidence.mjs';import {docker,inspectResources} from '../runtime/docker.mjs';import {loadState,ownedDirectory,safeFile,writePrivate} from '../runtime/state.mjs';import {loadRelease} from '../runtime/lifecycle.mjs';import {ContractClient} from '../contracts/scenario.mjs';
const sha=/^[a-f0-9]{40}$/,image=/^sha256:[a-f0-9]{64}$/,hash=/^[a-f0-9]{64}$/,uuid=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/;
export function assertAccountCredentialRuntime({state,directory,release,revision,imageId,resources}){
 assert(sha.test(revision)&&image.test(imageId),'Fixed runtime identity required');assert.equal(release.sourceRevision,revision);assert.equal(release.imageId,imageId);validateCloneOwnership(state,resources,directory);
 const names=['control-plane','worker','console','gateway-a','gateway-b'];for(const name of names){const containers=resources.filter(r=>r.Kind==='container'&&r.Config?.Labels?.['com.docker.compose.service']===name);assert.equal(containers.length,1,'Missing/duplicate owned '+name);const container=containers[0];assert.equal(container.Image,imageId);assert.equal(container.State?.Running,true);const networks=Object.keys(container.NetworkSettings?.Networks??{});assert(networks.includes(state.projectName+'_default'));assert(networks.every(n=>n.startsWith(state.projectName+'_')),'Foreign runtime network');const portName=({'console':'console','gateway-a':'gatewayA','gateway-b':'gatewayB'})[name];if(portName){const bindings=container.HostConfig?.PortBindings?.['8080/tcp'];assert(Array.isArray(bindings)&&bindings.length===1,'Missing or ambiguous published owner endpoint');assert.equal(bindings[0].HostIp,'127.0.0.1','Owner endpoint must be loopback');assert.equal(bindings[0].HostPort,String(state.ports[portName]),'Published endpoint does not match inspected owner');}}return true;
}
export function validateUsageCloneIsolation(proof){
 assert(proof&&uuid.test(proof.sourceOwnerId)&&uuid.test(proof.targetOwnerId));assert.notEqual(proof.sourceOwnerId,proof.targetOwnerId);assert.match(proof.originalProject,/^webapi-enterprise-local-test-[a-f0-9-]{36}$/);assert.match(proof.targetProject,/^webapi-enterprise-local-test-[a-f0-9-]{36}$/);assert.notEqual(proof.originalProject,proof.targetProject);assert(Array.isArray(proof.attachedNetworks)&&proof.attachedNetworks.length>0);assert(proof.attachedNetworks.every(n=>n.startsWith(proof.targetProject+'_')));assert(proof.attachedNetworks.includes(proof.targetProject+'_default'));assert.equal(proof.queueAction,'quarantined');assert.equal(proof.originalRequests,0);const url=new URL(proof.controlPlaneUrl);assert.equal(url.origin,'http://control-plane:8080');assert.equal(url.username+url.password+url.search+url.hash,'');return true;
}
export function validateSdkInput(input){
 assert(sha.test(input.revision)&&image.test(input.imageId)&&uuid.test(input.ownerId)&&hash.test(input.archiveSha256));assert.match(input.projectName,/^webapi-enterprise-local-test-[a-f0-9-]{36}$/);assert.equal(input.network,input.projectName+'_default');for(const value of [input.sourceManifest,input.context])for(const name of ['sourceRevision','imageId','ownerId','projectName'])assert.equal(value?.[name],input[name==='sourceRevision'?'revision':name]);assert.equal(input.sourceManifest.archiveSha256,input.archiveSha256);return true;
}
export async function readOwnedRuntime({revision,imageId,fixtureDirectory}){
 assert(sha.test(revision)&&image.test(imageId));await ownedDirectory(fixtureDirectory);const state=await loadState(fixtureDirectory),release=await loadRelease(path.join(fixtureDirectory,'release.json')),resources=await inspectResources(state);assertAccountCredentialRuntime({state,directory:fixtureDirectory,release,revision,imageId,resources});const actual=JSON.parse((await docker(['image','inspect',imageId])).stdout)[0];assert.equal(actual.Id,imageId);assert.equal(actual.Config.Labels['org.opencontainers.image.revision'],revision);return{state,release,resources,image:actual};
}
export async function readPrivateArtifact(root,name){
 assert(typeof name==='string'&&!path.isAbsolute(name)&&name.split('/').every(p=>p&&p!=='.'&&p!=='..')&&!name.includes('\\'));const base=await fs.realpath(root);let target=base;for(const part of name.split('/')){target=path.join(target,part);assert(!(await fs.lstat(target)).isSymbolicLink(),'Symlink private input');}const stat=await fs.lstat(target);assert(stat.isFile()&&stat.size<=16*1024*1024);await safeFile(target);return fs.readFile(target);
}
export async function createScenarioAccount(admin,context,{username,initialPassword}){
 let user=await admin.request('/users',{method:'POST',body:{username,displayName:'独立账号验收',password:initialPassword}});assert.equal(user.mustChangePassword,true);
 const roles=await admin.request('/roles'),role=roles.find(r=>r.code==='ApiDeveloper'&&r.isSystem);assert(role,'Missing real fixture role');
 user=await admin.request(`/users/${user.id}/roles`,{method:'PUT',headers:{'If-Match':`"${user.revision}"`},body:{roleIds:[role.id]}});
 await admin.request(`/users/${user.id}/scopes`,{method:'PUT',headers:{'If-Match':`"${user.revision}"`},body:{scopes:[{scope:{organizationId:context.organizationId,projectId:context.projectId},accessMode:'read'}]}});
 return user;
}
export function resetScenarioPassword(admin,user,temporaryPassword){
 return admin.request(`/users/${user.id}/password-reset`,{method:'POST',headers:{'If-Match':`"${user.revision}"`},body:{temporaryPassword}});
}
// This executable account portion records real requests. Additional fault, migration, UI and SDK
// material is required by acceptance; this function never invents their outcomes or marks them passed.
export async function runAccountCredentialScenario({revision,imageId,directory,fixtureDirectory}){
 await assertFixedAccountSources(revision);const runtime=await readOwnedRuntime({revision,imageId,fixtureDirectory});await ownedDirectory(directory);const {state}=runtime;
 const context=JSON.parse(await readPrivateArtifact(fixtureDirectory,'account-credential-context.json'));for(const name of ['sourceRevision','imageId','ownerId','projectName'])assert.equal(context[name],name==='sourceRevision'?revision:name==='imageId'?imageId:state[name]);
 const admin=new ContractClient('http://127.0.0.1:'+state.ports.console),password=(await readPrivateArtifact(fixtureDirectory,context.adminPasswordFile)).toString();const adminIdentity=await admin.login(context.adminUsername,password);assert.notEqual(adminIdentity.mustChangePassword,true,'Fixture admin must explicitly complete initial change before scenario setup');
 const values=[password],records=[];const note=(name,value)=>records.push({name,value,observedAt:new Date().toISOString()});
 const initial=randomBytes(32).toString('base64url')+'Aa1!',changed=randomBytes(32).toString('base64url')+'Bb2!',reset=randomBytes(32).toString('base64url')+'Cc3!',afterReset=randomBytes(32).toString('base64url')+'Dd4!';values.push(initial,changed,reset,afterReset);
 const username='p4-runtime-'+randomUUID().replaceAll('-',''),user=await createScenarioAccount(admin,context,{username,initialPassword:initial});
 const local=new ContractClient(admin.base),restricted=await local.login(username,initial);assert.equal(restricted.mustChangePassword,true);assert.deepEqual(restricted.permissions,[]);note('newLocalRequiresChange',true);
 let denied;try{await local.request('/organizations');}catch(error){denied=error;}assert.equal(denied?.status,403);assert.equal(denied.code,'password_change_required');note('restrictedBusinessStatus',denied.status);
 const normal=await local.request('/auth/change-password',{method:'POST',body:{currentPassword:initial,newPassword:changed,confirmPassword:changed}});assert.equal(normal.mustChangePassword,false);assert(normal.scopes.length>0);note('scopeRetainedAfterChange',normal.scopes.length);await local.request('/organizations');
 const users=await admin.request('/users?page=1&pageSize=100');const current=users.items.find(u=>u.id===user.id);assert(current,'Fixture account not found in bounded page; narrow fixture setup');
 const result=await resetScenarioPassword(admin,current,reset);assert.equal(result.mustChangePassword,true);
 let expired;try{await local.request('/auth/me');}catch(error){expired=error;}assert.equal(expired?.status,401);note('resetInvalidatesOldSessionStatus',expired.status);
 const replacement=new ContractClient(admin.base);assert.equal((await replacement.login(username,reset)).mustChangePassword,true);assert.equal((await replacement.request('/auth/change-password',{method:'POST',body:{currentPassword:reset,newPassword:afterReset,confirmPassword:afterReset}})).mustChangePassword,false);note('resetRequiresAnotherChange',true);
 await writePrivate(path.join(directory,'private-values.json'),values);const proof={schemaVersion:1,sourceRevision:revision,imageId,ownerId:state.ownerId,projectName:state.projectName,kind:'runtime-account-flow',records,finishedAt:new Date().toISOString(),remainingRequiredEvidence:['migration-and-legacy-accounts','SSO','usage-faults-and-recovery','cross-environment-rotation','1440px-UI','full-SDK','original-protection']};await writePrivate(path.join(directory,'account-flow.json'),proof);return proof;
}
