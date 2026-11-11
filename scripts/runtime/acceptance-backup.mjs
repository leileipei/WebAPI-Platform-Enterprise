import {assertRestoreComplete,inspectUsageRebindCapability,rebindRestoredUsage} from '../account-credential/clone-restore.mjs';
import {writeUsageBackupInventory,verifyUsageBackupInventory,requireSafeUsageCloneRestore,usageRestoreVolumeNames} from '../account-credential/journal-backup.mjs';
import {copyLoginProtectionBackup,restoreLoginProtectionBackup,loginProtectionReceiptName} from '../security/runtime-secrets.mjs';
import {synchronizeConsoleProxyTrust} from '../security/proxy-trust.mjs';
import {notificationVolumes,notificationStateName,notificationBackupRequiredInputs,copyNotificationBackup,restoreNotificationBackup} from '../notifications/runtime-secrets.mjs';
import {copyCacheBackup,restoreCacheBackup,cacheReceiptName} from '../gateway-policies/runtime-secrets.mjs';
import{copySsoBackup,restoreSsoBackup,ssoConfigName}from'./sso-deployment.mjs';
import fs from'node:fs/promises';import{constants}from'node:fs';import path from'node:path';import{createHash,randomBytes}from'node:crypto';import{initializeState,saveState,writePrivate,loadState,validateState,withRuntimeLock,safeFile,ownedDirectory}from'./state.mjs';import{docker,inspectResources,assertOwnership}from'./docker.mjs';
import{validateFixture}from'./fixture-guard.mjs';
import{runtimeServices,loadRelease,waitForRuntime}from'./lifecycle.mjs';import{prepareContext}from'./context.mjs';
import {prepareMigrationEvidenceVolume,markMigrationEvidenceVolumeReady} from './migration-evidence-volume.mjs';
import {validateMigrationEvidenceArchive,hashMigrationEvidenceArchive} from './migration-evidence-archive.mjs';
import {freezeMigrationEvidenceArchive,verifyFrozenMigrationEvidenceArchive} from './migration-evidence-freeze.mjs';
import {restoreInputNames,requireRestoreSourceCoverage,readVerifiedRestoreInput,freezeRestoreInputs,verifyFrozenRestoreInputs,restoreInputSecretModes} from './restore-input-snapshot.mjs';
import {readRestoreManifest,parseRestoreManifest} from './restore-manifest.mjs';
import {createRestoreIntent,writeRestoreReference,writeRestoreCompletion} from './restore-intent.mjs';
import {ensureDirectoryDurable,withRestoreTargetLock} from './restore-target-lock.mjs';
import {resolveRestoreReleaseCapabilities,verifyRestoreRevisionIdentity} from './restore-release-capabilities.mjs';
import {validateRestoreStorageBudget,requireRestoreIntegration,requireBoundRestoreProof,requireRestoreStorageProof,persistRestoreStorageProof} from './restore-storage-budget.mjs';
const verifiedMigrationRestores=new WeakSet();
const initializerPermits=new WeakMap();
export async function inspectMigrationEvidenceArchive(file,imageId,{validate=validateMigrationEvidenceArchive,hash=hashMigrationEvidenceArchive,run=docker,openInput=async name=>fs.open(name,constants.O_RDONLY|constants.O_NOFOLLOW),expectedHash}={}){
 if(!path.isAbsolute(file)||/[\u0000\r\n,]/.test(file)||!/^sha256:[a-f0-9]{64}$/.test(imageId??''))throw Error('Invalid migration evidence inspection identity');
 await validate(file);const before=await hash(file);if(expectedHash!==undefined&&before!==expectedHash)throw Error('Migration evidence archive checksum mismatch');
 const input=await openInput(file);let result;
 try{result=await run(['run','--rm','--pull','never','--network','none','--read-only','--interactive','--memory','512m','--cpus','1','--user','0:0',
  '--env','WEBAPI_MIGRATION_ARCHIVE_SHA256='+before,'--entrypoint','dotnet',imageId,'/app/migrator/WebApi.Migrator.dll','--inspect-evidence-archive-stdin'],{inputFd:input.fd});}
 finally{await input.close();}
 if(result.exitCode!==0||typeof result.stdout!=='string'||result.stdout.length>4096)throw Error('Migration evidence archive semantic inspection failed');
 let inspection;try{inspection=JSON.parse(result.stdout);}catch{throw Error('Migration evidence inspection response is invalid');}
 if(!inspection||Object.keys(inspection).sort().join(',')!=='attempts,contentHash,pendingAttempts,schemaVersion'||inspection.schemaVersion!==1||!/^[a-f0-9]{64}$/.test(inspection.contentHash??'')||
    !Number.isSafeInteger(inspection.attempts)||inspection.attempts<1||!Number.isSafeInteger(inspection.pendingAttempts)||
    inspection.pendingAttempts<0||inspection.pendingAttempts>inspection.attempts)throw Error('Migration evidence inspection response is invalid');
 // Revalidate structure after the verifier as well, then compare the same
 // bytes again; a replacement between the first check and hashing cannot be
 // accepted merely because its records happen to parse.
 await validate(file);if(await hash(file)!==before)throw Error('Migration evidence archive changed during inspection');
 return {...inspection,sha256:before};
}
export async function finishMigrationEvidenceRestore(directory,state,inspection,verified,{beforeFinish=async()=>{}}={}){
 directory=await ownedDirectory(directory);const file=path.join(directory,'restore-pending.json');await safeFile(file);
 const handle=await fs.open(file,constants.O_RDONLY|constants.O_NOFOLLOW);
 try{
  const initial=await handle.stat(),contents=await handle.readFile('utf8'),after=await handle.stat(),named=await fs.lstat(file);
  if(initial.nlink!==1||initial.dev!==after.dev||initial.ino!==after.ino||initial.size!==after.size||
     initial.mtimeMs!==after.mtimeMs||initial.ctimeMs!==after.ctimeMs||named.dev!==initial.dev||named.ino!==initial.ino)
   throw Error('Restore pending identity changed');
  const marker=JSON.parse(contents);
  if(marker.schemaVersion!==1||marker.ownerId!==state.ownerId||marker.projectName!==state.projectName)throw Error('Restore pending owner mismatch');
  if(inspection&&(!Number.isSafeInteger(inspection.pendingAttempts)||inspection.pendingAttempts<0))throw Error('Invalid migration evidence inspection');
  if(inspection&&(!verifiedMigrationRestores.has(verified)||inspection.pendingAttempts!==0||
     verified.ownerId!==state.ownerId||verified.projectName!==state.projectName||verified.sha256!==inspection.sha256||verified.contentHash!==inspection.contentHash||verified.attempts!==inspection.attempts))return false;
  if(inspection&&['dev','ino','size','mtimeMs','ctimeMs'].some(key=>verified.marker[key]!==initial[key]))throw Error('Restore pending identity changed');
  await beforeFinish();
  const current=await fs.lstat(file);
  if(current.dev!==initial.dev||current.ino!==initial.ino||current.size!==initial.size||current.mtimeMs!==initial.mtimeMs||current.ctimeMs!==initial.ctimeMs)
   throw Error('Restore pending identity changed');
  await fs.unlink(file);const parent=await fs.open(directory,'r');try{await parent.sync();}finally{await parent.close();}
  if(verified)verifiedMigrationRestores.delete(verified);
  return true;
 }finally{await handle.close();}
}
export function migrationEvidenceRestoreHeld(ctx,inspection){
 if(ctx.migrationEvidenceSupported&&!inspection)throw Error('Migration evidence inspection is required');
 if(!ctx.migrationEvidenceSupported&&inspection)throw Error('Unexpected migration evidence inspection');
 if(inspection&&(!Number.isSafeInteger(inspection.attempts)||inspection.attempts<1||!Number.isSafeInteger(inspection.pendingAttempts)||inspection.pendingAttempts<0||inspection.pendingAttempts>inspection.attempts))throw Error('Invalid migration evidence inspection');
 return Boolean(inspection?.pendingAttempts);
}
// The verification process reads the restored evidence volume and DB in a
// read-only transaction. Matching local snapshots are not an anti-rollback witness.
export async function verifyRestoredMigrationEvidence(ctx,frozen,inspection,{inspect=inspectResources,verifyFrozen=verifyFrozenMigrationEvidenceArchive}={}){
 const directory=await ownedDirectory(ctx.directory);validateFixture(ctx.state.projectName,directory);
 if(!ctx.migrationEvidenceSupported||!inspection||inspection.schemaVersion!==1||!Number.isSafeInteger(inspection.attempts)||inspection.attempts<1||
    inspection.pendingAttempts!==0||!/^[a-f0-9]{64}$/.test(inspection.contentHash??'')||!frozen||frozen.sha256!==inspection.sha256||!/^[a-f0-9]{64}$/.test(frozen.sha256??'')||
    path.dirname(frozen.path??'')!==directory||/[\u0000\r\n,:]/.test(frozen.path??''))throw Error('Invalid restored migration evidence identity');
 await verifyFrozen(frozen);assertOwnership(ctx.state,await inspect(ctx.state));
 await ctx.compose('up','-d','--wait','postgres');assertOwnership(ctx.state,await inspect(ctx.state));
 const result=await ctx.raw(['run','--rm','-T','--no-deps','-e','WEBAPI_MIGRATION_ARCHIVE_CONTENT_SHA256='+inspection.contentHash,
  '-e','WEBAPI_MIGRATION_ARCHIVE_ATTEMPTS='+inspection.attempts,'-e','WEBAPI_MIGRATION_ARCHIVE_PENDING=0',
  'migrator','dotnet','/app/migrator/WebApi.Migrator.dll','--verify-evidence-restore-digest'],{allowFailure:true});
 if(result.exitCode!==0||typeof result.stdout!=='string'||result.stdout.length>4096)throw Error('Restored migration evidence database verification failed');
 let report;try{report=JSON.parse(result.stdout);}catch{throw Error('Invalid migration evidence verification result');}
 if(!report||JSON.stringify(Object.keys(report).sort())!==JSON.stringify(['attempts','committedAttempts','contentHash','continuity','schemaVersion'])||
    report.schemaVersion!==1||report.attempts!==inspection.attempts||!Number.isSafeInteger(report.committedAttempts)||
    report.committedAttempts<1||report.committedAttempts>report.attempts||report.contentHash!==inspection.contentHash||report.continuity!=='Unknown')
  throw Error('Invalid migration evidence verification result');
 await verifyFrozen(frozen);
 const markerFile=path.join(directory,'restore-pending.json');await safeFile(markerFile);const marker=await fs.lstat(markerFile);
 if(marker.nlink!==1)throw Error('Restore pending identity changed');
 const token=Object.freeze({ownerId:ctx.state.ownerId,projectName:ctx.state.projectName,sha256:frozen.sha256,contentHash:report.contentHash,attempts:report.attempts,continuity:'Unknown',marker:Object.freeze(Object.fromEntries(['dev','ino','size','mtimeMs','ctimeMs'].map(key=>[key,marker[key]])))});
 verifiedMigrationRestores.add(token);return token;
}
export const deliveryVolumeNames=['delivery-secrets','delivery-lkg-a','delivery-lkg-b'];
export const deliveryServices=['prod-gateway-a','prod-gateway-b'];
export function validateDeliveryContext(ctx){if(!ctx.delivery)return [];if(JSON.stringify(ctx.delivery.volumes)!==JSON.stringify(deliveryVolumeNames)||JSON.stringify(ctx.delivery.services)!==JSON.stringify(deliveryServices))throw Error('Unexpected delivery backup scope');return deliveryVolumeNames;}
export function validatePipelineContext(ctx){
 if(!ctx.pipeline)return [];const groups=ctx.pipeline.stageGroups;if(!Array.isArray(groups)||groups.length<2||groups.length>8)throw Error('Unexpected pipeline stage scope');
 if(ctx.state&&ctx.pipeline.ownerId!==ctx.state.ownerId)throw Error('Pipeline owner mismatch');const ids=new Set();
 for(const [i,g]of groups.entries()){if(g.type!=='PipelineStageGroup'||g.order!==i+1||!g.environmentId||ids.has(g.environmentId))throw Error('Unexpected pipeline Stage group');ids.add(g.environmentId);const kind=i===0?'Source':i===groups.length-1?'Production':'Middle';if(g.kind!==kind)throw Error('Unexpected pipeline stage kind');const expected=kind==='Source'?{services:['gateway-a','gateway-b'],volumes:['lkg-a','lkg-b']}:kind==='Production'?{services:deliveryServices,volumes:deliveryVolumeNames}:{services:[`pipeline-stage-${g.order}-gateway-a`,`pipeline-stage-${g.order}-gateway-b`],volumes:[`pipeline-stage-${g.order}-secrets`,`pipeline-stage-${g.order}-lkg-a`,`pipeline-stage-${g.order}-lkg-b`]};if(JSON.stringify(g.services)!==JSON.stringify(expected.services)||JSON.stringify(g.volumes)!==JSON.stringify(expected.volumes))throw Error('Unexpected pipeline service/volume scope');}
 return [...new Set(groups.flatMap(g=>g.volumes))];
}
export const pipelineStageTopology=groups=>groups.map(({type,order,environmentId,kind,services,volumes})=>({type,order,environmentId,kind,services,volumes}));
export function validateRestoreAuxiliaryMetadata(manifest,previous,{pipeline,delivery}={}){
 if(manifest.delivery){
  if(!delivery||!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(delivery.targetEnvironmentId??'')||
     !delivery.ports||['gatewayA','gatewayB'].some(key=>!Number.isInteger(delivery.ports[key])||delivery.ports[key]<=1024||delivery.ports[key]>65535)||
     delivery.ports.gatewayA===delivery.ports.gatewayB)throw Error('Invalid delivery target environment or ports');
 }
 if(manifest.pipeline){
  if(manifest.pipeline.ownerId!==previous?.ownerId)throw Error('Restore pipeline owner mismatch');
  if(!pipeline||!Array.isArray(pipeline.stageGroups))throw Error('Restore pipeline metadata is invalid');
  validatePipelineContext({pipeline:{ownerId:previous.ownerId,stageGroups:pipeline.stageGroups}});
  if(JSON.stringify(pipelineStageTopology(pipeline.stageGroups))!==JSON.stringify(pipelineStageTopology(manifest.pipeline.stageGroups)))throw Error('Restore pipeline topology mismatch');
  for(const [index,group] of pipeline.stageGroups.entries()){
   if(!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(group.environmentId??'')||!group.ports||
      ['gatewayA','gatewayB'].some(key=>!Number.isInteger(group.ports[key])||group.ports[key]<=1024||group.ports[key]>65535)||group.ports.gatewayA===group.ports.gatewayB)
    throw Error('Restore pipeline stage environment or ports are invalid');
   const recorded=manifest.pipeline.stageGroups[index]?.ports;
   if(!recorded||['gatewayA','gatewayB'].some(key=>recorded[key]!==group.ports[key]))throw Error('Restore pipeline stage ports mismatch');
  }
  const source=pipeline.stageGroups[0].ports;
  if(!previous.ports||['console','gatewayA','gatewayB'].some(key=>source[key]!==previous.ports[key]||manifest.pipeline.stageGroups[0].ports[key]!==previous.ports[key]))throw Error('Restore pipeline source runtime ports mismatch');
  const production=pipeline.stageGroups.at(-1);
  if(production.environmentId!==delivery.targetEnvironmentId)throw Error('Restore pipeline production environment mismatch');
  if(!production.ports||['gatewayA','gatewayB'].some(key=>production.ports[key]!==delivery.ports[key]))throw Error('Restore pipeline production ports mismatch');
 }
}
export const pipelineServices=ctx=>{validatePipelineContext(ctx);return ctx.pipeline?ctx.pipeline.stageGroups.flatMap(g=>[...g.services,...(g.kind==='Middle'?[`pipeline-stage-${g.order}-backend`]:[])]):[];};
export const backupVolumeNames=ctx=>{if(ctx.pipeline&&ctx.reportsSupported!==true)throw Error('Pipeline backup requires protected report volume');return [...new Set([...validatePipelineContext(ctx),...validateDeliveryContext(ctx),...dataVolumes,...(ctx.reportsSupported?['verification-reports']:[]),...(ctx.cacheSupported?['secrets-cache']:[]),...(ctx.migrationEvidenceSupported?['migration-evidence']:[]),...(ctx.notification?notificationVolumes:[])])];};

