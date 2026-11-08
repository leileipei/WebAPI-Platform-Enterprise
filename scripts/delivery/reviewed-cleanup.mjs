import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {pathToFileURL} from 'node:url';
import {readScenarioProof} from './scenario-evidence.mjs';
import {loadState} from '../runtime/state.mjs';
import {loadRelease} from '../runtime/lifecycle.mjs';
import {inspectResources,docker} from '../runtime/docker.mjs';
import {ownedResourceSnapshot,ownedResourceDigest} from '../runtime/owned-resource-snapshot.mjs';
import {cleanupDirectoryIdentity,assertTrustedRestoreParent} from '../runtime/restore-target-lock.mjs';
import {assertRestoreComplete} from '../account-credential/clone-restore.mjs';
import {readCompletedRestoreEvidence} from '../runtime/restore-intent.mjs';
import {prepareMigrationEvidenceVolume} from '../runtime/migration-evidence-volume.mjs';

const hash=value=>createHash('sha256').update(JSON.stringify(value)).digest('hex');
const projectPattern=/^webapi-enterprise-local-test-[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/;
const ownerPattern=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
export const digestReviewPlan=plan=>hash(plan);
export function localDockerEnvironment(input=process.env){const env={...input};for(const name of ['DOCKER_HOST','DOCKER_CONTEXT','DOCKER_TLS','DOCKER_TLS_VERIFY','DOCKER_CERT_PATH'])delete env[name];return env;}
async function explicitLocalInspection(socket){
 if(typeof socket!=='string'||!path.isAbsolute(socket)||/[\u0000\r\n]/.test(socket))throw Error('Explicit local Unix Docker socket is required');
 if(!/^\/[a-zA-Z0-9/._-]+$/.test(socket))throw Error('Unsafe local Docker socket path');
 let info;try{info=await fs.lstat(socket);}catch{throw Error('Explicit local Unix Docker socket is unavailable');}
 if(!info.isSocket()||info.isSymbolicLink()||![0,process.getuid()].includes(info.uid)||await fs.realpath(socket)!==socket)throw Error('Unsafe local Docker socket');
 await assertTrustedRestoreParent(path.dirname(socket));
 const assertSocket=async()=>{const current=await fs.lstat(socket);if(!current.isSocket()||current.dev!==info.dev||current.ino!==info.ino||current.uid!==info.uid)throw Error('Docker socket identity changed');};
 const env=localDockerEnvironment();
 const run=async(args,options={})=>{await assertSocket();const result=await docker(['--host','unix://'+socket,...args],{...options,env});await assertSocket();return result;};
 return {inspect:state=>inspectResources(state,run),transport:{kind:'UnixSocket',path:socket,dev:info.dev,ino:info.ino,uid:info.uid}};
}
async function observeTarget(record,revision,imageId,inspect){
 const {directory,ownerId,projectName}=record??{};
 if(typeof directory!=='string'||!path.isAbsolute(directory)||path.basename(directory)!==projectName||!projectPattern.test(projectName??'')||!ownerPattern.test(ownerId??'')||record.retainedForReview!==true||record.ownerVerified!==true||record.directoryRetained!==true)throw Error('Reviewed fixture identity is incomplete');
 if(await fs.realpath(directory)!==directory)throw Error('Reviewed fixture path is not canonical');
 const identity=await cleanupDirectoryIdentity(directory),state=await loadState(directory);
 if(!identity||state.ownerId!==ownerId||state.projectName!==projectName)throw Error('Reviewed fixture owner changed');
 await assertRestoreComplete(directory);
 const release=await loadRelease(path.join(directory,'release.json'));
 if(release.sourceRevision!==revision||release.imageId!==imageId)throw Error('Reviewed fixture release changed');
 const resources=await inspect(state),snapshot=ownedResourceSnapshot(state,resources),volumes=snapshot.filter(x=>x.kind==='volume').map(x=>x.id).sort();
 const evidence=snapshot.find(x=>x.kind==='volume'&&x.id===projectName+'_migration-evidence');
 if(!evidence||evidence.volumeKind!=='migration-evidence'||!/^[a-f0-9]{64}$/.test(evidence.evidenceNonce??''))throw Error('Migration evidence volume identity is required for reviewed cleanup');
 await prepareMigrationEvidenceVolume(state,{directory,inspect});
 const migrationPending=await fs.lstat(path.join(directory,'migration-attempt-pending.json')).then(()=>true,error=>{if(error.code==='ENOENT')return false;throw error;});
 return {directory,projectName,ownerId,identity,stateSha256:hash(state),releaseSha256:hash(release),resourceSha256:ownedResourceDigest(state,resources),resources:snapshot,volumeNames:volumes,migrationPending,volumeContentQualification:'Unknown'};
}
export async function prepareReviewedCleanup({evidenceDirectory,dockerSocket},deps={}){
 if(typeof evidenceDirectory!=='string'||!path.isAbsolute(evidenceDirectory))throw Error('Absolute proof directory required');
 const root=await fs.realpath(evidenceDirectory);if(root!==evidenceDirectory)throw Error('Proof directory is not canonical');
 const proof=await readScenarioProof(root);
 if(proof?.evidencePersistence!=='verified'||proof.schemaVersion!==2||!['actual-isolated-cross-environment-promotion','actual-isolated-standard-pipeline'].includes(proof.classification)||proof.sourceVerified!==true||proof.executionPassed!==true||proof.isolatedAcceptance!==false||proof.phase!=='awaiting-retained-fixture-review'||proof.failure||proof.failureReason||proof.cleanupError||proof.secondaryFailures?.length||!/^[a-f0-9]{40}$/.test(proof.sourceRevision??'')||!/^sha256:[a-f0-9]{64}$/.test(proof.build?.imageId??'')||proof.build.sourceRevision!==proof.sourceRevision)throw Error('A complete, verified execution proof awaiting review is required');
 const backup=proof.backupRestore;
 if(backup?.passed!==true||backup.actual!==true||backup.sourceEvidenceSupported!==true||proof.cleanup?.projectName!==proof.projectName||backup.cloneCleanup?.projectName!==backup.cloneProjectName||backup.cloneCleanup?.directory!==backup.cloneDirectory||backup.sourceOwnerId!==proof.cleanup?.ownerId||backup.ownerId!==backup.cloneCleanup?.ownerId||backup.ownerId===backup.sourceOwnerId)throw Error('Source or restored clone proof is incomplete');
 const local=deps.inspectResources?{inspect:deps.inspectResources,transport:{kind:'InjectedInspectionOnly'}}:await explicitLocalInspection(dockerSocket),inspect=local.inspect;
 const source=await observeTarget(proof.cleanup,proof.sourceRevision,proof.build.imageId,inspect),clone=await observeTarget(backup.cloneCleanup,proof.sourceRevision,proof.build.imageId,inspect);
 if(source.directory===clone.directory||source.ownerId===clone.ownerId)throw Error('Source and clone must be independent');
 let completion=null;try{completion=await readCompletedRestoreEvidence(clone.directory);if(completion.ownerId!==clone.ownerId||completion.projectName!==clone.projectName)completion=null;}catch{}
 return {schemaVersion:1,status:'Blocked',canExecute:false,formalAcceptance:false,evidenceDirectory:root,classification:proof.classification,sourceRevision:proof.sourceRevision,imageId:proof.build.imageId,proofSha256:hash(proof),proofIntegrity:'LocalReceiptOnly',executionQualification:'Unknown',cloneRestoreQualification:completion?'LocalEvidenceMatched':'Unknown',cloneRestoreEvidence:completion,transport:local.transport,daemonQualification:'Unknown',projectExclusivity:'Unknown',originalEnvironmentQualification:'Unknown',source,clone,
  blockers:['execution_provenance_unverified','positive_restore_chain_unverified','project_exclusivity_unverified','volume_contents_unverified','safe_cleanup_protocol_unavailable']};
}
export async function executeReviewedCleanup(){throw Object.assign(new Error('Reviewed cleanup is blocked until execution provenance, restore identity, project exclusivity and data preservation are verified'),{code:'REVIEWED_CLEANUP_BLOCKED'});}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
 try{const [mode,directory,value,...extra]=process.argv.slice(2);if(extra.length||!directory||!['plan','execute'].includes(mode))throw Error('Use plan <absolute-proof-directory> <absolute-unix-socket> or execute <absolute-proof-directory> <reviewed-plan-sha256>');
  const result=mode==='plan'?await prepareReviewedCleanup({evidenceDirectory:directory,dockerSocket:value}):await executeReviewedCleanup({evidenceDirectory:directory,approvedPlanSha256:value});console.log(JSON.stringify(mode==='plan'?{plan:result,planSha256:digestReviewPlan(result)}:result,null,2));
 }catch(error){console.error(error.message);process.exitCode=1;}
}
