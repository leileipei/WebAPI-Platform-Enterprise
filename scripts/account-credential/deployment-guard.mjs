import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {docker,inspectResources,assertOwnership} from '../runtime/docker.mjs';

const feature='account-password-lifecycle-v1';
export async function accountBuildFeatures(source) {
  for(const file of ['src/WebApi.Infrastructure/Security/PasswordLifecycleGuard.cs','src/WebApi.ControlPlane/Security/PasswordChangeRequiredMiddleware.cs','src/WebApi.Infrastructure/Security/PasswordLifecycleService.cs']) {
    const stat=await fs.lstat(path.join(source,file)).catch(e=>{if(e.code==='ENOENT')return null;throw e;});
    if(!stat?.isFile()||stat.isSymbolicLink())return [];
  }
  const clone=await fs.lstat(path.join(source,'src/WebApi.Gateway/CredentialUsage/UsageCloneCommand.cs')).catch(e=>{if(e.code==='ENOENT')return null;throw e;});
  return [feature,...(clone?.isFile()&&!clone.isSymbolicLink()?['credential-usage-clone-rebind-v1']:[])];
}

// Build capability is a declaration in the immutable built image, not a signature or a
// substitute for qualified tests. Requiring it for the schema (even with zero pending
// users) avoids racing another administrator creating/resetting a local account.
export function assertAccountDeploymentCompatibility({hasLifecycleSchema,release,image,buildIdentity}) {
  assert.equal(typeof hasLifecycleSchema,'boolean');
  if(!hasLifecycleSchema)return true;
  assert.match(release.sourceRevision,/^[a-f0-9]{40}$/);assert.match(release.imageId,/^sha256:[a-f0-9]{64}$/);
  assert.equal(image?.Id,release.imageId);assert.equal(image.Config?.Labels?.['org.opencontainers.image.revision'],release.sourceRevision);
  assert.equal(buildIdentity?.sourceRevision,release.sourceRevision);
  assert(Object.keys(release.applications??{}).length>0,'Missing built application hashes');
  assert.deepEqual(buildIdentity.applications,release.applications);
  assert(Array.isArray(buildIdentity.features)&&buildIdentity.features.includes(feature),'Target build is not compatible with the account lifecycle schema; restore a compatible application before startup');
  return true;
}

export function needsAccountDeploymentGuard(args) {
  const operation=args[0];
  if(!['up','start','restart','run','create'].includes(operation))return false;
  if(operation==='run')return args.some(a=>['migrator','control-plane','worker','console'].includes(a));
  const explicit=args.slice(1).filter(a=>!a.startsWith('-'));
  const otherServices=new Set(['postgres','redis','collector','prometheus','loki','tempo','gateway-a','gateway-b','backend-a','backend-b','prod-gateway-a','prod-gateway-b','notification-fixture']);
  // Unknown option values (e.g. --scale control-plane=2), not just explicit service
  // names, must fail closed instead of concealing an implicit "start all" command.
  return explicit.length===0||explicit.some(a=>!otherServices.has(a));
}

export async function guardAccountDeployment(ctx,{inspect=inspectResources,ownership=assertOwnership,run=docker}={}) {
  ownership(ctx.state,await inspect(ctx.state));
  // Only PostgreSQL may start before the guard. No management process or migration
  // runs until a read-only query and, when needed, immutable capability checks pass.
  await ctx.raw(['up','-d','--wait','postgres']);
  const sql="BEGIN READ ONLY; SET LOCAL statement_timeout='5s'; SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='users' AND column_name='must_change_password'); COMMIT;";
  const response=await ctx.raw(['exec','-T','postgres','psql','--no-psqlrc','-U','webapi','-d','webapi','-v','ON_ERROR_STOP=1','-qAt','--command',sql]);
  assert.equal(response.exitCode,0,'Cannot inspect live account schema');
  const result=response.stdout.trim();assert(['t','f'].includes(result),'Unexpected account schema result');
  if(result==='f')return {lifecycleSchema:false};
  assert.match(ctx.release.imageId,/^sha256:[a-f0-9]{64}$/);
  const image=JSON.parse((await run(['image','inspect',ctx.release.imageId])).stdout)[0];
  const built=await run(['run','--rm','--network','none','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges','--label','com.docker.compose.project='+ctx.state.projectName,'--label','com.webapi.runtime.owner='+ctx.state.ownerId,'--entrypoint','cat',ctx.release.imageId,'/app/build-identity.json']);
  assertAccountDeploymentCompatibility({hasLifecycleSchema:true,release:ctx.release,image,buildIdentity:JSON.parse(built.stdout)});
  return {lifecycleSchema:true,feature};
}