export const dataVolumes=['pg','lkg-a','lkg-b','dp-keys','prometheus-data','loki-data','tempo-data'];
// Keep the read-only proxy online so stopping writers does not change its trusted IP.
export const backupStopServices=(state,notification=null)=>runtimeServices(state,notification).filter(name=>!['postgres','redis','console'].includes(name));
export function validateRestoreManifestScope(manifest,capabilities=null){
 const names=new Set(restoreInputNames(manifest));if(manifest.schemaVersion!==1)throw Error('Invalid restore manifest');
 if(manifest.pipeline&&!manifest.delivery)throw Error('Pipeline restore requires delivery metadata');
 const required=[...dataVolumes.map(name=>name+'.tar'),'runtime.json','release.json','secrets/postgres-password'];
 const selected=new Set(dataVolumes);
 if(manifest.delivery){const volumes=validateDeliveryContext({delivery:manifest.delivery});required.push('delivery-runtime.json',...volumes.map(name=>name+'.tar'));for(const name of volumes)selected.add(name);}
 else if(names.has('delivery-runtime.json'))throw Error('Unexpected delivery metadata without delivery scope');
 if(manifest.pipeline){const volumes=validatePipelineContext({pipeline:manifest.pipeline});required.push('pipeline-runtime.json','verification-reports.tar',...volumes.map(name=>name+'.tar'));for(const name of volumes)selected.add(name);}
 else if(names.has('pipeline-runtime.json'))throw Error('Unexpected pipeline metadata without pipeline scope');
 const auxiliaryArchive=/^(?:delivery-(?:secrets|lkg-[ab])|pipeline-stage-[1-8]-(?:secrets|lkg-[ab]))\.tar$/;
 for(const name of names)if(auxiliaryArchive.test(name)&&!selected.has(name.slice(0,-4)))throw Error('Unexpected delivery or pipeline archive outside its declared scope');
 const notificationPresent=names.has(notificationStateName)||[...names].some(name=>name.startsWith('notification-secrets/')||notificationVolumes.some(volume=>name===volume+'.tar'));
 if(notificationPresent)required.push(...notificationBackupRequiredInputs);
 if(['secrets/cache-hmac',cacheReceiptName,'secrets-cache.tar'].some(name=>names.has(name)))required.push('secrets/cache-hmac',cacheReceiptName,'secrets-cache.tar');
 if(['secrets/login-protection-hmac',loginProtectionReceiptName].some(name=>names.has(name)))required.push('secrets/login-protection-hmac',loginProtectionReceiptName);
 if(capabilities){
  if(!Array.isArray(capabilities.coreSecrets)||capabilities.coreSecrets.some(name=>!/^[a-z0-9-]+$/.test(name))||
     ['cache','loginProtection','evidence','reports'].some(name=>typeof capabilities[name]!=='boolean'))throw Error('Restore release capabilities cannot be verified');
  required.push(...capabilities.coreSecrets.map(name=>'secrets/'+name));
  if(capabilities.cache)required.push('secrets/cache-hmac',cacheReceiptName,'secrets-cache.tar');
  if(capabilities.loginProtection)required.push('secrets/login-protection-hmac',loginProtectionReceiptName);
  if(capabilities.evidence)required.push('migration-evidence.tar');
  if(capabilities.reports)required.push('verification-reports.tar');
  if(!capabilities.cache&&names.has('secrets-cache.tar')||!capabilities.evidence&&names.has('migration-evidence.tar')||!capabilities.reports&&names.has('verification-reports.tar'))throw Error('Restore archive conflicts with source release capabilities');
 }
 if(manifest.usageJournal){
  if(manifest.usageJournal.path!=='usage-journal-inventory.json'||manifest.usageJournal.sha256!==manifest.files['usage-journal-inventory.json'])throw Error('Missing required usage inventory');
  required.push('usage-journal-inventory.json');
 }
 for(const name of required)if(!names.has(name))throw Error('Missing required restore manifest input: '+name);
 return names;
}
export function restoreManifestVolumeNames(manifest){
 const names=validateRestoreManifestScope(manifest),volumes=[...dataVolumes];
 if(manifest.delivery)volumes.push(...validateDeliveryContext({delivery:manifest.delivery}));
 if(manifest.pipeline)volumes.push(...validatePipelineContext({pipeline:manifest.pipeline}));
 for(const name of ['verification-reports','secrets-cache','migration-evidence'])if(names.has(name+'.tar'))volumes.push(name);
 if(names.has(notificationStateName))volumes.push(...notificationVolumes);
 return [...new Set(volumes)].sort();
}
export function restoreWritableVolumeNames(manifest,capabilities){
 validateRestoreManifestScope(manifest,capabilities);
 return [...new Set([...restoreManifestVolumeNames(manifest),'bootstrap-password',...['postgres','control-plane','worker','gateway-a','gateway-b','migrator'].map(name=>'secrets-'+name)])].sort();
}
const restoreScratch='/tmp:rw,nosuid,nodev,noexec,size=67108864';
export function validateRestoreInitializerConfiguration(ctx,manifest,notification,names,config){
 const projectName=ctx.state.projectName,ownerId=ctx.state.ownerId,expected=new Set(names),seen=new Set(),revision=ctx.release?.sourceRevision;
 if(!path.isAbsolute(ctx.directory??'')||!/^[a-f0-9]{40}$/.test(revision??'')||typeof ctx.release?.dependencyImages?.sdk!=='string')throw Error('Restore initializer release identity is invalid');
 const services=['init-volumes',...(notification?['init-notification-volumes']:[]),...(manifest.delivery?['init-delivery-storage']:[]),
  ...(manifest.pipeline?ctx.pipeline.stageGroups.filter(g=>g.kind==='Middle').map(g=>`init-pipeline-stage-${g.order}-storage`):[])];
 const auxiliary=new Set([...(notification?notificationVolumes:[]),...(manifest.delivery?deliveryVolumeNames:[]),
  ...(manifest.pipeline?ctx.pipeline.stageGroups.filter(g=>g.kind==='Middle').flatMap(g=>g.volumes):[])]);
 const artifact=path.join(ctx.directory,'artifacts',revision,'deploy/runtime');
 const bind=(source,target)=>({type:'bind',source,target,readOnly:true}),volume=(source,target)=>({type:'volume',source,target,readOnly:false});
 const expectedMounts={'init-volumes':[bind(path.join(ctx.directory,'secrets'),'/input'),bind(path.join(artifact,'init-volumes.sh'),'/etc/runtime/init-volumes.sh'),
  ...(ctx.migrationEvidenceSupported?[bind(path.join(artifact,'init-migration-evidence.sh'),'/etc/runtime/init-migration-evidence.sh')]:[]),
  ...names.filter(name=>!auxiliary.has(name)).map(name=>volume(name,'/volumes/'+name))]};
 if(notification)expectedMounts['init-notification-volumes']=[bind(path.join(ctx.directory,'notification-secrets'),'/input'),bind(path.join(artifact,'init-notification-volumes.sh'),'/etc/runtime/init-notification-volumes.sh'),
  volume('notification-secrets','/volumes/sender'),volume('notification-fixture-secrets','/volumes/fixture'),volume('notification-fixture-data','/volumes/data')];
 if(manifest.delivery)expectedMounts['init-delivery-storage']=deliveryVolumeNames.map((name,i)=>volume(name,['/out','/lkg-a','/lkg-b'][i]));
 if(manifest.pipeline)for(const group of ctx.pipeline.stageGroups.filter(g=>g.kind==='Middle'))expectedMounts[`init-pipeline-stage-${group.order}-storage`]=group.volumes.map((name,i)=>volume(name,['/out','/lkg-a','/lkg-b'][i]));
 const storageCommand=['bash','-c','set -e; chown -R 10001:10001 /out /lkg-a /lkg-b; chmod 700 /out /lkg-a /lkg-b'];
 if(config?.name!==projectName||!config.services||!config.volumes)throw Error('Restore initializer configuration identity is invalid');
 for(const name of services){
  const service=config.services[name];
  if(!service||service.read_only!==true||service.network_mode!=='none'||Number(service.mem_limit)!==268435456||
     JSON.stringify(service.tmpfs)!==JSON.stringify([restoreScratch])||service.privileged===true||service.volumes_from?.length||service.devices?.length||!Array.isArray(service.volumes)||
     service.image!==ctx.release.dependencyImages.sdk||service.user!=='0:0'||service.entrypoint!=null)throw Error('Restore initializer isolation is unverified');
  const command=name==='init-volumes'?['bash','/etc/runtime/init-volumes.sh']:name==='init-notification-volumes'?['bash','/etc/runtime/init-notification-volumes.sh']:storageCommand;
  if(JSON.stringify(service.command)!==JSON.stringify(command))throw Error('Restore initializer command is invalid');
  const actual=service.volumes.map(mount=>({type:mount.type,source:mount.source,target:mount.target,readOnly:mount.read_only===true}));
  const key=mount=>JSON.stringify([mount.type,mount.source,mount.target,mount.readOnly]);
  if(JSON.stringify(actual.map(key).sort())!==JSON.stringify(expectedMounts[name].map(key).sort()))throw Error('Restore initializer mount mapping is invalid');
  for(const mount of service.volumes){
   if(mount?.type==='bind'&&mount.read_only===true)continue;
   if(mount?.type!=='volume'||mount.read_only===true||!expected.has(mount.source)||typeof mount.target!=='string'||!mount.target.startsWith('/')||mount.target==='/tmp'||mount.target.startsWith('/tmp/'))throw Error('Restore initializer has an unbounded writable mount');
   const volume=config.volumes[mount.source];
   if(!volume||volume.external===true||volume.name!==projectName+'_'+mount.source||volume.labels?.['com.webapi.runtime.owner']!==ownerId)throw Error('Restore initializer volume identity is invalid');
   seen.add(mount.source);
  }
 }
 if(seen.size!==expected.size||[...expected].some(name=>!seen.has(name)))throw Error('Restore initializer writable scope is incomplete');
 return true;
}
function restoreVolumeIdentities(state,resources,names){
 assertOwnership(state,resources);const identities={};
 for(const name of [...names].sort()){
  const matches=resources.filter(item=>item.Kind==='volume'&&item.Name===state.projectName+'_'+name);
  if(matches.length!==1)throw Error('Restore target volume identity is unavailable');
  const item=matches[0],labels=Object.entries(item.Labels??{}).sort(([left],[right])=>left.localeCompare(right));
  identities[name]=createHash('sha256').update(JSON.stringify([item.Name,item.Driver,item.Scope,item.CreatedAt,item.Mountpoint,item.Options,labels])).digest('hex');
 }
 return identities;
}
// Empty named volumes are created with fresh owner/creation labels first. A
// trusted provider must bind every identity to an enforced quota before any
// initializer is allowed to populate those volumes. Failed fixtures remain.
export async function provisionRestoreWritableVolumes(ctx,manifest,notification,{names,budget,storageVerifier,previous,run=docker,inspect=inspectResources,persist=persistRestoreStorageProof,initialize=initializeRestoreVolumes,prepareEvidence=prepareMigrationEvidenceVolume,markEvidenceReady=markMigrationEvidenceVolumeReady}={}){
 const {state,directory}=ctx;validateFixture(state.projectName,directory);
 if(!Array.isArray(names)||!names.length||new Set(names).size!==names.length||names.some(name=>!/^[a-z0-9-]+$/.test(name)))throw Error('Invalid writable restore volume scope');
 budget=requireRestoreIntegration({restoreBudget:budget,storageVerifier});
 requireBoundRestoreProof(previous,{budget,provider:storageVerifier,projectName:state.projectName,directory,volumeNames:names,phase:'preflight'});
 const initial=await inspect(state);assertOwnership(state,initial);if(initial.length)throw Error('Restore target already has resources; retain and inspect before retry');
 const nonces=new Map();let evidenceCreated=false;
 for(const name of [...names].sort()){
  if(name==='migration-evidence'){
   if(!ctx.migrationEvidenceSupported)throw Error('Unexpected migration evidence volume');
   evidenceCreated=await prepareEvidence(state,{directory,allowCreate:true,inspect,run});
   if(!evidenceCreated)throw Error('Restore evidence volume was not newly created');
   continue;
  }
  const nonce=randomBytes(32).toString('hex'),fullName=state.projectName+'_'+name;
  const labels={'com.docker.compose.project':state.projectName,'com.docker.compose.volume':name,'com.webapi.runtime.owner':state.ownerId,'com.webapi.restore.creation-nonce':nonce};
  const args=['volume','create'];for(const [key,value] of Object.entries(labels))args.push('--label',key+'='+value);args.push(fullName);
  const result=await run(args);if(result.stdout.trim()!==fullName)throw Error('Restore volume creation identity is unverified');nonces.set(name,nonce);
 }
 const verify=async()=>{
  const resources=await inspect(state);assertOwnership(state,resources);
  if(resources.some(item=>item.Kind!=='volume'||!names.some(name=>item.Name===state.projectName+'_'+name)))throw Error('Unexpected restore resource before initialization');
  for(const [name,nonce] of nonces){const item=resources.find(r=>r.Kind==='volume'&&r.Name===state.projectName+'_'+name);
   if(!item||item.Driver!=='local'||item.Scope!=='local'||item.Options&&Object.keys(item.Options).length||item.Labels?.['com.docker.compose.volume']!==name||item.Labels?.['com.webapi.restore.creation-nonce']!==nonce)throw Error('Restore volume creation identity changed');}
  return restoreVolumeIdentities(state,resources,names);
 };
 const identities=await verify(),proof=await requireRestoreStorageProof({provider:storageVerifier,budget,phase:'before-initialization',projectName:state.projectName,directory,volumeNames:names,ownerId:state.ownerId,volumeIdentities:identities,previous});
 await persist(directory,proof);
 if(JSON.stringify(await verify())!==JSON.stringify(identities))throw Error('Restore target volume identity changed before initialization');
 const permit=Object.freeze({});initializerPermits.set(permit,{ctx,manifest,notification,evidenceCreated,names:[...names]});
 try{await initialize(ctx,manifest,notification,{permit,markMigrationEvidenceVolumeReady:markEvidenceReady});}finally{initializerPermits.delete(permit);}
 return {identities,proof};
}
function normalizedRestoreTarget(target){if(typeof target!=='string'||!target||/[\u0000\r\n]/.test(target))throw Error('Invalid restore backup path');return path.resolve(target);}
export async function canonicalRestoreDirectory(directory){
 if(typeof directory!=='string'||!directory||/[\u0000\r\n,:]/.test(directory))throw Error('Invalid restore directory path');
 const target=path.resolve(directory),missing=[];let ancestor=path.dirname(target);
 for(;;){
  try{const canonical=path.join(await fs.realpath(ancestor),...missing,path.basename(target));if(/[\u0000\r\n,:]/.test(canonical))throw Error('Invalid canonical restore directory path');return canonical;}
  catch(error){if(error.code!=='ENOENT')throw error;const parent=path.dirname(ancestor);if(parent===ancestor)throw error;missing.unshift(path.basename(ancestor));ancestor=parent;}
 }
}
export function restoreVolumeExtractionArgs(ctx,target,volumes,frozen){
 target=normalizedRestoreTarget(target);
 if(/[\u0000\r\n,]/.test(target)||!Array.isArray(volumes)||!volumes.length||new Set(volumes).size!==volumes.length||volumes.some(n=>!/^[a-z0-9-]+$/.test(n)))throw Error('Invalid restore extraction scope');
 validateFixture(ctx.state.projectName,ctx.directory);
 const includesEvidence=volumes.includes('migration-evidence');
 if(includesEvidence!==Boolean(ctx.migrationEvidenceSupported)||includesEvidence!==Boolean(frozen))throw Error('Invalid restore evidence extraction scope');
 if(frozen&&(path.dirname(frozen.path??'')!==ctx.directory||/[\u0000\r\n,:]/.test(frozen.path??'')||!/^[a-f0-9]{64}$/.test(frozen.sha256??'')))throw Error('Invalid fixed archive identity');
 // Notification comparison materializes secrets only on bounded tmpfs, not
 // the Docker writable layer. The target volumes remain separately scoped.
 const args=['run','--rm','--pull','never','--network','none','--read-only','--memory','256m','--cpus','1','--tmpfs','/tmp:rw,nosuid,nodev,noexec,size=67108864','--user','0:0','--mount',`type=bind,src=${target},dst=/backup,readonly`];
 if(frozen)args.push('--mount',`type=bind,src=${frozen.path},dst=/verified-migration-evidence.tar,readonly`);
 for(const name of volumes)args.push('--mount',`type=volume,src=${ctx.state.projectName}_${name},dst=/volumes/${name}`);
 args.push(ctx.release.dependencyImages.sdk,'bash','-c','set -e; for name in '+volumes.join(' ')+'; do if [ "$name" = migration-evidence ]; then test -z "$(ls -A /volumes/$name)"; tar -xf /verified-migration-evidence.tar -C /volumes/$name; elif [ "$name" = secrets-cache ]; then tar -xOf /backup/$name.tar ./cache-hmac | sha256sum | cut -d " " -f1 > /tmp/cache-archive.sha; sha256sum /volumes/$name/cache-hmac | cut -d " " -f1 > /tmp/cache-seeded.sha; cmp /tmp/cache-archive.sha /tmp/cache-seeded.sha; elif [ "$name" = notification-secrets ] || [ "$name" = notification-fixture-secrets ]; then mkdir /tmp/check-$name; tar -xf /backup/$name.tar -C /tmp/check-$name; diff -r /tmp/check-$name /volumes/$name; else test -z "$(ls -A /volumes/$name)"; tar -xf /backup/$name.tar -C /volumes/$name; fi; done');
 return args;
}
export async function coldBackup(ctx,target,options={}){
 validateFixture(ctx.state.projectName,ctx.directory);return createBackup(ctx,target,options);
}
export async function createBackup(ctx,target,{contextFactory=prepareContext}={}){
 const directory=ctx.directory;await ownedDirectory(directory);
 return withRuntimeLock(directory,async()=>{await assertRestoreComplete(directory);const current=await loadState(directory);if(current.ownerId!==ctx.state.ownerId||current.projectName!==ctx.state.projectName)throw Error('Backup runtime owner mismatch');if(!current.initialized||current.pendingConfiguration||current.bindingPhase==='Validated')throw Error('Backup requires initialized runtime with no pending configuration; complete configure retry first');const release=await loadRelease(path.join(directory,'release.json'));return writeBackup(await contextFactory(directory,current,release),target);});
}
async function writeBackup(ctx,target){
 const volumes=backupVolumeNames(ctx);const resources=await inspectResources(ctx.state);assertOwnership(ctx.state,resources);for(const name of volumes)if(!resources.some(r=>r.Kind==='volume'&&r.Name===ctx.state.projectName+'_'+name))throw Error('Backup requires every owned data volume to exist');await fs.mkdir(target,{mode:0o700});return withBackupServicesPaused(ctx,async()=>{await ctx.compose('up','-d','--wait','--no-recreate','postgres');
 await ctx.compose('exec','-T','postgres','pg_dump','-U','webapi','-d','webapi','--format=custom','--file=/tmp/local-runtime.dump');const pg=(await inspectResources(ctx.state)).find(r=>r.Kind==='container'&&r.Config?.Labels?.['com.docker.compose.service']==='postgres');await docker(['cp',pg.Id+':/tmp/local-runtime.dump',path.join(target,'postgres.dump')]);await fs.chmod(path.join(target,'postgres.dump'),0o600);await ctx.compose('stop','postgres','redis');
 const args=['run','--rm','--user','0:0','--mount',`type=bind,src=${target},dst=/backup`];for(const name of volumes)args.push('--mount',`type=volume,src=${ctx.state.projectName}_${name},dst=/volumes/${name},readonly`);if(ctx.migrationEvidenceSupported)args.push('--mount',`type=bind,src=${path.join(ctx.directory,'artifacts',ctx.release.sourceRevision,'deploy/runtime/archive-migration-evidence.sh')},dst=/archive-evidence,readonly`);args.push(ctx.release.dependencyImages.sdk,'bash','-c','set -e; umask 077; for name in '+volumes.join(' ')+'; do if [ "$name" = migration-evidence ]; then bash /archive-evidence /volumes/$name /backup/$name.tar; else tar --numeric-owner -cf /backup/$name.tar -C /volumes/$name .; fi; done');await docker(args);
 const evidenceInspection=ctx.migrationEvidenceSupported?await inspectMigrationEvidenceArchive(path.join(target,'migration-evidence.tar'),ctx.release.imageId):null;
 await fs.cp(path.join(ctx.directory,'secrets'),path.join(target,'secrets'),{recursive:true});await fs.copyFile(path.join(ctx.directory,'release.json'),path.join(target,'release.json'));await writePrivate(path.join(target,'runtime.json'),ctx.state);await copySsoBackup(ctx.directory,target,ctx.state);await copyCacheBackup(ctx.directory,target,ctx.state);await copyLoginProtectionBackup(ctx.directory,target,ctx.state);const notificationHashes=await copyNotificationBackup(ctx.directory,target,ctx.state);const usageJournal=await writeUsageBackupInventory(target,volumes);const hashes={...notificationHashes,[usageJournal.path]:usageJournal.sha256};if(ctx.pipeline){await fs.copyFile(path.join(ctx.directory,'pipeline-runtime.json'),path.join(target,'pipeline-runtime.json'));await fs.chmod(path.join(target,'pipeline-runtime.json'),0o600);hashes['pipeline-runtime.json']=createHash('sha256').update(await fs.readFile(path.join(target,'pipeline-runtime.json'))).digest('hex');}if(ctx.delivery){await fs.copyFile(path.join(ctx.directory,'delivery-runtime.json'),path.join(target,'delivery-runtime.json'));await fs.chmod(path.join(target,'delivery-runtime.json'),0o600);hashes['delivery-runtime.json']=createHash('sha256').update(await fs.readFile(path.join(target,'delivery-runtime.json'))).digest('hex');}for(const file of ['postgres.dump',...volumes.map(n=>n+'.tar'),'runtime.json','release.json']){await fs.chmod(path.join(target,file),0o600);hashes[file]=file==='migration-evidence.tar'?evidenceInspection.sha256:createHash('sha256').update(await fs.readFile(path.join(target,file))).digest('hex');}for(const file of await fs.readdir(path.join(target,'secrets')))hashes['secrets/'+file]=createHash('sha256').update(await fs.readFile(path.join(target,'secrets',file))).digest('hex');for(const file of [ssoConfigName,cacheReceiptName,loginProtectionReceiptName,...['local-demo-api-approver.password','local-demo-security-reviewer.password','consumer-api-key'].map(n=>'demo/'+n)]){try{hashes[file]=createHash('sha256').update(await fs.readFile(path.join(target,file))).digest('hex');}catch(e){if(e.code!=='ENOENT')throw e;}}await writePrivate(path.join(target,'backup-manifest.json'),{schemaVersion:1,sourceRevision:ctx.release.sourceRevision,files:hashes,usageJournal,...(evidenceInspection?{migrationEvidence:evidenceInspection}:{}),...(ctx.delivery?{delivery:ctx.delivery}:{}),...(ctx.pipeline?{pipeline:ctx.pipeline}:{})});return hashes;});
}
export async function restoreColdBackup({target,projectName,directory,ports,notificationPorts,contextFactory,capabilityResolver=resolveRestoreReleaseCapabilities,restoreBudget,storageVerifier}){
 validateFixture(projectName,directory);target=normalizedRestoreTarget(target);
 const resolvedDirectory=await canonicalRestoreDirectory(directory);directory=resolvedDirectory;
 if(await fs.lstat(directory).catch(e=>{if(e.code==='ENOENT')return null;throw e;}))throw Error('Restore requires a fresh disposable directory');
 const manifest=await readRestoreManifest(path.join(target,'backup-manifest.json'));validateRestoreManifestScope(manifest);
 const optionalArchives=[...notificationVolumes,...deliveryVolumeNames,...Array.from({length:8},(_,i)=>['secrets','lkg-a','lkg-b'].map(name=>`pipeline-stage-${i+1}-${name}`)).flat()].map(name=>name+'.tar');
 await requireRestoreSourceCoverage(target,manifest,{directories:['secrets','notification-secrets','demo'],optionalFiles:[ssoConfigName,cacheReceiptName,loginProtectionReceiptName,notificationStateName,'pipeline-runtime.json','delivery-runtime.json','usage-journal-inventory.json','verification-reports.tar','secrets-cache.tar','migration-evidence.tar',...optionalArchives]});
 let releasePreflight,previousPreflight;
 try{releasePreflight=JSON.parse((await readVerifiedRestoreInput(target,'release.json',manifest)).toString('utf8'));previousPreflight=JSON.parse((await readVerifiedRestoreInput(target,'runtime.json',manifest)).toString('utf8'));}
 catch{throw Error('Restore release or runtime metadata is invalid');}
 validateState(previousPreflight);verifyRestoreRevisionIdentity(manifest,releasePreflight,previousPreflight);
 const auxiliary={};for(const [key,name] of [['pipeline','pipeline-runtime.json'],['delivery','delivery-runtime.json']])if(manifest[key]){
  try{auxiliary[key]=parseRestoreManifest(new TextDecoder('utf-8',{fatal:true}).decode(await readVerifiedRestoreInput(target,name,manifest)));}
  catch{throw Error('Restore '+key+' metadata is invalid');}
 }
 validateRestoreAuxiliaryMetadata(manifest,previousPreflight,auxiliary);
 const capabilities=await capabilityResolver(releasePreflight);validateRestoreManifestScope(manifest,capabilities);
 const budget=validateRestoreStorageBudget(restoreBudget),expectedVolumes=restoreManifestVolumeNames(manifest),writableVolumes=restoreWritableVolumeNames(manifest,capabilities);
 const storageProof=await requireRestoreStorageProof({provider:storageVerifier,budget,phase:'preflight',projectName,directory:resolvedDirectory,volumeNames:writableVolumes});
 const snapshotParent=path.dirname(resolvedDirectory);await ensureDirectoryDurable(snapshotParent);
 if(await fs.realpath(snapshotParent)!==snapshotParent)throw Error('Restore target parent identity changed');
 return withRestoreTargetLock(directory,async(lockedDirectory,targetLock)=>{
 if(lockedDirectory!==resolvedDirectory)throw Error('Restore target identity changed');
 const restoreIntent=await createRestoreIntent(snapshotParent,storageProof,{targetLock});
 const restoreInputs=await freezeRestoreInputs(target,snapshotParent,manifest,{budget,operationId:storageProof.operationId});target=restoreInputs.directory;
 try{
 await persistRestoreStorageProof(restoreInputs.directory,storageProof);
 const usageInventory=await verifyUsageBackupInventory(target,usageRestoreVolumeNames(manifest,[...dataVolumes,...validateDeliveryContext({delivery:manifest.delivery}),...validatePipelineContext({pipeline:manifest.pipeline})]),manifest.usageJournal);const restoreRelease=await loadRelease(path.join(target,'release.json'));const evidenceInspection=Object.hasOwn(manifest.files,'migration-evidence.tar')?await inspectMigrationEvidenceArchive(path.join(target,'migration-evidence.tar'),restoreRelease.imageId,{expectedHash:manifest.files['migration-evidence.tar']}):null;const usageCapability=Object.values(usageInventory.volumes).some(v=>v.status==='present')?await inspectUsageRebindCapability(restoreRelease):null;requireSafeUsageCloneRestore(usageInventory,usageCapability?{release:restoreRelease,...usageCapability}:undefined);
 const previous=JSON.parse(await fs.readFile(path.join(target,'runtime.json'),'utf8'));let state=await initializeState(directory,{projectName,ports,bootstrapUsername:previous.bootstrapUsername,requireFresh:true,restorePendingSourceOwnerId:previous.ownerId,restoreIntent,targetLock});await writeRestoreReference(directory,state,restoreIntent,restoreInputs.directory);state={...previous,ownerId:state.ownerId,projectName,ports};await saveState(directory,state);await fs.cp(path.join(target,'secrets'),path.join(directory,'secrets'),{recursive:true});await restoreInputSecretModes(directory,restoreInputs);await fs.copyFile(path.join(target,'release.json'),path.join(directory,'release.json'));const release=JSON.parse(await fs.readFile(path.join(directory,'release.json'),'utf8'));await restoreSsoBackup(target,directory,state);await restoreCacheBackup(target,directory,state);await restoreLoginProtectionBackup(target,directory,state);const notification=await restoreNotificationBackup(target,directory,state,notificationPorts);if(notification)await restoreInputSecretModes(directory,restoreInputs,'notification-secrets/');if(manifest.pipeline){if(!Object.hasOwn(manifest.files,'verification-reports.tar'))throw Error('Pipeline restore requires protected reports');if(manifest.pipeline.ownerId!==previous.ownerId)throw Error('Backup pipeline owner mismatch');validatePipelineContext({pipeline:manifest.pipeline});await fs.copyFile(path.join(target,'pipeline-runtime.json'),path.join(directory,'pipeline-runtime.json'));}if(manifest.delivery)await fs.copyFile(path.join(target,'delivery-runtime.json'),path.join(directory,'delivery-runtime.json'));const ctx=await contextFactory(directory,state,release);
 if(Boolean(ctx.migrationEvidenceSupported)!==capabilities.evidence||Boolean(ctx.reportsSupported)!==capabilities.reports||
    Boolean(ctx.cacheSupported)!==capabilities.cache||Boolean(ctx.securitySupported)!==capabilities.loginProtection)throw Error('Restore context differs from verified source release capabilities');
 if(ctx.reportsSupported&&!Object.hasOwn(manifest.files,'verification-reports.tar'))throw Error('Missing required verification report archive');
 if(ctx.cacheSupported&&!Object.hasOwn(manifest.files,'secrets-cache.tar'))throw Error('Missing required cache secret archive');
 if(migrationEvidenceRestoreHeld(ctx,evidenceInspection))return{state,ctx,hashesVerified:true,restoreComplete:false,restoreReason:'migration_evidence_pending_review',usageRebind:null,migrationEvidence:evidenceInspection,restoreInputs:restoreInputs.directory};
 const frozen=evidenceInspection?await freezeMigrationEvidenceArchive(path.join(target,'migration-evidence.tar'),ctx.directory,evidenceInspection.sha256):null;
 if(frozen){
  await verifyFrozenMigrationEvidenceArchive(frozen);
  const fixedInspection=await inspectMigrationEvidenceArchive(frozen.path,release.imageId,{expectedHash:evidenceInspection.sha256});
  if(fixedInspection.attempts!==evidenceInspection.attempts||fixedInspection.pendingAttempts!==0||fixedInspection.contentHash!==evidenceInspection.contentHash)throw Error('Fixed migration evidence inspection changed');
 }
 const provisioned=await provisionRestoreWritableVolumes(ctx,manifest,notification,{names:writableVolumes,budget,storageVerifier,previous:storageProof});
 await persistRestoreStorageProof(restoreInputs.directory,provisioned.proof);
 const resources=await inspectResources(state);assertOwnership(state,resources);
 const volumes=[...new Set([...(manifest.pipeline?validatePipelineContext(ctx):[]),...(manifest.delivery?deliveryVolumeNames:[]),...dataVolumes,...(Object.hasOwn(manifest.files,'verification-reports.tar')?['verification-reports']:[]),...(Object.hasOwn(manifest.files,'secrets-cache.tar')?['secrets-cache']:[]),...(ctx.migrationEvidenceSupported?['migration-evidence']:[]),...(notification?notificationVolumes:[])])];
 if(JSON.stringify([...volumes].sort())!==JSON.stringify(expectedVolumes))throw Error('Restore volume scope changed after preflight');
 const volumeIdentities=restoreVolumeIdentities(state,resources,writableVolumes);
 if(JSON.stringify(volumeIdentities)!==JSON.stringify(provisioned.identities))throw Error('Restore target volume identity changed during initialization');
 const targetStorageProof=await requireRestoreStorageProof({provider:storageVerifier,budget,phase:'before-extraction',projectName,directory:resolvedDirectory,volumeNames:writableVolumes,ownerId:state.ownerId,volumeIdentities,previous:provisioned.proof});
 await persistRestoreStorageProof(directory,targetStorageProof);
 await persistRestoreStorageProof(restoreInputs.directory,targetStorageProof);
 if(JSON.stringify(restoreVolumeIdentities(state,await inspectResources(state),writableVolumes))!==JSON.stringify(volumeIdentities))throw Error('Restore target volume identity changed');
 const args=restoreVolumeExtractionArgs(ctx,target,volumes,frozen);
 if(frozen)await verifyFrozenMigrationEvidenceArchive(frozen);
 await docker(args,{maxOutputBytes:1024*1024});
 if(frozen)await verifyFrozenMigrationEvidenceArchive(frozen);
 const verified=frozen?await verifyRestoredMigrationEvidence(ctx,frozen,evidenceInspection):null;
 if(notification){await docker(['run','--rm','--user','10001:10001','--label','com.docker.compose.project='+state.projectName,'--label','com.webapi.runtime.owner='+state.ownerId,'--mount','type=volume,src='+projectName+'_notification-fixture-data,dst=/var/fixture',release.imageId,'dotnet','/app/notification-fixture/WebApi.NotificationFixtureHost.dll','rebind-journal','/var/fixture',previous.ownerId,previous.projectName,state.ownerId,state.projectName]);}const usageRebind=usageCapability?await rebindRestoredUsage(ctx,previous.ownerId,usageInventory,usageCapability):null;if(frozen)await verifyFrozenMigrationEvidenceArchive(frozen);await verifyFrozenRestoreInputs(restoreInputs);const restoreComplete=await finishMigrationEvidenceRestore(directory,state,evidenceInspection,verified,{beforeFinish:()=>writeRestoreCompletion(directory,state,restoreIntent,restoreInputs.directory)});return{state,ctx,hashesVerified:true,restoreComplete,usageRebind,migrationEvidence:evidenceInspection,migrationContinuity:frozen?'Unknown':null,restoreInputs:restoreInputs.directory};
 }catch(error){if(error&&typeof error==='object')try{error.retainedRestoreInputs=restoreInputs.directory;}catch{}throw error;}
 },{retainOnError:true});
}

