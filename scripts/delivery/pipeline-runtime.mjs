import fs from 'node:fs/promises';import path from 'node:path';import assert from 'node:assert/strict';
import {validateFixture} from '../runtime/fixture-guard.mjs';
import {prepareDeliveryContext} from './promotion-scenario.mjs';
import {prepareContext} from '../runtime/context.mjs';import {loadState,writePrivate} from '../runtime/state.mjs';import {docker,inspectResources,assertOwnership} from '../runtime/docker.mjs';import {validatePipelineContext,pipelineServices,backupVolumeNames} from '../runtime/acceptance-backup.mjs';
import {prepareMigrationEvidenceVolume} from '../runtime/migration-evidence-volume.mjs';
import {withRestoreTargetLock,cleanupDirectoryIdentity,assertCleanupDirectoryIdentity,cleanupParentWritable,assertTrustedRestoreParent} from '../runtime/restore-target-lock.mjs';
import {assertRestoreComplete} from '../account-credential/clone-restore.mjs';
export async function preparePipelineContext(directory,state,release){
 const ctx=await prepareDeliveryContext(directory,state,release),meta=JSON.parse(await fs.readFile(path.join(directory,'pipeline-runtime.json'),'utf8'));ctx.pipeline={ownerId:state.ownerId,stageGroups:meta.stageGroups};validatePipelineContext(ctx);
 const base=JSON.parse(await fs.readFile(path.join(directory,'artifacts',release.sourceRevision,'deploy/compose.runtime.gateways.yml'),'utf8')),demo=JSON.parse(await fs.readFile(path.join(directory,'artifacts',release.sourceRevision,'deploy/compose.runtime.demo.yml'),'utf8'));
 const services={},volumes={},cp={environment:{},volumes:[]};let enrollment=4;
 for(const g of meta.stageGroups){assert.match(g.environmentId,/^[a-f0-9-]{36}$/);if(g.kind!=='Middle')continue;const prefix=`pipeline-stage-${g.order}`;for(const name of g.volumes)volumes[name]={labels:{'com.webapi.runtime.owner':state.ownerId}};
 const mount=g.volumes.map((v,i)=>v+':'+['/out','/lkg-a','/lkg-b'][i]);services[`init-${prefix}-storage`]={image:release.dependencyImages.sdk,user:'0:0',profiles:['tools'],labels:{'com.webapi.runtime.owner':state.ownerId},volumes:mount,command:['bash','-c','set -e; chown -R 10001:10001 /out /lkg-a /lkg-b; chmod 700 /out /lkg-a /lkg-b']};
 services[`seed-${prefix}-secrets`]={...services[`init-${prefix}-storage`],volumes:[...mount,'secrets-control-plane:/source:ro',path.join(directory,'pipeline-private')+':/input:ro'],command:['bash','-c',`set -e; umask 077; test -z "$(ls -A /out)"; cp /source/ip-hmac /source/cursor-signing /out/; cp /input/node-${g.order}-a /out/node-a; cp /input/node-${g.order}-b /out/node-b; chown -R 10001:10001 /out /lkg-a /lkg-b; chmod 700 /out /lkg-a /lkg-b; chmod 600 /out/*`]};
 const backend=structuredClone(demo.services['backend-a']);backend.image=release.imageId;backend.labels={'com.webapi.runtime.owner':state.ownerId};backend.environment.Backend__Id='Stage'+g.order;services[prefix+'-backend']=backend;
 cp.volumes.push(`${prefix}-secrets:/run/${prefix}:ro`);
 for(const [i,name]of g.services.entries()){const letter=i?'b':'a',gateway=structuredClone(base.services[i?'gateway-b':'gateway-a']);gateway.image=release.imageId;gateway.labels={'com.webapi.runtime.owner':state.ownerId};gateway.environment={...gateway.environment,Gateway__EnvironmentId:g.environmentId,Gateway__NodeName:name,Gateway__SecretFile:'/run/secrets/node-'+letter,Upstream__AllowedOrigins__0:`http://${prefix}-backend:8080`,Upstream__AllowedOrigins__1:'http://backend-b:8080'};gateway.volumes=[`${prefix}-secrets:/run/secrets:ro`,`${prefix}-lkg-${letter}:/lkg`,'secrets-cache:/run/cache:ro'];const port=g.ports[i?'gatewayB':'gatewayA'];assert(Number.isInteger(port)&&port>1024&&port<=65535);gateway.ports=[`127.0.0.1:${port}:8080`];services[name]=gateway;cp.environment[`Nodes__Enrollments__${enrollment}__EnvironmentId`]=g.environmentId;cp.environment[`Nodes__Enrollments__${enrollment}__NodeName`]=name;cp.environment[`Nodes__Enrollments__${enrollment}__SecretFile`]=`/run/${prefix}/node-${letter}`;enrollment++;}
 }
 services['control-plane']=cp;const overlay=path.join(directory,'pipeline-overlay.json');await writePrivate(overlay,{services,volumes});ctx.files.push('-f',overlay);return ctx;
}
export async function cleanupPipelineContext(directory,release,{expectedOwner,inspect=inspectResources,prepare=prepareContext,preparePipeline=preparePipelineContext,prepareEvidence=prepareMigrationEvidenceVolume,remove=fs.rm,retainOnFailure=false,retainForReview=false,targetLock}={}){
 const identity=await cleanupDirectoryIdentity(directory),state=await loadState(directory);validateFixture(state.projectName,directory);if(!expectedOwner||state.ownerId!==expectedOwner)throw Error('Expected disposable cleanup owner mismatch');await assertCleanupDirectoryIdentity(directory,identity);
 const parentWritable=await cleanupParentWritable(directory);
 const recheck=async()=>{await assertCleanupDirectoryIdentity(directory,identity);const current=await loadState(directory);if(current.ownerId!==state.ownerId||current.projectName!==state.projectName)throw Error('Cleanup owner identity changed');};
 return withRestoreTargetLock(directory,async()=>{await assertTrustedRestoreParent(await fs.realpath(path.dirname(path.resolve(directory))),{privateParent:false,protectedChild:directory});await recheck();await assertRestoreComplete(directory);const resources=await inspect(state);assertOwnership(state,resources);
 // Failure resources remain diagnostic evidence, including on old releases.
 if(retainOnFailure||retainForReview)return {...(retainOnFailure?{retainedForFailure:true}:{retainedForReview:true}),projectName:state.projectName,ownerId:state.ownerId,directory,directoryRetained:true,remainingOwnedResources:resources.length,ownerVerified:true};
 let ctx;try{await fs.access(path.join(directory,'pipeline-runtime.json'));ctx=await preparePipeline(directory,state,release);}catch(e){if(e.code!=='ENOENT')throw e;ctx=await prepare(directory,state,release);}
 const allowed=new Set([...backupVolumeNames(ctx),'bootstrap-password',...['postgres','control-plane','worker','gateway-a','gateway-b','migrator'].map(n=>'secrets-'+n)]);
 const validate=items=>{assertOwnership(state,items);for(const r of items)if(r.Kind==='volume')assert([...allowed].some(n=>r.Name===state.projectName+'_'+n),'Unexpected owned pipeline volume');};validate(resources);
 const evidence=resources.find(r=>r.Kind==='volume'&&r.Name===state.projectName+'_migration-evidence');
 if(ctx.migrationEvidenceSupported||evidence){
  if(!ctx.migrationEvidenceSupported||!evidence)throw Error('Migration evidence volume is missing or its identity is unverified; retain fixture');
  await prepareEvidence(state,{directory,inspect});
  await ctx.compose('--profile','tools','down','--remove-orphans');
  const remaining=await inspect(state);validate(remaining);
  if(remaining.some(r=>r.Kind!=='volume'))throw Error('Pipeline containers or networks remain; retain fixture');
  await prepareEvidence(state,{directory,inspect});
  return {retainedForEvidence:true,projectName:state.projectName,ownerId:state.ownerId,directory,directoryRetained:true,identityVerified:true,containersRemaining:0,volumesRemaining:remaining.length,retainedVolumeNames:remaining.map(r=>r.Name).sort(),remainingOwnedResources:remaining.length,ownerVerified:true,originalRuntimeUntouched:true};
 }
 if(!parentWritable)throw Error('Cleanup requires a writable parent to remove the target directory');
 await ctx.compose('--profile','tools','down','--volumes','--remove-orphans');const remaining=await inspect(state);validate(remaining);assert.equal(remaining.length,0);await recheck();await remove(directory,{recursive:true,force:true});return {projectName:state.projectName,ownerId:state.ownerId,directory,directoryRetained:false,containersRemaining:0,volumesRemaining:0,secretFilesRemaining:0,remainingOwnedResources:0,ownerVerified:true,originalRuntimeUntouched:true};
 },{token:targetLock,forceParent:parentWritable});
}
export const pipelineRuntimeServices=ctx=>[...new Set([...pipelineServices(ctx),...(ctx.pipeline?.stageGroups.filter(g=>g.kind==='Middle').map(g=>`pipeline-stage-${g.order}-backend`)||[])])];
