import assert from 'node:assert/strict';import {validateBridgeReceipt} from './bridge.mjs';
import fs from 'node:fs/promises';import path from 'node:path';import {createHash,randomUUID,randomBytes} from 'node:crypto';
import {loadState,writePrivate,withRuntimeLock,validGuid} from '../runtime/state.mjs';
import {loadRelease,waitForRuntime,runtimeServices} from '../runtime/lifecycle.mjs';
import {prepareContext} from '../runtime/context.mjs';
import {getRuntimeStatus} from '../runtime/status.mjs';
import {coldBackup,backupVolumeNames} from '../runtime/acceptance-backup.mjs';
import {docker,ownedDocker,inspectResources,assertOwnership} from '../runtime/docker.mjs';
import {validateCloneOwnership} from '../contracts/evidence.mjs';
import {ContractClient} from '../contracts/scenario.mjs';
import {executeNotificationSql} from '../notifications/scenario.mjs';
import {fixtureRequest} from '../notifications/fixture.mjs';
import {loadNotificationSecrets} from '../notifications/runtime-secrets.mjs';
import {until,pause} from '../runtime/acceptance-scenario.mjs';
const sha=b=>createHash('sha256').update(b).digest('hex');
export function assertOldWorkerFrozenOff(snapshot){assert.equal(snapshot?.policy?.externalEnabled,false);assert(Array.isArray(snapshot.policy.requestedChannels)&&snapshot.policy.requestedChannels.length===0);assert.equal(snapshot.emailProfileId,null);assert.equal(snapshot.webhookProfileId,null);return true;}
export function assertRecoveredNotificationWire(deliveries,smtp,webhook){const email=deliveries.find(row=>row.channel==='Email'),hook=deliveries.find(row=>row.channel==='Webhook');assert(email?.status==='Accepted'&&hook?.status==='Accepted');assert(smtp.some(row=>row.deliveryId===email.id&&row.accepted===true&&row.tls===true&&row.authenticated===true&&row.replyCode===250&&row.recipientCount===1&&row.ccCount===0&&row.bccCount===0));assert(webhook.some(row=>row.deliveryId===hook.id&&row.remoteAccepted===true&&row.signatureValid===true&&row.tls===true&&row.replyCode>=200&&row.replyCode<300));return true;}
const apps=['control-plane','worker','console','gateway-a','gateway-b'];
const protectedTables={business:['organizations','projects','environments','apis','api_versions','api_groups','api_routes','route_methods','upstream_clusters','policies','route_policy_bindings','applications','application_api_permissions','release_records','release_items','approval_flows','approval_steps','approval_tasks'],accounts:['users','user_roles','user_project_scopes','roles','role_permissions','application_credentials'],attachments:['api_version_contract_sources','api_schemas','api_parameters'],notificationDefinitions:['system_settings','alert_rules','notification_channel_profiles','notification_channel_states'],notificationHistory:['notification_deliveries','notification_delivery_attempts'],sso:['sso_providers','user_external_identities']};
async function dpHashes(state,release){
 const volume=state.projectName+'_dp-keys';assertOwnership(state,JSON.parse((await docker(['volume','inspect',volume])).stdout));
 const result=await docker(['run','--rm','--user','10001:10001','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges:true','--label','com.docker.compose.project='+state.projectName,'--label','com.webapi.runtime.owner='+state.ownerId,'--mount','type=volume,src='+volume+',dst=/keys,readonly',release.dependencyImages.sdk,'find','/keys','-type','f','-exec','sha256sum','{}','+']);
 const files=Object.fromEntries(result.stdout.trim().split('\n').map(line=>{const match=/^([a-f0-9]{64})\s+\/keys\/(.+)$/.exec(line);assert(match);return[match[2],match[1]];}).sort(([a],[b])=>a.localeCompare(b)));assert(Object.keys(files).length);return files;
}
// A stopped process cannot reclaim its own expired leases. Fence unfinished
// attempts transactionally, retain OutcomeUnknown and require explicit budgeted
// retry after maintenance. Never send, create a new attempt or extend a deadline.
export async function drainStoppedNotificationAttempts(ctx,sql,{inspect=inspectResources}={}){
 const before=await inspect(ctx.state);assertOwnership(ctx.state,before);
 const workers=resources=>resources.filter(r=>r.Kind==='container'&&r.Config?.Labels?.['com.docker.compose.service']==='worker');
 assert(workers(before).length>0,'Worker inventory is required for maintenance');
 await ctx.compose('stop','worker');
 const stopped=await inspect(ctx.state);assertOwnership(ctx.state,stopped);
 assert(workers(stopped).length===workers(before).length&&workers(stopped).every(r=>r.State?.Running===false),'Every owned Worker must be stopped before maintenance');
 const rows=JSON.parse(await sql("SELECT COALESCE(json_agg(json_build_object('id',id,'attemptCount',attempt_count,'maxAttempts',max_attempts,'expiresAt',expires_at,'leaseToken',lease_token) ORDER BY id),'[]'::json)::text FROM notification_deliveries WHERE status='Sending';"));
 const query=await fs.readFile(new URL('./drain.sql',import.meta.url),'utf8');
 await sql("BEGIN; SET LOCAL webapi.notification_workers_stopped='confirmed';\n"+query+"\nCOMMIT;");
 assert.equal(await sql("SELECT count(*) FROM notification_deliveries WHERE status='Sending';"),'0','Maintenance must leave no Sending rows');
 for(const row of rows){assert(validGuid(row.id)&&Number.isInteger(row.attemptCount)&&Number.isSafeInteger(row.leaseToken));const current=JSON.parse(await sql("SELECT json_build_object('status',d.status,'reason',d.reason,'attemptCount',d.attempt_count,'maxAttempts',d.max_attempts,'expiresAt',d.expires_at,'leaseToken',d.lease_token,'outcome',a.outcome,'code',a.code,'completed',a.completed_at IS NOT NULL)::text FROM notification_deliveries d JOIN notification_delivery_attempts a ON a.delivery_id=d.id AND a.attempt_no=d.attempt_count WHERE d.id='"+row.id+"';"));assert.equal(current.status,'Failed');assert.equal(current.reason,'MaintenanceInterrupted');assert.equal(current.outcome,'OutcomeUnknown');assert.equal(current.code,'MaintenanceInterrupted');assert.equal(current.completed,true);assert.equal(current.attemptCount,row.attemptCount);assert.equal(current.maxAttempts,row.maxAttempts);assert.equal(current.expiresAt,row.expiresAt);assert(current.leaseToken>row.leaseToken);}
 return{workersStopped:true,incompleteAttempts:rows.length,unknownAttempts:rows.length,automaticRetryFrozen:true,originalBudgetRetained:true,noNewAttempts:true};
}
// Actual clone only: never restores a database and never modifies the installed
// release/runtime manifests. Image switches are transient Compose overrides.
export async function runDataPreservingRecovery({candidate,bridge,cloneDirectory,fixtureDirectory}){
 const state=await loadState(cloneDirectory),release=await loadRelease(path.join(cloneDirectory,'release.json'));
 validateCloneOwnership(state,await inspectResources(state),cloneDirectory);
 assert(Object.values(state.ports).every(port=>port>1024&&![4192,4193,4196,4197].includes(port)));
 assert.equal(release.sourceRevision,candidate.sourceRevision);assert.equal(release.imageId,candidate.imageId);
 const initialApps=(await inspectResources(state)).filter(r=>r.Kind==='container'&&[...apps,'notification-fixture'].includes(r.Config?.Labels?.['com.docker.compose.service']));
 assert(initialApps.length>=6&&initialApps.every(r=>r.State.Running&&r.Image===release.imageId),'Candidate image inventory must be running and exact');
 const candidateImage=JSON.parse((await docker(['image','inspect',release.imageId])).stdout)[0];assert.equal(candidateImage.Config.Labels['org.opencontainers.image.revision'],release.sourceRevision);
 const expected={candidateRevision:release.sourceRevision,candidateImageId:release.imageId,baselineRevision:bridge.baselineRevision,bridgeRevision:bridge.bridgeRevision,bridgeImageId:bridge.imageId,patchSha256:bridge.patchSha256,lockFiles:bridge.lockFiles};
 assert(validateBridgeReceipt(bridge,expected).passed);assert.equal(bridge.baselineRevision,'c1c271555e3a125d22184697c17320c94a4db07c');
 assert.equal(path.resolve(fixtureDirectory),path.join(path.resolve(cloneDirectory),'notification-approval-recovery'));
 await fs.mkdir(fixtureDirectory,{mode:0o700});
 const ctx=await prepareContext(cloneDirectory,state,release);assert(ctx.notification?.fixtureEnabled);
 const pg=(await inspectResources(state)).find(r=>r.Kind==='container'&&r.Config?.Labels?.['com.docker.compose.service']==='postgres');assert(pg);
 const sql=text=>executeNotificationSql(state,pg,text);
 const client=new ContractClient('http://127.0.0.1:'+state.ports.console);
 await client.login(state.bootstrapUsername,(await fs.readFile(path.join(cloneDirectory,'secrets/bootstrap-password'),'utf8')).trimEnd());
 for(const [file,hash]of Object.entries(release.staticFiles)){const response=await fetch(client.base+'/'+file.replace('console/dist/',''));assert(response.ok);assert.equal(sha(Buffer.from(await response.arrayBuffer())),hash,'Candidate static SHA');}
 const note=name=>console.log(JSON.stringify({step:name,projectName:state.projectName}));
 const overlay=path.join(fixtureDirectory,'transient-switch.json');
 const switchTo=async(imageId,mode,{worker=false}={})=>{
  const image=JSON.parse((await docker(['image','inspect',imageId])).stdout)[0];assert.equal(image.Id,imageId);
  const services=Object.fromEntries(apps.map(name=>[name,{image:imageId,...(name==='control-plane'?{environment:{CompatibilityNotifications__Mode:mode}}:{})}]));
  await writePrivate(overlay,{services});
  await ownedDocker(state,['compose','-p',state.projectName,...ctx.files,'-f',overlay,'up','-d','--no-deps',...apps.filter(name=>worker||name!=='worker')],{env:ctx.env});
  await waitForRuntime(ctx);
  const resources=await inspectResources(state);assertOwnership(state,resources);
  for(const name of apps.filter(name=>worker||name!=='worker')){const app=resources.find(r=>r.Kind==='container'&&r.Config?.Labels?.['com.docker.compose.service']===name);assert(app?.State.Running&&app.Image===imageId,name+' image switch failed');}
  return {imageId,revision:image.Config.Labels['org.opencontainers.image.revision']};
 };
 const rows=async table=>JSON.parse(await sql('SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),\'[]\'::jsonb) FROM "'+table+'" t;'));
 const snapshot=async before=>{
  const details={},digests={};for(const[group,tables]of Object.entries(protectedTables)){const result={};for(const table of tables){const current=(await rows(table)).map(row=>JSON.stringify(row)).sort();if(before){const preserved=before[group][table];assert(preserved.every(row=>current.includes(row)),table+' lost or changed protected rows');result[table]=preserved;}else result[table]=current;}details[group]=result;digests[group]=sha(JSON.stringify(result));}
  const keys=await dpHashes(state,release);digests.dpKeys=sha(JSON.stringify(keys));return {details,digests,dpKeys:keys};
 };
 const reject=async(route,options)=>{await assert.rejects(()=>client.request(route,options),error=>error.status===423&&error.code==='notification_maintenance_mode');return 423;};
 const tag=randomUUID().slice(0,8),events={};let pumpActive=false,pump,pumpError,success=false;
 try{
  // A real signed receiver accepts the body but delays its response. Stop the
  // executor during that physical attempt; a zero-Sending switch is insufficient.
  await fixtureRequest(cloneDirectory,state,'/owner/mode',{method:'POST',body:{smtp:'Accept',webhook:'Delay',retryAfter:'1'}});
  const savedNotification=await client.request('/settings/system/notification');
  const interrupted=await client.request('/settings/system/notification/tests',{method:'POST',headers:{'If-Match':'"'+savedNotification.revision+'"'},body:{channel:'Webhook'}});
  assert.equal(interrupted.maxAttempts,1);assert.equal(interrupted.status,'Queued');events.interruptedTestId=interrupted.id;
  await until(()=>client.request('/settings/system/notification/tests/'+interrupted.id),row=>row.status==='Sending',30);
  const interruptedWire=await until(()=>fixtureRequest(cloneDirectory,state,'/owner/observations'),value=>value.protocol.webhook.some(row=>row.deliveryId===interrupted.id&&row.remoteAccepted&&row.signatureValid&&row.tls),10);
  note('candidate-drain');const initialDrain=await drainStoppedNotificationAttempts(ctx,sql);assert(initialDrain.incompleteAttempts>0,'Actual in-flight attempt must be recovered');

  const environment=await client.request('/environments/'+state.binding.environmentId),project=await client.request('/projects/'+environment.projectId);
  const markerProject=await client.request('/organizations/'+project.organizationId+'/projects',{method:'POST',body:{code:'D1_'+tag.toUpperCase(),name:'升级后新业务标记 '+tag}});
  const password=randomBytes(24).toString('base64');const account=await client.request('/users',{method:'POST',body:{username:'recovery-'+tag,displayName:'恢复后保留账号',password}});
  await writePrivate(path.join(fixtureDirectory,'marker-account.json'),{id:account.id,password});
  const cluster=await client.request('/environments/'+environment.id+'/clusters',{method:'POST',body:{name:'恢复附件 '+tag,loadBalancingPolicy:'RoundRobin',healthCheckEnabled:false}});
  const source='openapi: 3.1.0\ninfo: {title: Recovery '+tag+', version: 1.0.0}\npaths:\n  /recovery-'+tag+':\n    get:\n      operationId: recoveryRead\n      responses:\n        "200":\n          description: OK\n          content:\n            application/json:\n              schema: {$ref: "schema.yaml#/components/schemas/Recovery"}\n';
  const attachment='components:\n  schemas:\n    Recovery:\n      type: object\n      properties:\n        marker: {type: string, const: "'+tag+'"}\n';
  const preview=await client.request('/openapi/import-previews',{method:'POST',body:{projectId:project.id,environmentId:environment.id,clusterId:cluster.id,sourceText:source,format:'yaml',files:[{name:'schema.yaml',content:attachment,format:'yaml'}]}});
  const imported=await client.request('/openapi/import-previews/'+preview.previewId+'/commit',{method:'POST',body:{expectedBundleHash:preview.bundleHash,targets:[{operationId:'recoveryRead',newApiCode:'D1_'+tag.toUpperCase(),newApiName:'恢复附件标记',version:'1.0.0'}]}});
  const definition={organizationId:project.organizationId,projectId:project.id,environmentId:environment.id,name:'旧 Worker 默认关闭 '+tag,metric:'request_rps',expression:'request_rps > 0',severity:'Warning',enabled:true,forSeconds:0,targetType:'Environment',targetId:null,windowSeconds:60,notification:{inConsole:true,requestedChannels:['Email','Webhook'],externalEnabled:true,emailRecipients:['recipient@example.test'],notifyRecovery:true,retryPolicy:{maxAttempts:3,baseDelaySeconds:1,maxDelaySeconds:900,expiresAfterMinutes:5}}};
  let rule=await client.request('/observability/alert-rules',{method:'POST',body:{...definition,enabled:false}});events.ruleId=rule.id;
  const marker={projectId:markerProject.id,accountId:account.id,versionId:imported.operations[0].versionId,ruleId:rule.id,attachmentKind:'actual imported supporting YAML stored in contract sources'};
  await writePrivate(path.join(fixtureDirectory,'markers.json'),marker);
  // All upgraded marker rows, attachment bundle, definitions and existing histories
  // are inside the cold backup; every internal manifest file is reread below.
  const backupDirectory=path.join(fixtureDirectory,'cold-backup');await coldBackup(ctx,backupDirectory);const backupDrain=await drainStoppedNotificationAttempts(ctx,sql);
  rule=await client.request('/observability/alert-rules/'+rule.id+'/enable',{method:'POST',headers:{'If-Match':'"'+rule.revision+'"'}});
  assert.equal(await sql("SELECT count(*) FROM alert_events WHERE rule_id='"+rule.id+"';"),'0','Old-worker proof requires a fresh occurrence');
  const manifestBytes=await fs.readFile(path.join(backupDirectory,'backup-manifest.json')),manifest=JSON.parse(manifestBytes);assert.equal(manifest.sourceRevision,release.sourceRevision);
  for(const[file,hash]of Object.entries(manifest.files)){assert(!path.isAbsolute(file)&&!file.split('/').includes('..'));assert.equal(sha(await fs.readFile(path.join(backupDirectory,file))),hash,'Cold archive '+file);}
  const before=await snapshot();await writePrivate(path.join(fixtureDirectory,'protected-before-private.json'),before);
  const secretBefore=await loadNotificationSecrets(path.join(cloneDirectory,'notification-secrets'),state.ownerId,state.projectName);
  const ssoConfigBefore=sha(await fs.readFile(path.join(cloneDirectory,'sso-deployment.json')));
  note('actual-old-image-rejects-nine');
  const oldImage='sha256:bc07e5333e135ff4672fc2934e5e8076e619744185728174d205e3f8231b8b1b';
  const oldIdentity=await switchTo(oldImage,'Disabled');assert.equal(oldIdentity.revision,bridge.baselineRevision);
  await assert.rejects(()=>client.request('/settings/system/notification'),error=>error.status===503&&error.code==='stored_settings_invalid');
  note('bridge-readonly');const bridgeIdentity=await switchTo(bridge.imageId,'ReadOnly');assert.equal(bridgeIdentity.revision,bridge.bridgeRevision);
  for(const group of ['security','release','gateway','audit','notification'])await client.request('/settings/system/'+group);
  const checks={settingsGroups:5};
  checks.notificationSave=await reject('/settings/system/notification',{method:'PUT',headers:{'If-Match':'"1"'},body:{values:{},confirmationToken:'unused'}});
  checks.ruleCreate=await reject('/observability/alert-rules',{method:'POST',body:{...definition,name:'Blocked'}});
  checks.ruleUpdate=await reject('/observability/alert-rules/'+rule.id,{method:'PUT',headers:{'If-Match':'"'+rule.revision+'"'},body:definition});
  for(const operation of ['enable','disable'])checks[operation==='enable'?'ruleEnable':'ruleDisable']=await reject('/observability/alert-rules/'+rule.id+'/'+operation,{method:'POST',headers:{'If-Match':'"'+rule.revision+'"'}});
  checks.notificationTest=await reject('/settings/system/notification/tests',{method:'POST',body:{channel:'Email',email:'recipient@example.test'}});
  checks.notificationRetry=await reject('/notification-deliveries/'+randomUUID()+'/retry',{method:'POST',headers:{'If-Match':'"1"'}});
  const bridgeProject=await client.request('/organizations/'+project.organizationId+'/projects',{method:'POST',body:{code:'BRIDGE_'+tag.toUpperCase(),name:'桥接仍可新增业务 '+tag}});checks.otherBusinessWrite=!!bridgeProject.id;
  // The existing authenticated cookie is used across both ControlPlane images;
  // no relogin or re-created DP key is allowed to conceal decryption failure.
  await client.request('/auth/me');
  const credential=(await fs.readFile(path.join(cloneDirectory,'demo/consumer-api-key'),'utf8')).trimEnd();
  await switchTo(bridge.imageId,'ReadOnly',{worker:true});pumpActive=true;
  pump=(async()=>{while(pumpActive){try{for(const port of [state.ports.gatewayA,state.ports.gatewayB]){const response=await fetch('http://127.0.0.1:'+port+'/orders',{headers:{'X-API-Key':credential},signal:AbortSignal.timeout(5000)});await response.arrayBuffer();assert.equal(response.status,200);}}catch(error){pumpError=error;break;}await pause(200);}})();
  events.oldWorkerEventId=await until(async()=>await sql("SELECT id::text FROM alert_events WHERE rule_id='"+rule.id+"' ORDER BY started_at DESC LIMIT 1;"),id=>/^[a-f0-9-]{36}$/.test(id),180);
  assert(!pumpError,'Gateway pump failed');pumpActive=false;await pump;await ctx.compose('stop','worker');
  const frozen=JSON.parse(await sql("SELECT frozen_notification::text FROM alert_events WHERE id='"+events.oldWorkerEventId+"';"));checks.oldWorkerFrozenOff=assertOldWorkerFrozenOff(frozen);
  assert.equal(await sql("SELECT count(*) FROM notification_deliveries WHERE event_id='"+events.oldWorkerEventId+"';"),'0');
  await writePrivate(path.join(fixtureDirectory,'bridge-observation.json'),{actual:true,...bridgeIdentity,checks,events,frozenNotification:frozen,gatewayStatus:200,cookieSurvived:true});
  note('candidate-restored');await switchTo(release.imageId,'Disabled');
  for(const [file,hash]of Object.entries(release.staticFiles)){const response=await fetch(client.base+'/'+file.replace('console/dist/',''));assert(response.ok);assert.equal(sha(Buffer.from(await response.arrayBuffer())),hash,'Restored static SHA');}
  const after=await snapshot(before.details);assert.deepEqual(after.digests,before.digests);await writePrivate(path.join(fixtureDirectory,'protected-after-private.json'),after);
  assert.equal(sha(await fs.readFile(path.join(cloneDirectory,'sso-deployment.json'))),ssoConfigBefore);
  const secretAfter=await loadNotificationSecrets(path.join(cloneDirectory,'notification-secrets'),state.ownerId,state.projectName);assert.deepEqual(secretAfter.files,secretBefore.files);
  await client.request('/auth/me');const accountClient=new ContractClient(client.base);await accountClient.login('recovery-'+tag,password);
  // Exercise the shipped test API after recovery. This needs no fixture seed
  // or broadened business Scope; both tasks use the original saved settings.
  await fixtureRequest(cloneDirectory,state,'/owner/mode',{method:'POST',body:{smtp:'Accept',webhook:'Accept',retryAfter:'1'}});
  const notificationSettings=await client.request('/settings/system/notification'),testIds=[];
  for(const channel of ['Email','Webhook']){const queued=await client.request('/settings/system/notification/tests',{method:'POST',headers:{'If-Match':'"'+notificationSettings.revision+'"'},body:{channel,...(channel==='Email'?{email:'recipient@example.test'}:{})}});assert.equal(queued.status,'Queued');testIds.push(queued.id);}
  await ctx.compose('up','-d','worker');
  const accepted=await until(()=>Promise.all(testIds.map(id=>client.request('/settings/system/notification/tests/'+id))),rows=>rows.every(row=>row.status==='Accepted'),60);
  const deliveries={items:accepted};
  const fixture=await fixtureRequest(cloneDirectory,state,'/owner/observations');assert(fixture.binaryVerified&&fixture.sourceRevision===release.sourceRevision);
  const smtp=fixture.protocol.smtp.filter(row=>deliveries.items.some(d=>d.id===row.deliveryId)),webhook=fixture.protocol.webhook.filter(row=>deliveries.items.some(d=>d.id===row.deliveryId));
  assertRecoveredNotificationWire(deliveries.items,smtp,webhook);
  assert.equal(await sql("SELECT count(*) FROM notification_deliveries WHERE event_id='"+events.oldWorkerEventId+"';"),'0','No retrospective task for old-worker event');
  const interruptedAfter=await client.request('/settings/system/notification/tests/'+interrupted.id);
  assert.equal(interruptedAfter.status,'Failed');assert.equal(interruptedAfter.reason,'MaintenanceInterrupted');assert.equal(interruptedAfter.attemptCount,1);assert.equal(interruptedAfter.maxAttempts,1);assert.equal(interruptedAfter.expiresAt,interrupted.expiresAt);assert.equal(interruptedAfter.canRetry,false);
  const interruptedAttempts=await client.request('/notification-deliveries/'+interrupted.id+'/attempts');assert.equal(interruptedAttempts.items.length,1);assert.equal(interruptedAttempts.items[0].outcome,'OutcomeUnknown');assert.equal(interruptedAttempts.items[0].code,'MaintenanceInterrupted');
  assert.equal(fixture.protocol.webhook.filter(row=>row.deliveryId===interrupted.id).length,1,'No hidden resend of interrupted test');

  const status=await until(()=>getRuntimeStatus(state,{directory:cloneDirectory}),value=>value.phase==='Ready'&&value.notifications.fixture==='Available',60);
  const receipt={schemaVersion:1,actual:true,sourceRevision:release.sourceRevision,imageId:release.imageId,clone:{ownerVerified:true,ownerId:state.ownerId,projectName:state.projectName,consolePort:state.ports.console},bridge,phases:[{revision:release.sourceRevision,imageId:release.imageId},{revision:bridge.bridgeRevision,imageId:bridge.imageId,mode:'ReadOnly'},{revision:release.sourceRevision,imageId:release.imageId}],drained:true,maintenanceDrain:{initial:initialDrain,afterBackup:backupDrain},maintenanceInterrupted:{id:interrupted.id,remoteAccepted:true,localOutcome:"OutcomeUnknown",attemptCount:1,maxAttempts:1,expiryPreserved:true,noHiddenResend:true,canRetry:false},sendingBeforeSwitch:0,sourceDatabaseRestored:false,oldImageRejectsNine:true,protectedBefore:before.digests,protectedAfter:after.digests,protectedRowCounts:Object.fromEntries(Object.entries(before.details).map(([group,tables])=>[group,Object.fromEntries(Object.entries(tables).map(([name,values])=>[name,values.length]))])),backup:{actual:true,manifestVerified:true,manifestFilesVerified:Object.keys(manifest.files).length,manifestSha256:sha(manifestBytes),volumes:backupVolumeNames(ctx)},bridgeChecks:checks,candidateRestored:{ready:status.phase==='Ready',dpRetained:true,ssoRetained:true,noRetrospectiveSends:true,smtpAccepted:true,webhookAccepted:true},markers:marker,events,fresh:{kind:'Test',createdVia:'shipped notification API',deliveries:deliveries.items.map(d=>({id:d.id,channel:d.channel,status:d.status,attemptCount:d.attemptCount})),smtp,webhook},ssoEvidence:{storedProviderAndBindingRowsUnchanged:true,deploymentConfigUnchanged:true,persistentDpKeysUnchanged:true,sessionCookieSurvived:true,newLocalAccountLoginPassed:true,liveOidcLoginPerformed:false},temporarySwitchOnly:true,original4192Upgraded:false};
  const gate=validateDataPreservingRecovery(receipt,expected);assert(gate.passed,gate.errors.join('; '));await writePrivate(path.join(fixtureDirectory,'recovery-receipt.json'),receipt);success=true;note('actual-recovery-gate-passed');return receipt;
 }finally{
  pumpActive=false;if(pump)await pump;
  // Even failed proofs leave the owned clone on its original candidate, never on
  // the old or maintenance image. No original installation is touched here.
  await withRuntimeLock(cloneDirectory,async()=>{await switchTo(release.imageId,'Disabled',{worker:true});await ctx.compose('up','-d',...runtimeServices(state,ctx.notification));await waitForRuntime(ctx);});
  await writePrivate(path.join(fixtureDirectory,'finally.json'),{candidateRestored:true,proofComplete:success,sourceRevision:release.sourceRevision,imageId:release.imageId,checkedAt:new Date().toISOString()});
 }
}
export function validateDataPreservingRecovery(receipt,expected){try{assert(receipt?.schemaVersion===1&&receipt.actual===true);assert(receipt.sourceRevision===expected.candidateRevision&&receipt.imageId===expected.candidateImageId);const bridge=validateBridgeReceipt(receipt.bridge,expected);assert(bridge.passed,bridge.errors.join('; '));const clone=receipt.clone;assert(clone?.ownerVerified===true&&/^webapi-enterprise-local-test-[a-f0-9-]{36}$/.test(clone.projectName)&&validGuid(clone.ownerId)&&validGuid(clone.projectName.replace('webapi-enterprise-local-test-','')));assert(Number.isInteger(clone.consolePort)&&clone.consolePort>1024&&clone.consolePort<=65535&&![4192,4193,4196,4197].includes(clone.consolePort));assert.deepEqual(receipt.phases,[{revision:expected.candidateRevision,imageId:expected.candidateImageId},{revision:expected.bridgeRevision,imageId:expected.bridgeImageId,mode:'ReadOnly'},{revision:expected.candidateRevision,imageId:expected.candidateImageId}]);assert(receipt.drained===true&&receipt.sendingBeforeSwitch===0&&receipt.sourceDatabaseRestored===false&&receipt.oldImageRejectsNine===true);for(const phase of ['initial','afterBackup']){const drain=receipt.maintenanceDrain?.[phase];assert(drain?.workersStopped===true&&Number.isSafeInteger(drain.incompleteAttempts)&&drain.incompleteAttempts>=0&&drain.unknownAttempts===drain.incompleteAttempts&&drain.automaticRetryFrozen===true&&drain.originalBudgetRetained===true&&drain.noNewAttempts===true,'Stopped-worker maintenance evidence is required');}assert(receipt.maintenanceDrain.initial.incompleteAttempts>0,'Actual interrupted send is required, an idle-only recovery is insufficient');for(const key of ['business','accounts','attachments','notificationDefinitions','notificationHistory','dpKeys','sso'])assert(/^[a-f0-9]{64}$/.test(receipt.protectedBefore?.[key]??'')&&receipt.protectedBefore[key]===receipt.protectedAfter?.[key],key+' must survive recovery');const backup=receipt.backup;assert(backup?.actual===true&&backup.manifestVerified===true&&Number.isInteger(backup.manifestFilesVerified)&&backup.manifestFilesVerified>0);assert(new Set(backup.volumes).size===backup.volumes.length);for(const name of ['pg','lkg-a','lkg-b','dp-keys','prometheus-data','loki-data','tempo-data','secrets-cache','notification-secrets','notification-fixture-secrets','notification-fixture-data'])assert(backup.volumes?.includes(name));const checks=receipt.bridgeChecks;assert(checks?.settingsGroups===5);for(const key of ['notificationSave','ruleCreate','ruleUpdate','ruleEnable','ruleDisable','notificationTest','notificationRetry'])assert(checks[key]===423,key+' must be maintenance locked');assert(checks.otherBusinessWrite===true&&checks.oldWorkerFrozenOff===true);const restored=receipt.candidateRestored;assert(restored?.ready===true&&restored.dpRetained===true&&restored.ssoRetained===true&&restored.noRetrospectiveSends===true&&restored.smtpAccepted===true&&restored.webhookAccepted===true);return{passed:true,errors:[]};}catch(error){return{passed:false,errors:[error instanceof Error?error.message:'Invalid recovery receipt']};}}
