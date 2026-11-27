import fs from 'node:fs/promises';import path from 'node:path';import {randomUUID} from 'node:crypto';import assert from 'node:assert/strict';import {buildPolicySource,verifyPolicySource} from '../policies/build-source.mjs';import {runSettingsScenario} from './scenario.mjs';import {assertNoSecrets,createPrivateWorkspace,toolManifest,verifyToolManifest,secretNames} from './evidence.mjs';
import {writePrivate} from '../runtime/state.mjs';
import {execFileAndWait} from '../runtime/exec-file.mjs';
import {withAcceptanceSignals} from '../acceptance-credentials.mjs';
import {cleanupSettingsRun,settingsComposeArguments,publishSettingsPointer,retireSettingsContext,withSettingsRecords,writePublicJson} from './cleanup.mjs';
export async function runSettingsAcceptance({root,retainForReview=false,signal}){
 const publicDirectory=path.join(root,'docs/evidence/settings');
 await fs.mkdir(publicDirectory,{recursive:true});
 let workspace,project,owner,directory,env,source,image,context,result,receipt;
 let args=null,stage='workspace preparation',scenarioComplete=false;
 async function command(binary,argv,opts={}){
   const log=path.join(directory,'operations.log');
   const options={cwd:root,env,signal,maxBuffer:32*1024*1024,...opts};
   try{
     const response=await execFileAndWait(binary,argv,options);
     if(response.stdout||response.stderr)await fs.appendFile(log,response.stdout+response.stderr);
     return response.stdout.trim();
   }catch(error){
     options.signal?.throwIfAborted();
     await fs.appendFile(log,(error.stdout||'')+(error.stderr||''));
     const message=binary+' '+argv.slice(0,3).join(' ')+' failed (see private operations.log)';
     throw Object.assign(Error(message),{settingsPublicMessage:message});
   }
 }
 async function cleanup(){
   return cleanupSettingsRun(root,workspace,args,argv=>command('docker',argv,{signal:undefined}));
 }
 try{
   workspace=await createPrivateWorkspace(root);
   ({project,owner,directory}=workspace);
   env={...process.env,WEBAPI_E2E_PROJECT:project,WEBAPI_E2E_DIRECTORY:directory,WEBAPI_E2E_RELATIVE_DIR:'.runtime/'+project,WEBAPI_ENVIRONMENT_ID:randomUUID(),WEBAPI_OBS_SCENARIO:'true'};
   stage='source and tool verification';
   const tools=await toolManifest(root);
   await writePublicJson(path.join(publicDirectory,'tool-manifest.json'),tools);
 console.log('Freezing actual build source');source=await buildPolicySource({root,outputDirectory:path.join(directory,'build')});await fs.copyFile(source.manifestPath,path.join(publicDirectory,'source-manifest.json'));
 const publish=path.join(directory,'build/publish');await fs.mkdir(publish);const apps={'control-plane':'ControlPlane',worker:'Worker',gateway:'Gateway','console-host':'ConsoleHost',migrator:'Migrator','runtime-tool':'RuntimeTool','demo-backend':'TestBackend'},sdk=JSON.parse(await fs.readFile(path.join(root,'deploy/images.lock.json'))).sdk;
 console.log('Building frozen console and seven applications');await fs.symlink(path.join(root,'console/node_modules'),path.join(source.sourceDirectory,'console/node_modules'));await command(process.execPath,[path.join(root,'console/node_modules/typescript/bin/tsc'),'--noEmit'],{cwd:path.join(source.sourceDirectory,'console')});await command(process.execPath,[path.join(root,'console/node_modules/vite/bin/vite.js'),'build'],{cwd:path.join(source.sourceDirectory,'console')});await fs.cp(path.join(source.sourceDirectory,'console/dist'),path.join(publish,'console-dist'),{recursive:true});
 const script=['set -eu','export NUGET_PACKAGES=/build/.nuget','mkdir -p /build/.nuget; cp -a /nuget-seed/. /build/.nuget/'];for(const [target,name]of Object.entries(apps))script.push(`dotnet restore src/WebApi.${name}/WebApi.${name}.csproj --locked-mode`,`dotnet publish src/WebApi.${name}/WebApi.${name}.csproj -c Release --no-restore -o /build/publish/${target}`);
 await command('docker',['run','--rm','--workdir','/build/source','-v',path.join(directory,'build')+':/build','-v','webapi-enterprise-core-test_nuget-test:/nuget-seed:ro',sdk,'bash','-c',script.join('\n')]);await verifyPolicySource(source);
 const dockerfile=`ARG SDK_IMAGE\nFROM \${SDK_IMAGE}\nLABEL org.opencontainers.image.revision="${source.baseCommit}" com.webapi.settings.source-manifest="${source.sourceManifestHash}"\nWORKDIR /app\nCOPY --chown=10001:10001 publish/ /app/\nENV DOTNET_CLI_TELEMETRY_OPTOUT=1\nUSER 10001:10001\n`;await fs.writeFile(path.join(directory,'build/Dockerfile'),dockerfile);const tag='webapi-settings:'+source.sourceManifestHash.slice(0,20);console.log('Building source-manifest identified image');await command('docker',['build','--build-arg','SDK_IMAGE='+sdk,'-t',tag,path.join(directory,'build')]);const inspect=JSON.parse(await command('docker',['image','inspect',tag]))[0];assert.equal(inspect.Config.Labels['com.webapi.settings.source-manifest'],source.sourceManifestHash);image={imageTag:tag,imageDigest:inspect.Id,baseCommit:source.baseCommit,sourceManifestHash:source.sourceManifestHash};
 const overlay={volumes:{'webapi-enterprise-core-test_nuget-test':{external:true}},services:{},secrets:{password:{file:path.join(directory,'password')}}};for(const service of ['postgres','redis','collector','prometheus','loki','tempo','sdk','control-plane','worker','worker-b','gateway-a','gateway-b','backend-a','backend-b','console'])overlay.services[service]={labels:{'com.webapi.settings.owner':owner}};
 for(const name of ['pg','nuget','lkg-a','lkg-b','evidence','lpm','prometheus-data','loki-data','tempo-data'])overlay.volumes[name]={labels:{'com.webapi.settings.owner':owner}};
 for(const [service,target]of Object.entries({'control-plane':'control-plane',worker:'worker','worker-b':'worker','gateway-a':'gateway','gateway-b':'gateway','backend-a':'demo-backend','backend-b':'demo-backend',console:'console-host'})){Object.assign(overlay.services[service],{image:tag,working_dir:'/app',command:['dotnet',`/app/${target}/WebApi.${apps[target]}.dll`]});}
 overlay.services['control-plane'].environment={WEBAPI_BOOTSTRAP_USERNAME:'e2e-admin',WEBAPI_BOOTSTRAP_PASSWORD_FILE:'/run/secrets/password',Authentication__LoginProtection__HmacSecretFile:'/lpm/login-hmac'};overlay.services['control-plane'].secrets=['password','postgres-password','node-a','node-b'];overlay.services.console.environment={Console__DistDirectory:'/app/console-dist'};for(const n of ['gateway-a','gateway-b'])overlay.services[n].environment={TrafficPolicies__RedisConnection:'redis:6379,abortConnect=false,connectTimeout=1000,asyncTimeout=1000',TrafficPolicies__RedisKeyPrefix:'settings-'+owner};
 // Override bind mounts from old source runners; snapshots execute only the frozen image.
 const file=path.join(directory,'settings-overlay.yml');let yaml=JSON.stringify(overlay,null,2);await fs.writeFile(file,yaml);
 const volumeOverride=path.join(directory,'settings-volumes.yml');await fs.writeFile(volumeOverride,'services:\n'+Object.keys(overlay.services).filter(s=>['control-plane','worker','worker-b','backend-a','backend-b','console','gateway-a','gateway-b'].includes(s)).map(s=>`  ${s}:\n    volumes: !override ${s.startsWith('gateway')?`[lkg-${s.endsWith('a')?'a':'b'}:/lkg]`:(s==='control-plane'?`[evidence:/evidence,lpm:/lpm]`:'[]')}\n`).join('')+`  sdk:\n    working_dir: /workspace\n    volumes: !override ["${source.sourceDirectory}:/workspace", "${directory}:/settings-results", "webapi-enterprise-core-test_nuget-test:/root/.nuget/packages:ro"]\n`);
 const composeArgs=settingsComposeArguments(workspace);
 const environment={WEBAPI_E2E_PROJECT:project,WEBAPI_E2E_DIRECTORY:directory,WEBAPI_E2E_RELATIVE_DIR:env.WEBAPI_E2E_RELATIVE_DIR,WEBAPI_ENVIRONMENT_ID:env.WEBAPI_ENVIRONMENT_ID};
 await writePrivate(path.join(directory,'cleanup-context.json'),{...workspace,args:composeArgs,environment});
 await writePrivate(path.join(directory,'review-context.json'),{...workspace,args:composeArgs,environment});
 args=composeArgs;
 const compose=(...a)=>command('docker',[...args,...a]);
 stage='Compose startup';
 console.log('Starting independent database, Redis and three sources');await compose('up','-d','--wait','postgres','redis');await command('docker',['volume','create','--label','com.webapi.settings.owner='+owner,'--label','com.docker.compose.project='+project,'--label','com.docker.compose.volume=evidence',project+'_evidence']);await command('docker',['run','--rm','--user','0:0','-v',project+'_evidence:/evidence',sdk,'bash','-c','chown -R 10001:10001 /evidence && chmod 700 /evidence']);await command('docker',['volume','create','--label','com.webapi.settings.owner='+owner,'--label','com.docker.compose.project='+project,'--label','com.docker.compose.volume=lpm',project+'_lpm']);await command('docker',['run','--rm','--user','0:0','-v',project+'_lpm:/lpm','-v',path.join(directory,'login-hmac')+':/src/login-hmac:ro',sdk,'bash','-c','install -o 10001 -g 10001 -m 600 /src/login-hmac /lpm/login-hmac']);await compose('run','--rm','--user','10001:10001','--workdir','/app','--entrypoint','dotnet','-e','WEBAPI_BOOTSTRAP_USERNAME=e2e-admin','-e','WEBAPI_BOOTSTRAP_PASSWORD_FILE=/run/secrets/password','-e','WEBAPI_MIGRATION_EVIDENCE_DIR=/evidence','-e','WEBAPI_MIGRATION_EVIDENCE_RECORD_MAX_BYTES=8192','-e','WEBAPI_MIGRATION_EVIDENCE_QUOTA_BYTES=33554432','-e','WEBAPI_MIGRATION_EVIDENCE_MIN_FREE_BYTES=0','-e','WEBAPI_MIGRATION_TRANSACTION_SECONDS=900','-e','WEBAPI_MIGRATION_SETTINGS_MAX_BYTES=65536','-e','WEBAPI_MIGRATION_RULE_MAX_BYTES=16384','-e','WEBAPI_MIGRATION_RULE_MAX_COUNT=100000','-e','WEBAPI_MIGRATION_TOTAL_MAX_BYTES=67108864','-e','WEBAPI_MIGRATION_RECEIPT_MAX_ROWS=100000','control-plane','/app/migrator/WebApi.Migrator.dll','--bootstrap','--seed-catalog');await compose('up','-d','collector','prometheus','loki','tempo','control-plane');
 const endpoint=async(s,p=8080)=>'http://'+await compose('port',s,String(p));env.WEBAPI_E2E_CONTROL_PLANE_URL=await endpoint('control-plane');await command(process.execPath,['scripts/wait-http.mjs',env.WEBAPI_E2E_CONTROL_PLANE_URL+'/health/live']);await command(process.execPath,['scripts/prepare-e2e.mjs']);context=JSON.parse(await fs.readFile(path.join(directory,'context.json')));env.WEBAPI_ENVIRONMENT_ID=context.environmentId;await compose('up','-d','--force-recreate','control-plane');env.WEBAPI_E2E_CONTROL_PLANE_URL=await endpoint('control-plane');await compose('create','gateway-a','gateway-b');for(const name of ['lkg-a','lkg-b']){const volume=project+'_'+name,labels=JSON.parse(await command('docker',['volume','inspect','--format','{{json .Labels}}',volume]));assert.equal(labels['com.webapi.settings.owner'],owner);assert.equal(labels['com.docker.compose.project'],project);await command('docker',['run','--rm','--user','0:0','-v',volume+':/settings-lkg',sdk,'bash','-c','chown 10001:10001 /settings-lkg && chmod 700 /settings-lkg']);}await compose('up','-d','worker','worker-b','backend-a','backend-b','gateway-a','gateway-b','console');await command(process.execPath,['scripts/wait-http.mjs',env.WEBAPI_E2E_CONTROL_PLANE_URL+'/health/live']);await command(process.execPath,['scripts/wait-nodes.mjs']);
 const endpoints={control_plane:env.WEBAPI_E2E_CONTROL_PLANE_URL};for(const [s,p]of [['collector',4318],['prometheus',9090],['loki',3100],['tempo',3200],['console',8080],['gateway-a',8080],['gateway-b',8080],['backend-a',8080],['backend-b',8080]])endpoints[s.replaceAll('-','_')]=await endpoint(s,p);environment.WEBAPI_ENVIRONMENT_ID=env.WEBAPI_ENVIRONMENT_ID;const reviewContext={...workspace,endpoints,context,image,args,environment};await writePrivate(path.join(directory,'review-context.json'),reviewContext);
 stage='settings scenario';
 console.log('Running real settings persistence and double-gateway scenarios');result=await runSettingsScenario({root,directory,project,owner,endpoints,context,image,compose,signal});await writePrivate(path.join(directory,'review-context.json'),reviewContext);await fs.writeFile(path.join(directory,'result.json'),JSON.stringify(result,null,2),{mode:0o600});await compose('run','--rm','-e','WEBAPI_SETTINGS_RESULT_DIRECTORY=/settings-results','sdk','dotnet','test','tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj','-c','Release','--filter','Scenario=Settings');await verifyToolManifest(root,tools);result.toolManifestHash=tools.sha256;assertNoSecrets(result,await Promise.all([...secretNames,'credential'].map(n=>fs.readFile(path.join(directory,n),'utf8'))));

   scenarioComplete=true;
   signal?.throwIfAborted();
   if(retainForReview){
     result.review={project,url:endpoints.console,privateContextFile:path.join(directory,'review-context.json')};
   }else{
     stage='cleanup';
     receipt=await cleanup();
     result.cleanup=receipt;
     if(receipt.failed)throw Object.assign(Error(receipt.error),{settingsPublicMessage:receipt.error});
   }
   result.complete=true;
   await withSettingsRecords(root,async()=>{
     signal?.throwIfAborted();
     if(retainForReview)await publishSettingsPointer(root,workspace,endpoints.console,true);
     await writePublicJson(path.join(publicDirectory,'verification.json'),result);
     if(!retainForReview)await retireSettingsContext(root,workspace);
   });
   signal?.throwIfAborted();
   if(retainForReview)console.log('Independent review entry: '+endpoints.console);
   console.log('Settings scenario complete: '+result.checks.length+' verified checks');
   return {sourceManifestHash:source.sourceManifestHash,images:[image],resultPath:path.join(publicDirectory,'verification.json'),reviewContextPath:retainForReview?path.join(directory,'review-context.json'):null};
 }catch(error){
   workspace??=error.settingsWorkspace;
   receipt??=error.settingsCleanup;
   if(workspace){
     ({project,owner,directory}=workspace);
     if(args&&!receipt)for(const service of ['worker','gateway-a','gateway-b'])await command('docker',[...args,'logs','--tail','150',service],{signal:undefined}).catch(()=>{});
     receipt??=await cleanup().catch(()=>({failed:true,secretsRemaining:null,error:'Settings cleanup ownership could not be verified'}));
   }
   const errorCode=typeof error.code==='string' && /^[A-Z0-9_]{1,48}$/.test(error.code) ? ' ('+error.code+')' : '';
   error.settingsPublicMessage??=(signal?.aborted ? 'Settings acceptance interrupted during ' : 'Settings acceptance failed during ')+stage+errorCode;
   const failure={complete:false,project,time:new Date().toISOString(),message:error.settingsPublicMessage,cleanup:receipt};
   try{
     if(workspace)await writePrivate(path.join(directory,'cleanup.json'),{project,...receipt,time:failure.time,message:failure.message});
     await withSettingsRecords(root,async()=>{
       await writePublicJson(path.join(publicDirectory,'last-failure.json'),failure);
       const historyFile=path.join(publicDirectory,'failed-attempts.json');
       let history=[];
       try{history=JSON.parse(await fs.readFile(historyFile));}catch(readError){if(readError.code!=='ENOENT')throw readError;}
       history.push(failure);
       await writePublicJson(historyFile,history);
       if(workspace){
         if(receipt.failed){
           if(scenarioComplete)await writePublicJson(path.join(publicDirectory,'verification.json'),{...result,scenarioComplete:true,complete:false,cleanup:receipt});
           await publishSettingsPointer(root,workspace,null,false);
         }else{
           if(signal?.aborted&&scenarioComplete){
             const file=path.join(publicDirectory,'verification.json');
             const current=await fs.readFile(file,'utf8').catch(readError=>{if(readError.code!=='ENOENT')throw readError;return 'null';});
             if(JSON.parse(current)?.project===project)await writePublicJson(file,{...result,scenarioComplete:true,complete:false,cleanup:receipt});
           }
           await retireSettingsContext(root,workspace);
         }
       }
     });
   }catch{
     console.error('Settings failure evidence could not be fully recorded.');
   }
   throw error;
 }
}
if(process.argv[1]&&path.resolve(process.argv[1])===path.resolve(import.meta.filename)){
 try{await withAcceptanceSignals(signal=>runSettingsAcceptance({root:path.resolve(import.meta.dirname,'../..'),retainForReview:process.argv.includes('--retain-for-review'),signal}));}
 catch(error){console.error(error.settingsPublicMessage||'Settings acceptance failed');process.exitCode=1;}
}
