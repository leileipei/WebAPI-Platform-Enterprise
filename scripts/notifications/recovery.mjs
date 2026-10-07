import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import {createHash,randomUUID} from 'node:crypto';
import {validateState,loadState,writePrivate} from '../runtime/state.mjs';
import {validateFixture} from '../runtime/fixture-guard.mjs';
import {prepareContext} from '../runtime/context.mjs';
import {loadRelease,runtimeServices,waitForRuntime} from '../runtime/lifecycle.mjs';
import {coldBackup,restoreColdBackup,backupVolumeNames} from '../runtime/acceptance-backup.mjs';
import {getRuntimeStatus} from '../runtime/status.mjs';
import {inspectResources,assertOwnership,docker} from '../runtime/docker.mjs';
import {validateCloneOwnership,assertReleaseIdentity} from '../contracts/evidence.mjs';
import {until} from '../runtime/acceptance-scenario.mjs';
import {fixtureRequest} from './fixture.mjs';
import {loadNotificationSecrets,validateNotificationRuntime} from './runtime-secrets.mjs';
import {executeNotificationSql} from './scenario.mjs';
const sha=b=>createHash('sha256').update(b).digest('hex');
export function validateNotificationRecoveryTarget(source,target){
 validateState(source);validateFixture(source.projectName,source.projectName);validateFixture(target.projectName,target.directory);
 assert.notEqual(target.projectName,source.projectName,'Restore requires a new disposable identity');
 const next=validateState({...source,ownerId:randomUUID(),projectName:target.projectName,ports:target.ports});
 assert(Object.values(next.ports).every(p=>p>1024&&!Object.values(source.ports).includes(p)&&![4192,4193,4196,4197].includes(p)),'Restore ports must be separate from original and source');
 validateNotificationRuntime({schemaVersion:1,ownerId:next.ownerId,projectName:next.projectName,enabled:true,fixtureEnabled:true,ports:target.notificationPorts,secretReceipt:'notification-secrets/receipt.json'},next);
 assert(Object.values(target.notificationPorts).every(p=>!Object.values(source.ports).includes(p)),'Notification ports collide with source');return next;
}
async function keyHashes(state,release){
 const name=state.projectName+'_dp-keys',volumes=JSON.parse((await docker(['volume','inspect',name])).stdout);assertOwnership(state,volumes);
 const output=await docker(['run','--rm','--user','10001:10001','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges:true','--label','com.docker.compose.project='+state.projectName,'--label','com.webapi.runtime.owner='+state.ownerId,'--mount','type=volume,src='+name+',dst=/keys,readonly',release.dependencyImages.sdk,'find','/keys','-type','f','-exec','sha256sum','{}','+']);
 const rows=output.stdout.trim().split('\n').filter(Boolean).map(line=>{const match=/^([a-f0-9]{64})\s+\/keys\/(.+)$/.exec(line);assert(match,'Invalid private key hash output');return [match[2],match[1]];}).sort(([a],[b])=>a.localeCompare(b));assert(rows.length,'Persistent DP keys missing');return Object.fromEntries(rows);
}
export async function runNotificationColdRecovery({cloneDirectory,backupDirectory,restoreDirectory,restoreProjectName,ports,notificationPorts}){
 const state=await loadState(cloneDirectory),release=await loadRelease(path.join(cloneDirectory,'release.json')),resources=await inspectResources(state);validateCloneOwnership(state,resources,cloneDirectory);
 validateNotificationRecoveryTarget(state,{projectName:restoreProjectName,directory:restoreDirectory,ports,notificationPorts});
 const applications=resources.filter(r=>r.Kind==='container'&&['control-plane','worker','console','gateway-a','gateway-b','notification-fixture'].includes(r.Config?.Labels?.['com.docker.compose.service']));assert(applications.length>=6&&applications.every(r=>r.State.Running));
 assertReleaseIdentity({revision:release.sourceRevision,release,actualImages:await Promise.all(applications.map(async app=>{const image=JSON.parse((await docker(['image','inspect',app.Image])).stdout)[0];return {imageId:image.Id,sourceRevision:image.Config.Labels['org.opencontainers.image.revision']};}))});
 const ctx=await prepareContext(cloneDirectory,state,release),pg=resources.find(r=>r.Kind==='container'&&r.Config?.Labels?.['com.docker.compose.service']==='postgres');assert(pg);
 assert.equal(await executeNotificationSql(state,pg,"SELECT count(*) FROM notification_deliveries WHERE status='Sending';"),'0','Drain actual sends before cold backup');
 const protocol=(await fixtureRequest(cloneDirectory,state,'/owner/observations')).protocol,keys=await keyHashes(state,release),secrets=await loadNotificationSecrets(path.join(cloneDirectory,'notification-secrets'),state.ownerId,state.projectName);
 await coldBackup(ctx,backupDirectory);const manifestBytes=await fs.readFile(path.join(backupDirectory,'backup-manifest.json')),manifest=JSON.parse(manifestBytes);assert.equal(manifest.sourceRevision,release.sourceRevision);
 for(const [file,expected]of Object.entries(manifest.files)){assert(!path.isAbsolute(file)&&!file.split('/').includes('..'));assert.equal(sha(await fs.readFile(path.join(backupDirectory,file))),expected,'Backup archive checksum');}
 const originalStatus=await until(()=>getRuntimeStatus(state,{directory:cloneDirectory}),s=>s.phase==='Ready'&&s.notifications.fixture==='Available',60);
 const restored=await restoreColdBackup({target:backupDirectory,projectName:restoreProjectName,directory:restoreDirectory,ports,notificationPorts,contextFactory:prepareContext});
 await restored.ctx.compose('up','-d',...runtimeServices(restored.state,restored.ctx.notification));await waitForRuntime(restored.ctx);
 const status=await until(()=>getRuntimeStatus(restored.state,{directory:restoreDirectory}),s=>s.phase==='Ready'&&s.notifications.fixture==='Available',60),fixture=await fixtureRequest(restoreDirectory,restored.state,'/owner/observations'),restoredKeys=await keyHashes(restored.state,release),restoredSecrets=await loadNotificationSecrets(path.join(restoreDirectory,'notification-secrets'),restored.state.ownerId,restored.state.projectName);
 assert.equal(fixture.sourceRevision,release.sourceRevision);assert.equal(fixture.binaryHash,release.applications['notification-fixture/WebApi.NotificationFixtureHost.dll']);assert(fixture.binaryVerified);assert.deepEqual(fixture.protocol,protocol);assert.deepEqual(restoredKeys,keys);assert.deepEqual(restoredSecrets.files,secrets.files);
 const identity={sourceRevision:release.sourceRevision,imageId:release.imageId};const receipt={...identity,source:'actual-clone',ownerId:state.ownerId,projectName:state.projectName,manifestSha256:sha(manifestBytes),manifestFilesVerified:Object.keys(manifest.files).length,volumes:backupVolumeNames(ctx),finallyReady:originalStatus.phase==='Ready',restore:{...identity,actual:true,ownerVerified:true,ownerId:restored.state.ownerId,projectName:restored.state.projectName,consolePort:ports.console,fixtureBinaryVerified:fixture.binaryVerified,fixtureProtocolBefore:sha(JSON.stringify(protocol)),fixtureProtocolAfter:sha(JSON.stringify(fixture.protocol)),dpKeysBefore:keys,dpKeysAfter:restoredKeys,secretHashesBefore:secrets.files,secretHashesAfter:restoredSecrets.files,finallyReady:status.phase==='Ready'}};
 await writePrivate(path.join(cloneDirectory,'notification-evidence/cold-recovery.json'),receipt);await writePrivate(path.join(cloneDirectory,'notification-evidence/cold-backup-manifest.json'),manifest);return receipt;
}
