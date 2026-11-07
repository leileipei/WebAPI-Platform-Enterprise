import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID,createHash} from 'node:crypto';
import {initializeState} from '../../scripts/runtime/state.mjs';
import {writeScenarioProof} from '../../scripts/delivery/scenario-evidence.mjs';
import {cleanupPipelineContext} from '../../scripts/delivery/pipeline-runtime.mjs';
const reviewed=await import('../../scripts/delivery/reviewed-cleanup.mjs').catch(error=>{if(error.code==='ERR_MODULE_NOT_FOUND'&&error.url?.endsWith('/scripts/delivery/reviewed-cleanup.mjs'))return {};throw error;});

const root=path.resolve('.superpowers/sdd/reviewed-cleanup-test-fixtures');
const sha=value=>createHash('sha256').update(value).digest('hex');
async function fixture(changeProof=proof=>proof){
 await fs.mkdir(root,{recursive:true,mode:0o700});const parent=await fs.mkdtemp(path.join(root,'run-'));console.log('retained-review-fixture='+parent);
 const states=[];for(let i=0;i<2;i++){const projectName='webapi-enterprise-local-test-'+randomUUID(),directory=path.join(parent,projectName),state=await initializeState(directory,{projectName,bootstrapUsername:'fixture-admin'});await fs.writeFile(path.join(directory,'release.json'),JSON.stringify({schemaVersion:1,configVersion:1,imageId:'sha256:'+'a'.repeat(64),sourceRevision:'b'.repeat(40)}),{mode:0o600});await fs.writeFile(path.join(directory,'migration-evidence-provision.json'),JSON.stringify({schemaVersion:1,ownerId:state.ownerId,projectName,volumeName:projectName+'_migration-evidence',nonce:'c'.repeat(64),phase:'Ready'}),{mode:0o600});states.push({directory,state});}
 const [source,clone]=states,evidence=path.join(parent,'proof');await fs.mkdir(evidence,{mode:0o700});
 const retained=({directory,state})=>({retainedForReview:true,ownerVerified:true,directoryRetained:true,directory,projectName:state.projectName,ownerId:state.ownerId});
 const proof={schemaVersion:2,classification:'actual-isolated-cross-environment-promotion',sourceRevision:'b'.repeat(40),sourceVerified:true,build:{imageId:'sha256:'+'a'.repeat(64),sourceRevision:'b'.repeat(40)},isolatedAcceptance:false,executionPassed:true,phase:'awaiting-retained-fixture-review',projectName:source.state.projectName,cleanup:retained(source),backupRestore:{passed:true,actual:true,sourceEvidenceSupported:true,sourceOwnerId:source.state.ownerId,ownerId:clone.state.ownerId,cloneProjectName:clone.state.projectName,cloneDirectory:clone.directory,cloneCleanup:retained(clone)}};
 await writeScenarioProof(evidence,changeProof(proof));const resources=new Map(states.map(({state})=>{const labels={'com.docker.compose.project':state.projectName,'com.webapi.runtime.owner':state.ownerId};return[state.ownerId,[{Kind:'volume',Name:state.projectName+'_migration-evidence',Driver:'local',Scope:'local',Options:{},Labels:{...labels,'com.docker.compose.volume':'migration-evidence','com.webapi.migration.evidence-nonce':'c'.repeat(64)}},{Kind:'container',Id:(state.ownerId.replaceAll('-','')+'d'.repeat(32)).slice(0,64),Config:{Labels:labels},State:{Running:true}}]];}));
 const calls=[];const deps={inspectResources:async state=>resources.get(state.ownerId).map(r=>structuredClone(r)),captureOriginalRuntime:async()=>({stable:true}),cleanupPromotionContext:async(directory,_release,options)=>{const item=states.find(s=>s.directory===directory);calls.push(item.state.ownerId);assert.equal(options.expectedOwner,item.state.ownerId);resources.set(item.state.ownerId,resources.get(item.state.ownerId).filter(r=>r.Kind==='volume'));return{retainedForEvidence:true,ownerVerified:true,identityVerified:true,directoryRetained:true,directory,projectName:item.state.projectName,ownerId:item.state.ownerId,containersRemaining:0,volumesRemaining:1,remainingOwnedResources:1,secretFilesRemaining:0,retainedVolumeNames:[item.state.projectName+'_migration-evidence']};}};
 return{parent,source,clone,evidence,proof,resources,calls,deps};
}
test('a self-asserted proof only yields a blocked read-only review',async()=>{
 const f=await fixture(),proofPath=path.join(f.evidence,'proof.json'),before=sha(await fs.readFile(proofPath));f.deps.captureOriginalRuntime=async()=>{throw Error('must not inspect cwd-relative runtime');};
 const plan=await reviewed.prepareReviewedCleanup({evidenceDirectory:f.evidence},f.deps);assert.equal(plan.status,'Blocked');assert.equal(plan.canExecute,false);assert.equal(plan.executionQualification,'Unknown');assert.equal(plan.cloneRestoreQualification,'Unknown');assert.equal(plan.originalEnvironmentQualification,'Unknown');assert.equal(plan.formalAcceptance,false);assert.equal(sha(await fs.readFile(proofPath)),before);assert.equal(f.calls.length,0);
});
test('execution is blocked even with the preview digest and invokes no cleanup',async()=>{
 const f=await fixture(),plan=await reviewed.prepareReviewedCleanup({evidenceDirectory:f.evidence},f.deps),digest=reviewed.digestReviewPlan(plan);await assert.rejects(reviewed.executeReviewedCleanup({evidenceDirectory:f.evidence,approvedPlanSha256:digest},f.deps),/blocked|unavailable/i);assert.equal(f.calls.length,0);assert.equal(await fs.lstat(path.join(f.evidence,'reviewed-cleanup.started.json')).catch(e=>e.code==='ENOENT'?null:Promise.reject(e)),null);
});
test('migration pending is visible and cannot be presented as cleanup ready',async()=>{
 const f=await fixture();await fs.writeFile(path.join(f.source.directory,'migration-attempt-pending.json'),'{}',{mode:0o600});const plan=await reviewed.prepareReviewedCleanup({evidenceDirectory:f.evidence},f.deps);assert.equal(plan.source.migrationPending,true);assert.equal(plan.canExecute,false);assert.equal(f.calls.length,0);
});
test('metadata inventory is not a claim that protected volume contents were verified',async()=>{
 const f=await fixture(),plan=await reviewed.prepareReviewedCleanup({evidenceDirectory:f.evidence},f.deps);assert.equal(plan.source.volumeContentQualification,'Unknown');assert.equal(plan.clone.volumeContentQualification,'Unknown');assert.equal(plan.projectExclusivity,'Unknown');assert(plan.source.resources.some(r=>r.kind==='volume'));
});
test('a failed proof or a missing evidence identity cannot become review ready',async()=>{
 const failed=await fixture(proof=>({...proof,executionPassed:false,phase:'failed'}));await assert.rejects(reviewed.prepareReviewedCleanup({evidenceDirectory:failed.evidence},failed.deps),/execution proof/i);const missing=await fixture();await fs.rename(path.join(missing.source.directory,'migration-evidence-provision.json'),path.join(missing.source.directory,'migration-evidence-provision.retained.json'));await assert.rejects(reviewed.prepareReviewedCleanup({evidenceDirectory:missing.evidence},missing.deps),/identity record|identity/i);assert.equal(missing.calls.length,0);
});
test('a preview never inherits an implicit Docker context and requires an explicit socket',async()=>{
 const f=await fixture(),oldHost=process.env.DOCKER_HOST,oldContext=process.env.DOCKER_CONTEXT;process.env.DOCKER_HOST='unix:///definitely-absent-webapi-review.sock';delete process.env.DOCKER_CONTEXT;
 try{await assert.rejects(reviewed.prepareReviewedCleanup({evidenceDirectory:f.evidence}),/explicit.*socket/i);const file=path.join(f.parent,'not-a-socket');await fs.writeFile(file,'synthetic');await assert.rejects(reviewed.prepareReviewedCleanup({evidenceDirectory:f.evidence,dockerSocket:file}),/socket/i);for(const char of ['?','#','%'])await assert.rejects(reviewed.prepareReviewedCleanup({evidenceDirectory:f.evidence,dockerSocket:'/tmp/webapi'+char+'socket'}),/unsafe.*socket/i);}finally{if(oldHost===undefined)delete process.env.DOCKER_HOST;else process.env.DOCKER_HOST=oldHost;if(oldContext===undefined)delete process.env.DOCKER_CONTEXT;else process.env.DOCKER_CONTEXT=oldContext;}
});
test('explicit socket environment drops inherited host, context and every TLS override',()=>{
 const env=reviewed.localDockerEnvironment({DOCKER_HOST:'tcp://example.invalid',DOCKER_CONTEXT:'foreign',DOCKER_TLS:'1',DOCKER_TLS_VERIFY:'1',DOCKER_CERT_PATH:'/synthetic',PATH:'/bin'});for(const key of ['DOCKER_HOST','DOCKER_CONTEXT','DOCKER_TLS','DOCKER_TLS_VERIFY','DOCKER_CERT_PATH'])assert.equal(Object.hasOwn(env,key),false);assert.equal(env.PATH,'/bin');
});
