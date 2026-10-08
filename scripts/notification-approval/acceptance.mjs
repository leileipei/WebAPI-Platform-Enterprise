import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {loadState,ownedDirectory,writePrivate} from '../runtime/state.mjs';
import {loadRelease} from '../runtime/lifecycle.mjs';
import {docker,inspectResources} from '../runtime/docker.mjs';
import {validateCloneOwnership} from '../contracts/evidence.mjs';
import {validateNotificationApprovalEvidence} from './evidence.mjs';
const sha=bytes=>createHash('sha256').update(bytes).digest('hex');
const services=['control-plane','worker','console','gateway-a','gateway-b'];
// Read only exact manifest paths. Never recursively copy a runtime directory:
// its actor context, database rows and credentials are private inputs.
export async function readEvidenceFile(directory,name){
 assert(typeof name==='string'&&/^[a-zA-Z0-9_./-]+$/.test(name)&&!path.isAbsolute(name)&&name.split('/').every(p=>p&&p!=='.'&&p!=='..'),'Invalid evidence path');
 const root=await fs.realpath(directory);assert(!(await fs.lstat(directory)).isSymbolicLink());
 for(const [index] of name.split('/').entries()){const file=path.join(root,...name.split('/').slice(0,index+1)),stat=await fs.lstat(file);assert(!stat.isSymbolicLink(),'Evidence symlink forbidden');if(index===name.split('/').length-1)assert(stat.isFile());else assert(stat.isDirectory());}
 const content=await fs.readFile(path.join(root,name));return {path:name,type:'file',content,sha256:sha(content)};
}
export async function collectNotificationApprovalEvidence({notificationDirectory,approvalDirectory,recoveryDirectory,suiteDirectory,observationDirectory,options}){
 const files=[];const add=file=>{assert(!files.some(f=>f.path===file.path));files.push(file);};
 async function module(prefix,directory){const wrapper=await readEvidenceFile(directory,'evidence.json'),proof=JSON.parse(wrapper.content);add({...wrapper,path:prefix+'/evidence.json'});assert(Array.isArray(proof.manifest));for(const entry of proof.manifest){assert(entry.path!=='evidence.json');const file=await readEvidenceFile(directory,entry.path);assert.equal(file.sha256,entry.sha256);add({...file,path:prefix+'/'+file.path});}return proof;}
 await module('notifications',notificationDirectory);await module('approvals',approvalDirectory);
 for(const [destination,name] of [['recovery/receipt.json','recovery-receipt.json'],['recovery/finally.json','finally.json'],['recovery/bridge-observation.json','bridge-observation.json'],['recovery/backup-manifest.json','cold-backup/backup-manifest.json']])add({...await readEvidenceFile(recoveryDirectory,name),path:destination});
 for(const name of ['images.json','static.json','regression.json','gateway-regression.json','sso-regression.json'])add(await readEvidenceFile(observationDirectory,name));
 const suites=[];for(const name of ['domain','integration','gateway','console','runtime','features']){const summary=await readEvidenceFile(suiteDirectory,name+'.json'),value=JSON.parse(summary.content);assert(value.name===name&&value.log==='suites/'+name+'.log'&&!Object.hasOwn(value,'artifact'));add({...summary,path:'suites/'+name+'.json'});add({...await readEvidenceFile(suiteDirectory,name+'.log'),path:value.log});suites.push({...value,artifact:'suites/'+name+'.json'});}
 const proof={schemaVersion:1,sourceRevision:options.revision,imageId:options.imageId,stage:'pre-install',originalInstallation:'not-upgraded',references:{notifications:'notifications/evidence.json',approvals:'approvals/evidence.json',recovery:'recovery/receipt.json',finally:'recovery/finally.json',images:'images.json',static:'static.json',regression:'regression.json',gatewayRegression:'gateway-regression.json',ssoRegression:'sso-regression.json'},suites,manifest:files.map(({path,sha256})=>({path,sha256}))};
 return {proof,files};
}
// Live inventory is freshly read and reduced to public identity fields. Docker
// Config.Env, mounts and private host paths are never published.
export async function observeNotificationApprovalRuntime({cloneDirectory,revision,imageId}){
 const state=await loadState(cloneDirectory),resources=await inspectResources(state);validateCloneOwnership(state,resources,cloneDirectory);
 const release=await loadRelease(path.join(cloneDirectory,'release.json'));assert.equal(release.sourceRevision,revision);assert.equal(release.imageId,imageId);
 const apps=resources.filter(r=>r.Kind==='container'&&services.includes(r.Config?.Labels?.['com.docker.compose.service']));assert.equal(apps.length,5);
 for(const app of apps)assert(app.State.Running&&app.Image===imageId);
 const image=JSON.parse((await docker(['image','inspect',imageId])).stdout)[0];assert.equal(image.Id,imageId);assert.equal(image.Config.Labels['org.opencontainers.image.revision'],revision);assert.equal(image.Config.User,'10001:10001');
 const identity={sourceRevision:revision,imageId},actual={};for(const [file,expected] of Object.entries(release.staticFiles)){assert(file.startsWith('console/dist/'));const response=await fetch('http://127.0.0.1:'+state.ports.console+'/'+file.slice('console/dist/'.length));assert(response.ok);actual[file]=sha(Buffer.from(await response.arrayBuffer()));assert.equal(actual[file],expected);}
 return {images:{...identity,ownerId:state.ownerId,projectName:state.projectName,resources:apps.map(r=>({Kind:r.Kind,Id:r.Id,Image:r.Image,State:{Running:r.State.Running},Config:{Labels:Object.fromEntries(['com.docker.compose.service','com.docker.compose.project','com.webapi.runtime.owner'].map(k=>[k,r.Config.Labels[k]]))}})),images:[{Id:image.Id,Config:{User:image.Config.User,Labels:{'org.opencontainers.image.revision':revision}}}]},static:{...identity,expected:release.staticFiles,actual}};
}
export async function sealNotificationApprovalEvidence({proof,files,options,directory}){
 const result=validateNotificationApprovalEvidence(proof,files,options);assert(result.passed,result.errors.join('; '));
 // Validate before creating the destination. A failed gate must leave no
 // complete artifact, and an existing destination must never be replaced.
 await fs.mkdir(directory,{mode:0o700});await ownedDirectory(directory);
 for(const file of files){await fs.mkdir(path.dirname(path.join(directory,file.path)),{recursive:true,mode:0o700});await fs.writeFile(path.join(directory,file.path),file.content,{flag:'wx',mode:0o600});}
 for(const file of files)assert.equal((await readEvidenceFile(directory,file.path)).sha256,file.sha256);
 await writePrivate(path.join(directory,'evidence.json'),proof);await writePrivate(path.join(directory,'manifest.json'),{schemaVersion:1,sourceRevision:options.revision,imageId:options.imageId,stage:'pre-install',complete:true,originalInstallation:'not-upgraded',files:[...proof.manifest,{path:'evidence.json',sha256:sha(await fs.readFile(path.join(directory,'evidence.json')))}]});return result;
}
export async function runNotificationApprovalAcceptance({directory,cloneDirectory,options}){
 await ownedDirectory(directory);const proof=JSON.parse((await readEvidenceFile(directory,'evidence.json')).content),files=[];
 for(const entry of proof.manifest){const file=await readEvidenceFile(directory,entry.path);assert.equal(file.sha256,entry.sha256);files.push(file);}
 const observed=await observeNotificationApprovalRuntime({cloneDirectory,revision:options.revision,imageId:options.imageId}),read=name=>JSON.parse(files.find(f=>f.path===name).content);
 assert.deepEqual(read(proof.references.images),observed.images,'Inventory is not the currently running clone');assert.deepEqual(read(proof.references.static),observed.static);
 return validateNotificationApprovalEvidence(proof,files,options);
}