export function planRestoreVolumeInitialization(ctx,manifest,notification,{precreatedEvidence=false}={}){
 if(manifest.pipeline){validatePipelineContext({pipeline:manifest.pipeline});validatePipelineContext(ctx);if(JSON.stringify(pipelineStageTopology(manifest.pipeline.stageGroups))!==JSON.stringify(pipelineStageTopology(ctx.pipeline.stageGroups)))throw Error('Restore pipeline context mismatch');}else if(ctx.pipeline)throw Error('Unexpected pipeline restore context');
 if(manifest.delivery){validateDeliveryContext({delivery:manifest.delivery});validateDeliveryContext(ctx);if(JSON.stringify(ctx.delivery)!==JSON.stringify(manifest.delivery))throw Error('Restore delivery context mismatch');}else if(ctx.delivery)throw Error('Unexpected delivery restore context');
 const evidenceArchive=manifest.files?.['migration-evidence.tar'];if(ctx.migrationEvidenceSupported&&!/^[a-f0-9]{64}$/.test(evidenceArchive??''))throw Error('Migration evidence archive is required for restore');if(!ctx.migrationEvidenceSupported&&evidenceArchive)throw Error('Unexpected migration evidence archive for this release');
 if(ctx.migrationEvidenceSupported&&!precreatedEvidence)throw Error('Precreated migration evidence volume is required');
 const initArgs=['run','--rm','--pull','never','--no-deps'];if(precreatedEvidence)initArgs.push('-e','WEBAPI_MIGRATION_EVIDENCE_NEW_VOLUME=1');initArgs.push('init-volumes');
 const commands=[initArgs];if(notification)commands.push(['run','--rm','--pull','never','--no-deps','init-notification-volumes']);
 if(manifest.delivery)commands.push(['run','--rm','--pull','never','--no-deps','init-delivery-storage']);
 if(manifest.pipeline)for(const group of ctx.pipeline.stageGroups.filter(g=>g.kind==='Middle'))commands.push(['run','--rm','--pull','never','--no-deps',`init-pipeline-stage-${group.order}-storage`]);
 return {commands,markEvidenceReady:precreatedEvidence};
}
export async function initializeRestoreVolumes(ctx,manifest,notification,{permit,markMigrationEvidenceVolumeReady:markEvidenceReady=markMigrationEvidenceVolumeReady}={}){
 const grant=initializerPermits.get(permit);if(!grant||grant.ctx!==ctx||grant.manifest!==manifest||grant.notification!==notification)throw Error('Restore initialization authorization is required');
 initializerPermits.delete(permit);
 const plan=planRestoreVolumeInitialization(ctx,manifest,notification,{precreatedEvidence:grant.evidenceCreated});
 if(!Array.isArray(ctx.files))throw Error('Restore initializer context cannot be bounded');
 const services=Object.fromEntries(plan.commands.map(args=>[args.at(-1),{read_only:true,network_mode:'none',mem_limit:'256m',tmpfs:[restoreScratch]}]));
 const overlay=path.join(ctx.directory,'restore-initializer-boundary-'+randomBytes(16).toString('hex')+'.json');
 const handle=await fs.open(overlay,'wx',0o600);try{await handle.writeFile(JSON.stringify({services})+'\n');await handle.sync();}finally{await handle.close();}
 const parent=await fs.open(ctx.directory,'r');try{await parent.sync();}finally{await parent.close();}
 ctx.files.push('-f',overlay);
 const expanded=await ctx.compose('--profile','tools','config','--format','json');let configuration;
 try{configuration=JSON.parse(expanded.stdout);}catch{throw Error('Restore initializer configuration is unavailable');}
 validateRestoreInitializerConfiguration(ctx,manifest,notification,grant.names,configuration);
 for(const [i,args] of plan.commands.entries()){await ctx.compose(...args);if(i===0&&plan.markEvidenceReady)await markEvidenceReady(ctx.state,{directory:ctx.directory});}
}

export async function withBackupServicesPaused(ctx,action,{wait=waitForRuntime}={}){try{await ctx.compose('stop',...[...new Set([...backupStopServices(ctx.state,ctx.notification),...(validateDeliveryContext(ctx).length?deliveryServices:[]),...pipelineServices(ctx)])]);return await action();}finally{await ctx.compose('up','-d','--no-recreate',...[...new Set([...runtimeServices(ctx.state,ctx.notification),...(ctx.delivery?deliveryServices:[]),...pipelineServices(ctx)])]);if(ctx.securitySupported)await synchronizeConsoleProxyTrust(ctx);await wait(ctx);}}
