import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {assertAccountDeploymentCompatibility} from './deployment-guard.mjs';
import {validateCloneOwnership} from '../contracts/evidence.mjs';
import {docker,inspectResources} from '../runtime/docker.mjs';
import {safeFile,writePrivate,RuntimeError} from '../runtime/state.mjs';

const uuid=/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/;
export async function assertRestoreComplete(directory) {
  if(await fs.lstat(path.join(directory,'restore-pending.json')).catch(e=>{if(e.code==='ENOENT')return null;throw e;}))
    throw new RuntimeError('Runtime restore is incomplete/pending; retain its material and complete the owned restore before startup.',3);
}
export async function inspectUsageRebindCapability(release) {
  const image=JSON.parse((await docker(['image','inspect',release.imageId])).stdout)[0];
  const buildIdentity=JSON.parse((await docker(['run','--rm','--network','none','--read-only','--entrypoint','cat',release.imageId,'/app/build-identity.json'])).stdout);
  assertAccountDeploymentCompatibility({hasLifecycleSchema:true,release,image,buildIdentity});
  assert(buildIdentity.features.includes('credential-usage-clone-rebind-v1'),'Fixed target image cannot rebind a usage clone');
  return {image,buildIdentity};
}

export function planUsageCloneRebind({state,sourceOwnerId,directory,release,image,buildIdentity,inventory,config,resources}) {
  assert(uuid.test(sourceOwnerId)&&uuid.test(state.ownerId)&&sourceOwnerId!==state.ownerId,'Distinct clone owners required');
  validateCloneOwnership(state,resources,directory);
  assert.equal(config?.name,state.projectName,'Compose project identity mismatch');
  assertAccountDeploymentCompatibility({hasLifecycleSchema:true,release,image,buildIdentity});
  assert(buildIdentity.features.includes('credential-usage-clone-rebind-v1'),'Missing clone-rebind capability');
  for(const resource of resources) {
    if(resource.Kind==='network')assert(resource.Name.startsWith(state.projectName+'_'),'Foreign restore network');
    if(resource.Kind==='container')assert(!resource.State?.Running,'Restore rebind must precede all application startup');
  }
  const actualVolume=source=>config.volumes?.[source]?.name??source;
  function volume(source){const name=actualVolume(source);assert(name.startsWith(state.projectName+'_'),'Foreign restore volume');assert(resources.some(r=>r.Kind==='volume'&&r.Name===name),'Missing owned restore volume');return name;}
  return Object.entries(inventory.volumes).filter(([,v])=>v.status==='present').map(([name,entry])=>{
    const targetVolume=volume(name),matches=Object.entries(config.services??{}).filter(([,s])=>(s.volumes??[]).some(m=>m.type==='volume'&&actualVolume(m.source)===targetVolume&&m.target==='/lkg'));
    assert.equal(matches.length,1,'Missing or ambiguous restored gateway');
    const [serviceName,service]=matches[0],env=service.environment??{},checkpoint=entry.checkpoint;
    assert.equal(service.image,release.imageId);
    assert.equal(service.labels?.['com.webapi.runtime.owner'],state.ownerId);
    if(Object.hasOwn(service.labels,'com.docker.compose.project'))assert.equal(service.labels['com.docker.compose.project'],state.projectName);
    assert.equal(env.Gateway__LkgDirectory,'/lkg');assert.equal(env.Gateway__EnvironmentId,checkpoint.environmentId);assert.equal(env.Gateway__NodeName,checkpoint.nodeName);
    const cp=new URL(env.Gateway__ControlPlaneUrl);assert.equal(cp.origin,'http://control-plane:8080');assert.equal(cp.username+cp.password+cp.search+cp.hash+cp.pathname,'/');
    assert.match(checkpoint.sha256,/^[a-f0-9]{64}$/);
    const secret=env.Gateway__SecretFile;assert(typeof secret==='string'&&path.posix.isAbsolute(secret));
    const mounts=(service.volumes??[]).filter(m=>m.read_only===true&&(secret===m.target||secret.startsWith(m.target+'/')));
    assert.equal(mounts.length,1,'Missing private enrollment mount');const mount=mounts[0],privateBindPaths=[];
    let secretMount,secretFile;
    if(mount.type==='volume') {secretMount='type=volume,src='+volume(mount.source)+',dst='+mount.target+',readonly';secretFile=secret;}
    else {
      assert.equal(mount.type,'bind');const relative=path.posix.relative(mount.target,secret);assert(!relative.split('/').includes('..'));
      const source=path.resolve(mount.source,relative);assert(source.startsWith(path.resolve(directory)+path.sep),'Foreign enrollment bind');
      privateBindPaths.push(source);secretMount='type=bind,src='+source+',dst=/run/rebind.secret,readonly';secretFile='/run/rebind.secret';
    }
    assert(!/[,\r\n\0]/.test(secretMount.replaceAll(',src=',':').replaceAll(',dst=',':').replace(/,readonly$/,'')),'Unsafe restore mount syntax');
    const args=['run','--rm','--network','none','--cpus','1','--memory','2g','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges','--user','10001:10001','--label','com.docker.compose.project='+state.projectName,'--label','com.webapi.runtime.owner='+state.ownerId,
      '--mount','type=volume,src='+targetVolume+',dst=/lkg','--mount',secretMount,release.imageId,'dotnet','/app/gateway/WebApi.Gateway.dll','usage-rebind','/lkg',checkpoint.environmentId,checkpoint.nodeName,cp.href,secretFile,sourceOwnerId,state.ownerId,checkpoint.sha256];
    return {volume:name,serviceName,checkpoint,privateBindPaths,args};
  });
}

export async function rebindRestoredUsage(ctx,sourceOwnerId,inventory,capability) {
  const config=JSON.parse((await ctx.raw(['config','--format','json'])).stdout);
  const receipts=[];
  for(const name of Object.keys(inventory.volumes).filter(n=>inventory.volumes[n].status==='present')) {
    const plan=planUsageCloneRebind({state:ctx.state,sourceOwnerId,directory:ctx.directory,release:ctx.release,...capability,inventory:{schemaVersion:1,volumes:{[name]:inventory.volumes[name]}},config,resources:await inspectResources(ctx.state)});
    const item=plan[0];
    for(const file of item.privateBindPaths){await safeFile(file);assert((await fs.realpath(file)).startsWith((await fs.realpath(ctx.directory))+path.sep),'Symlink enrollment ancestor');}
    const result=await docker(item.args),receipt=JSON.parse(result.stdout.trim());
    assert(['Rebound','AlreadyRebound'].includes(receipt.result));assert.equal(receipt.sourceOwnerId,sourceOwnerId);assert.equal(receipt.targetOwnerId,ctx.state.ownerId);
    assert.equal(receipt.previousCollectorId,item.checkpoint.collectorId);assert.equal(receipt.previousNextSequence,item.checkpoint.nextSequence);assert.equal(receipt.previousCheckpointHash,item.checkpoint.sha256);
    assert(uuid.test(receipt.collectorId)&&receipt.collectorId!==receipt.previousCollectorId);assert.equal(receipt.nextSequence,'1');
    receipts.push({volume:name,serviceName:item.serviceName,...receipt});
  }
  const proof={schemaVersion:1,sourceOwnerId,targetOwnerId:ctx.state.ownerId,projectName:ctx.state.projectName,sourceRevision:ctx.release.sourceRevision,imageId:ctx.release.imageId,networkMode:'none',receipts};
  await writePrivate(path.join(ctx.directory,'usage-clone-rebind.json'),proof);return proof;
}
