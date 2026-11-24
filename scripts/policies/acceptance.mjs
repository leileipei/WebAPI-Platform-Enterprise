import fs from 'node:fs/promises';import path from 'node:path';import {randomUUID,randomBytes} from 'node:crypto';import {execFile} from 'node:child_process';import {promisify} from 'node:util';import assert from 'node:assert/strict';import {buildPolicySource,verifyPolicySource} from './build-source.mjs';import {runTrafficPolicyScenario} from './scenario.mjs';import {assertNoSecrets} from './evidence.mjs';
import {cleanupPolicyRun, policyComposeArguments, secretNames} from './cleanup.mjs';
import {changePolicyReview, claimPolicyReview} from './review-pointer.mjs';
import {withAcceptanceSignals} from '../acceptance-credentials.mjs';

await withAcceptanceSignals(runAcceptance);

async function runAcceptance(signal) {
const exec=promisify(execFile),root=path.resolve(import.meta.dirname,'../..'),mode=process.argv[2]||'--e2e',owner=randomUUID(),project='webapi-enterprise-e2e-policies-'+owner,directory=path.join(root,'.runtime',project),publicDirectory=path.join(root,'docs/evidence/policies');
await fs.mkdir(path.dirname(directory), {recursive: true, mode: 0o700});
await fs.mkdir(directory, {mode: 0o700});
await fs.mkdir(publicDirectory,{recursive:true});
const env={...process.env,WEBAPI_E2E_PROJECT:project,WEBAPI_E2E_DIRECTORY:directory,WEBAPI_E2E_RELATIVE_DIR:'.runtime/'+project,WEBAPI_ENVIRONMENT_ID:randomUUID(),WEBAPI_OBS_SCENARIO:'true'};const log=path.join(directory,'operations.log');
async function command(binary, args, opts = {}) {
 const options = {cwd: root, env, signal, maxBuffer: 32 * 1024 * 1024, ...opts};
 try {
   const run = await exec(binary, args, options);
   if (run.stdout || run.stderr) await fs.appendFile(log, run.stdout + run.stderr);
   return run.stdout.trim();
 } catch (error) {
   await fs.appendFile(log, (error.stdout || '') + (error.stderr || ''));
   options.signal?.throwIfAborted();
   throw Error(`${binary} ${args.slice(0, 3).join(' ')} failed (see private operations.log)`);
 }
}
let args, source, image, context, result, cleanupReceipt, claimedReview;
const pointer = path.join(root, '.runtime/policy-review.json');
const reviewMode = mode === '--browser' || mode === '--review';
const cleanup = () => cleanupPolicyRun(directory, () => ({project, owner, args, docker: argv => command('docker', argv, {signal: undefined})}));
try{
 if (reviewMode) {
   const active = await fs.lstat(pointer).catch(error => { if (error.code !== 'ENOENT') throw error; });
   if (active) throw Error('An independent policy review is already active');
   await fs.writeFile(path.join(directory, 'owner.json'), JSON.stringify({project, owner}), {mode: 0o600, flag: 'wx'});
 }
 for (const name of secretNames) {
   signal.throwIfAborted();
   const encoding = ['ip-hmac', 'cursor-key', 'login-hmac'].includes(name) ? 'base64' : 'base64url';
   await fs.writeFile(path.join(directory, name), randomBytes(32).toString(encoding), {mode: 0o600, flag: 'wx'});
 }
 console.log('Freezing actual build source');source=await buildPolicySource({root,outputDirectory:path.join(directory,'build')});await fs.copyFile(source.manifestPath,path.join(publicDirectory,'source-manifest.json'));
 const publish=path.join(directory,'build/publish');await fs.mkdir(publish);const apps={'control-plane':'ControlPlane',worker:'Worker',gateway:'Gateway','console-host':'ConsoleHost',migrator:'Migrator','runtime-tool':'RuntimeTool','demo-backend':'TestBackend'},sdk=JSON.parse(await fs.readFile(path.join(root,'deploy/images.lock.json'))).sdk;
 console.log('Building frozen console and seven applications');await fs.symlink(path.join(root,'console/node_modules'),path.join(source.sourceDirectory,'console/node_modules'));await command(process.execPath,[path.join(root,'console/node_modules/typescript/bin/tsc'),'--noEmit'],{cwd:path.join(source.sourceDirectory,'console')});await command(process.execPath,[path.join(root,'console/node_modules/vite/bin/vite.js'),'build'],{cwd:path.join(source.sourceDirectory,'console')});await fs.cp(path.join(source.sourceDirectory,'console/dist'),path.join(publish,'console-dist'),{recursive:true});
 const script=['set -eu','export NUGET_PACKAGES=/build/.nuget','mkdir -p /build/.nuget; cp -a /nuget-seed/. /build/.nuget/'];for(const [target,name]of Object.entries(apps))script.push(`dotnet restore src/WebApi.${name}/WebApi.${name}.csproj --locked-mode`,`dotnet publish src/WebApi.${name}/WebApi.${name}.csproj -c Release --no-restore -o /build/publish/${target}`);
 await command('docker',['run','--rm','--workdir','/build/source','-v',path.join(directory,'build')+':/build','-v','webapi-enterprise-core-test_nuget-test:/nuget-seed:ro',sdk,'bash','-c',script.join('\n')]);await verifyPolicySource(source);
 const dockerfile=`ARG SDK_IMAGE\nFROM \${SDK_IMAGE}\nLABEL org.opencontainers.image.revision="${source.baseCommit}" com.webapi.policies.source-manifest="${source.sourceManifestHash}"\nWORKDIR /app\nCOPY --chown=10001:10001 publish/ /app/\nENV DOTNET_CLI_TELEMETRY_OPTOUT=1\nUSER 10001:10001\n`;await fs.writeFile(path.join(directory,'build/Dockerfile'),dockerfile);const tag='webapi-policies:'+source.sourceManifestHash.slice(0,20);console.log('Building source-manifest identified image');await command('docker',['build','--build-arg','SDK_IMAGE='+sdk,'-t',tag,path.join(directory,'build')]);const inspect=JSON.parse(await command('docker',['image','inspect',tag]))[0];assert.equal(inspect.Config.Labels['com.webapi.policies.source-manifest'],source.sourceManifestHash);image={imageTag:tag,imageDigest:inspect.Id,baseCommit:source.baseCommit,sourceManifestHash:source.sourceManifestHash};
 const overlay={volumes:{'webapi-enterprise-core-test_nuget-test':{external:true}},services:{},secrets:{password:{file:path.join(directory,'password')}}};for(const service of ['postgres','redis','collector','prometheus','loki','tempo','sdk','control-plane','worker','worker-b','gateway-a','gateway-b','backend-a','backend-b','console'])overlay.services[service]={labels:{'com.webapi.policies.owner':owner}};
 for(const name of ['pg','nuget','lkg-a','lkg-b','evidence','lpm','prometheus-data','loki-data','tempo-data'])overlay.volumes[name]={labels:{'com.webapi.policies.owner':owner}};
 for(const [service,target]of Object.entries({'control-plane':'control-plane',worker:'worker','worker-b':'worker','gateway-a':'gateway','gateway-b':'gateway','backend-a':'demo-backend','backend-b':'demo-backend',console:'console-host'})){Object.assign(overlay.services[service],{image:tag,working_dir:'/app',command:['dotnet',`/app/${target}/WebApi.${apps[target]}.dll`]});}
 overlay.services['control-plane'].environment={WEBAPI_BOOTSTRAP_USERNAME:'e2e-admin',WEBAPI_BOOTSTRAP_PASSWORD_FILE:'/run/secrets/password',Authentication__LoginProtection__HmacSecretFile:'/lpm/login-hmac'};overlay.services['control-plane'].secrets=['password','postgres-password','node-a','node-b'];overlay.services.console.environment={Console__DistDirectory:'/app/console-dist'};for(const n of ['gateway-a','gateway-b'])overlay.services[n].environment={TrafficPolicies__RedisConnection:'redis:6379,abortConnect=false,connectTimeout=1000,asyncTimeout=1000',TrafficPolicies__RedisKeyPrefix:'policies-'+owner};
 // Override bind mounts from old source runners; snapshots execute only the frozen image.
 const file=path.join(directory,'policy-overlay.yml');let yaml=JSON.stringify(overlay,null,2);await fs.writeFile(file,yaml);
 const volumeOverride=path.join(directory,'policy-volumes.yml');await fs.writeFile(volumeOverride,'services:\n'+Object.keys(overlay.services).filter(s=>['control-plane','worker','worker-b','backend-a','backend-b','console','gateway-a','gateway-b'].includes(s)).map(s=>`  ${s}:\n    volumes: !override ${s.startsWith('gateway')?`[lkg-${s.endsWith('a')?'a':'b'}:/lkg]`:(s==='control-plane'?`[evidence:/evidence,lpm:/lpm]`:'[]')}\n`).join('')+`  sdk:\n    working_dir: /workspace\n    volumes: !override ["${source.sourceDirectory}:/workspace", "${directory}:/policy-results", "webapi-enterprise-core-test_nuget-test:/root/.nuget/packages:ro"]\n`);
 args=policyComposeArguments(directory,project);const compose=(...a)=>command('docker',[...args,...a]);
 console.log('Starting independent database, Redis and three sources');await compose('up','-d','--wait','postgres','redis');await command('docker',['volume','create','--label','com.webapi.policies.owner='+owner,project+'_evidence']);await command('docker',['run','--rm','--user','0:0','-v',project+'_evidence:/evidence',sdk,'bash','-c','chown -R 10001:10001 /evidence && chmod 700 /evidence']);await command('docker',['volume','create','--label','com.webapi.policies.owner='+owner,project+'_lpm']);await command('docker',['run','--rm','--user','0:0','-v',project+'_lpm:/lpm','-v',path.join(directory,'login-hmac')+':/src/login-hmac:ro',sdk,'bash','-c','install -o 10001 -g 10001 -m 600 /src/login-hmac /lpm/login-hmac']);await compose('run','--rm','--user','10001:10001','--workdir','/app','--entrypoint','dotnet','-e','WEBAPI_BOOTSTRAP_USERNAME=e2e-admin','-e','WEBAPI_BOOTSTRAP_PASSWORD_FILE=/run/secrets/password','-e','WEBAPI_MIGRATION_EVIDENCE_DIR=/evidence','-e','WEBAPI_MIGRATION_EVIDENCE_RECORD_MAX_BYTES=8192','-e','WEBAPI_MIGRATION_EVIDENCE_QUOTA_BYTES=33554432','-e','WEBAPI_MIGRATION_EVIDENCE_MIN_FREE_BYTES=0','-e','WEBAPI_MIGRATION_TRANSACTION_SECONDS=900','-e','WEBAPI_MIGRATION_SETTINGS_MAX_BYTES=65536','-e','WEBAPI_MIGRATION_RULE_MAX_BYTES=16384','-e','WEBAPI_MIGRATION_RULE_MAX_COUNT=100000','-e','WEBAPI_MIGRATION_TOTAL_MAX_BYTES=67108864','-e','WEBAPI_MIGRATION_RECEIPT_MAX_ROWS=100000','control-plane','/app/migrator/WebApi.Migrator.dll','--bootstrap','--seed-catalog');await compose('up','-d','collector','prometheus','loki','tempo','control-plane');
 const endpoint=async(s,p=8080)=>'http://'+await compose('port',s,String(p));env.WEBAPI_E2E_CONTROL_PLANE_URL=await endpoint('control-plane');await command(process.execPath,['scripts/wait-http.mjs',env.WEBAPI_E2E_CONTROL_PLANE_URL+'/health/live']);await command(process.execPath,['scripts/prepare-e2e.mjs']);context=JSON.parse(await fs.readFile(path.join(directory,'context.json')));env.WEBAPI_ENVIRONMENT_ID=context.environmentId;await compose('up','-d','--force-recreate','control-plane');env.WEBAPI_E2E_CONTROL_PLANE_URL=await endpoint('control-plane');await compose('create','gateway-a','gateway-b');for(const name of ['lkg-a','lkg-b']){const volume=project+'_'+name,labels=JSON.parse(await command('docker',['volume','inspect','--format','{{json .Labels}}',volume]));assert.equal(labels['com.webapi.policies.owner'],owner);assert.equal(labels['com.docker.compose.project'],project);await command('docker',['run','--rm','--user','0:0','-v',volume+':/policy-lkg',sdk,'bash','-c','chown 10001:10001 /policy-lkg && chmod 700 /policy-lkg']);}await compose('up','-d','worker','worker-b','backend-a','backend-b','gateway-a','gateway-b','console');await command(process.execPath,['scripts/wait-http.mjs',env.WEBAPI_E2E_CONTROL_PLANE_URL+'/health/live']);await command(process.execPath,['scripts/wait-nodes.mjs']);
 const endpoints={control_plane:env.WEBAPI_E2E_CONTROL_PLANE_URL};for(const [s,p]of [['collector',4318],['prometheus',9090],['loki',3100],['tempo',3200],['console',8080],['gateway-a',8080],['gateway-b',8080],['backend-a',8080],['backend-b',8080]])endpoints[s.replaceAll('-','_')]=await endpoint(s,p);await fs.writeFile(path.join(directory,'review-context.json'),JSON.stringify({project,owner,directory,endpoints,context,image,args,environment:{WEBAPI_E2E_PROJECT:project,WEBAPI_E2E_DIRECTORY:directory,WEBAPI_E2E_RELATIVE_DIR:env.WEBAPI_E2E_RELATIVE_DIR,WEBAPI_ENVIRONMENT_ID:env.WEBAPI_ENVIRONMENT_ID}},null,2),{mode:0o600});
 console.log('Running real policy release and failure scenarios');result=await runTrafficPolicyScenario({root,directory,project,owner,endpoints,context,image,compose,signal});await fs.writeFile(path.join(directory,'review-context.json'),JSON.stringify({...JSON.parse(await fs.readFile(path.join(directory,'review-context.json'))),endpoints},null,2),{mode:0o600});await fs.writeFile(path.join(directory,'result.json'),JSON.stringify(result,null,2),{mode:0o600});await compose('run','--rm','-e','WEBAPI_POLICY_RESULT_DIRECTORY=/policy-results','sdk','dotnet','test','tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj','-c','Release','--filter','Scenario=Policies');result.complete=true;assertNoSecrets(result,await Promise.all([...secretNames,'credential'].map(n=>fs.readFile(path.join(directory,n),'utf8'))));
 if (reviewMode) {
   signal.throwIfAborted();
   result.review = {project, url: endpoints.console, privateContextFile: path.join(directory, 'review-context.json')};
   const record = {directory, project, url: endpoints.console};
   await claimPolicyReview(pointer, record);
   claimedReview = record;
 } else {
   cleanupReceipt = await cleanup();
   if (cleanupReceipt.failed) throw Error(cleanupReceipt.error);
   result.cleanup = cleanupReceipt;
 }
 await fs.writeFile(path.join(publicDirectory,'verification.json'),JSON.stringify(result,null,2)+'\n');
 signal.throwIfAborted();
 if (claimedReview) console.log('Independent review entry: ' + claimedReview.url);
 console.log('Policy scenario complete: '+result.checks.length+' verified checks');
} catch (error) {
 if (args && !cleanupReceipt) {
   for (const service of ['control-plane', 'worker', 'worker-b', 'gateway-a', 'gateway-b']) {
     await command('docker', [...args, 'logs', '--tail', '150', service], {signal: undefined}).catch(() => {});
   }
 }
 cleanupReceipt ??= await cleanup();
 if (claimedReview && !cleanupReceipt.failed) {
   try { await changePolicyReview(pointer, claimedReview); }
   catch { Object.assign(cleanupReceipt, {failed: true, error: 'Policy review pointer release failed'}); }
 }
 const failure = {complete: false, project, time: new Date().toISOString(), message: error.message, cleanup: cleanupReceipt};
 try {
   await fs.writeFile(path.join(publicDirectory, 'last-failure.json'), JSON.stringify(failure, null, 2));
   const historyFile = path.join(publicDirectory, 'failed-attempts.json');
   let history;
   try { history = JSON.parse(await fs.readFile(historyFile)); }
   catch (readError) { if (readError.code !== 'ENOENT') throw readError; history = []; }
   history.push(failure);
   await fs.writeFile(historyFile, JSON.stringify(history, null, 2));
 } catch {
   console.error('Policy failure evidence could not be recorded');
 }
 throw error;
}
}
