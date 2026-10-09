import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {createOwner,cleanupOwned} from './owner.mjs';
import {exportInputs} from './sources.mjs';
import {buildMatrix} from './matrix.mjs';
import {cellKey} from './report.mjs';
import {scanPublicEvidence,removePrivateOwner} from './secrets.mjs';
import {resourceId} from './management.mjs';
import {verifyEnvironment} from './environment.mjs';
import {retainSqlAudit} from './audit.mjs';
import {validateClassificationManifest} from './classification.mjs';
import {verifyBaselineCompletion} from './completion.mjs';
import {runWithContainerResources,verifyResourceDiagnosticCompletion} from './resources.mjs';
import {verifyExplainCompletion} from './explain.mjs';
import {privateJson,evidence,docker,pickSubnet,assembleValidation,build,waitReady} from "./runner-support.mjs";
export async function runPerformance(options,repo){
  if(options.mode==='compare'||options.mode==='validate'&&options.diagnostic==='compare')return (await import('./comparison-runner.mjs')).runComparison(options,repo);
  if(options.mode==='migration'||options.mode==='validate'&&options.diagnostic==='migration')return (await import('./migration-runner.mjs')).runMigration(options,repo);
  if(options.mode==='explain'){
    await fs.mkdir(options.output,{recursive:true});if((await fs.readdir(options.output)).length)throw Error('Performance output directory must be empty');
    const windowStart=new Date(Date.now()-60000).toISOString(),windowEnd=new Date(Date.now()+20*3600000).toISOString(),variants=[];
    for(const variant of ['baseline','candidate'])variants.push(await runPerformance({...options,mode:'explain-one',source:options[variant],variant,windowStart,windowEnd,output:path.join(options.output,variant)},repo));
    const summary={mode:'explain',measurementScope:'explain-diagnostic-not-performance',baseline:options.baseline,candidate:options.candidate,harness:options.harness,variants,fullBaselineComplete:false,complete:variants.every(v=>v.diagnosticComplete&&v.immutable)};
    await evidence(path.join(options.output,'manifest.json'),summary);return summary;
  }
  if(!['validate','baseline','explain-one'].includes(options.mode))throw Error('Unsupported performance runner mode; no measurement started');
  const diagnostic=options.mode==='explain-one'||options.diagnostic==='explain',resourceDiagnostic=options.mode==='validate'&&options.diagnostic==='resources';
  await fs.mkdir(path.join(repo,'.runtime','performance'),{recursive:true,mode:0o700});
  await fs.mkdir(options.output,{recursive:true});
  if((await fs.readdir(options.output)).length)throw Error('Performance output directory must be empty');
  const manifest=await createOwner(path.join(repo,'.runtime','performance'));const buildDirectory=path.join(manifest.directory,'build');await fs.mkdir(buildDirectory);
  let composeFile,environment,receipt,status,completionInput,succeeded=false;const results=[];
  try {
    const sourceDirectory=path.join(buildDirectory,'source');
    const identity=options.mode==='validate'?await assembleValidation(repo,sourceDirectory):await exportInputs({repo,source:options.source??options.baseline,harness:options.harness,directory:sourceDirectory});
    let classificationManifestHash=null;
    // A resources-only lifecycle check issues no management queries and makes
    // no SQL classification claim; SQL-producing modes retain strict source binding.
    if(!resourceDiagnostic){
      const classificationPath=options.classifications??path.join(sourceDirectory,'scripts/performance/sql-classifications-baseline.json');
      const classificationInfo=await fs.lstat(classificationPath);if(!classificationInfo.isFile()||classificationInfo.isSymbolicLink()||classificationInfo.size>1024*1024)throw Error('Invalid SQL classification input file');
      const manifestJson=await fs.readFile(classificationPath,'utf8'),classificationManifest=validateClassificationManifest(JSON.parse(manifestJson),identity.source);classificationManifestHash=createHash('sha256').update(manifestJson).digest('hex');
      await privateJson(path.join(manifest.directory,'sql-classification-input.json'),{manifestJson,manifestHash:classificationManifestHash,sourceCommit:identity.source});
      await evidence(path.join(options.output,'sql-classification-manifest.json'),classificationManifest);
    }
    const image=await build(manifest,sourceDirectory,identity,options.output);
    composeFile=path.join(sourceDirectory,'deploy/compose.performance.yml');
    environment={...process.env,PERF_OWNER:manifest.owner,PERF_DIRECTORY:manifest.directory,PERF_RESULT_DIRECTORY:options.output,PERF_HARNESS_DIRECTORY:sourceDirectory,PERF_IMAGE:image.imageId,PERF_SUBNET:await pickSubnet()};
    manifest.composeEnvironment=Object.fromEntries(['PERF_IMAGE','PERF_SUBNET','PERF_RESULT_DIRECTORY','PERF_HARNESS_DIRECTORY'].map(key=>[key,environment[key]]));await privateJson(path.join(manifest.directory,'owner.json'),manifest);
    const compose=(...args)=>docker(['compose','-p',manifest.project,'-f',composeFile,...args],{env:environment});
    const secret=(await fs.readFile(path.join(manifest.directory,'postgres-password'),'utf8')).trim();
    const connection=`Host=postgres;Database=webapi_performance;Username=webapi;Password=${secret};Maximum Pool Size=32;Timeout=30;Include Error Detail=false`;
    const seed=20261009;const cryptoStart=options.windowStart??new Date(Date.now()-60000).toISOString(),cryptoEnd=options.windowEnd??new Date(Date.now()+20*3600000).toISOString();
    const envId=resourceId(seed,'environment',0);
    const common={'urls':'http://0.0.0.0:8080','Performance:PrivateDirectory':'/private','Redis:Connection':'redis:6379,abortConnect=false','Observability:Enabled':'false','Upstream:AllowedOrigins:0':'http://backend:8080'};
    const auditConfig=diagnostic?{'Performance:ExplainEnabled':'true','Performance:DiagnosticMode':'explain'}:{};if(options['sql-audit']){
      if(options.mode!=='validate')throw Error('SQL audit is not permitted during measurement');
      await fs.mkdir(path.join(manifest.directory,'sql-audit'),{mode:0o700});auditConfig['Performance:SqlAuditDirectory']='/private/sql-audit';
    }
    await privateJson(path.join(manifest.directory,'host-backend.json'),common);
    await privateJson(path.join(manifest.directory,'host-controlplane.json'),{...common,...auditConfig,'ConnectionStrings:WebApi':connection,'Authentication:LoginProtection:HmacSecretFile':'/private/login-hmac','Authentication:LoginProtection:RedisPrefix':'performance-'+manifest.owner,'DataProtection:KeysDirectory':'/private/data-protection','DataProtection:ApplicationName':manifest.project});
    for(const name of ['gateway-a','gateway-b'])await privateJson(path.join(manifest.directory,`host-${name}.json`),{...common,'Gateway:EnvironmentId':envId,'Gateway:NodeName':name,'Gateway:SecretFile':'/private/gateway-secret','Gateway:LkgDirectory':'/private/lkg-'+name,'Gateway:ControlPlaneUrl':'http://controlplane:8080','Gateway:AutomaticUpdates':'false'});
    await compose('up','-d','--wait','postgres','redis');
    const scales=resourceDiagnostic?[100,10000]:options.mode==='validate'?[100]:diagnostic?[100,10000]:[100,1000,10000];
    for(const scale of scales){
      await privateJson(path.join(manifest.directory,`spec-${scale}.json`),{scale,seed,windowStart:cryptoStart,windowEnd:cryptoEnd});
      await compose('run','--rm','--no-deps','controlplane','dotnet','WebApi.PerformanceHost.dll','prepare','--spec',`/private/spec-${scale}.json`,'--directory',`/private/scales/${scale}`,'--output',`/results/gateway-${scale}.json`);
    }
    await compose('run','--rm','--no-deps','controlplane','dotnet','WebApi.PerformanceHost.dll','seed','--spec','/private/spec-100.json','--config','/private/host-controlplane.json','--directory','/private','--output','/results/management-100.json');
    const analyze=()=>compose('exec','-T','postgres','psql','-U','webapi','-d','webapi_performance','-v','ON_ERROR_STOP=1','-c','ANALYZE');
    if(diagnostic)await analyze();
    await compose('up','-d','controlplane','backend','gateway-a','gateway-b');
    const addresses={};for(const name of ['gateway-a','gateway-b','controlplane']){
      addresses[name]='http://'+(await compose('port',name,'8080')).stdout.trim();await waitReady(addresses[name]);
    }
    const token=(await fs.readFile(path.join(manifest.directory,'probe-token'),'utf8')).trim();let sequence=0;
    await compose('create','client');
    const containerIds=(await compose('ps','--all','--quiet')).stdout.trim().split(/\s+/).filter(Boolean);
    const format='{"labels":{{json .Config.Labels}},"nanoCpus":{{json .HostConfig.NanoCpus}},"memoryBytes":{{json .HostConfig.Memory}},"imageId":{{json .Image}},"user":{{json .Config.User}}}';
    const actualContainers=(await docker(['inspect','--format',format,...containerIds])).stdout.trim().split('\n').map(line=>JSON.parse(line));
    const host=JSON.parse((await docker(['info','--format','{"version":{{json .ServerVersion}},"architecture":{{json .Architecture}},"cpus":{{json .NCPU}},"memoryBytes":{{json .MemTotal}},"operatingSystem":{{json .OperatingSystem}}}'])).stdout);
    const actualEnvironment=verifyEnvironment(manifest,actualContainers,host);
    const runtimeState={};for(const name of ['gateway-a','gateway-b','controlplane']){
      const response=await fetch(addresses[name]+'/__perf/state',{headers:{'X-Performance-Token':token}});if(!response.ok)throw Error('Actual runtime settings unavailable');
      const value=await response.json();if(value.processorCount!==2||!value.serverGc||value.totalAvailableMemoryBytes<=0)throw Error('Actual GC/processor configuration mismatch');
      runtimeState[name]={processorCount:value.processorCount,serverGc:value.serverGc,totalAvailableMemoryBytes:value.totalAvailableMemoryBytes};
    }
    actualEnvironment.runtimeState=runtimeState;
    const pgSettings=(await compose('exec','-T','postgres','psql','-U','webapi','-d','webapi_performance','-At','-c',"SELECT json_build_object('maxConnections',current_setting('max_connections'),'sharedBuffers',current_setting('shared_buffers'),'workMem',current_setting('work_mem'),'maintenanceWorkMem',current_setting('maintenance_work_mem'),'parallelWorkersPerGather',current_setting('max_parallel_workers_per_gather'),'timezone',current_setting('TimeZone'))")).stdout.trim();
    actualEnvironment.databaseActual=JSON.parse(pgSettings);
    if(actualEnvironment.databaseActual.maxConnections!=='100'||actualEnvironment.databaseActual.sharedBuffers!=='512MB'||actualEnvironment.databaseActual.workMem!=='16MB'||actualEnvironment.databaseActual.maintenanceWorkMem!=='256MB'||actualEnvironment.databaseActual.parallelWorkersPerGather!=='0'||actualEnvironment.databaseActual.timezone!=='UTC')throw Error('Actual database settings mismatch');
    const dockerHost=JSON.parse((await docker(['context','inspect','--format','{{json .Endpoints.docker.Host}}'])).stdout.trim());
    const apiVersion=(await docker(['version','--format','{{.Server.APIVersion}}'])).stdout.trim();
    actualEnvironment.containerSampler={source:'docker-engine-stats-stream',apiVersion,memoryPeakScope:'sampled-points-inside-window',cpuScope:'intervals-wholly-inside-window'};
    await evidence(path.join(options.output,'environment.json'),actualEnvironment);
    const monitoredClient=async(key,resultFile,args=[])=>{
      const resources=await runWithContainerResources({manifest,dockerHost,apiVersion,output:options.output,key,resultFile},()=>compose('run','--rm','--no-deps','client',...args));
      const measured=JSON.parse(await fs.readFile(path.join(options.output,resultFile),'utf8'));
      measured.containerResources=resources;
      await evidence(path.join(options.output,resultFile),measured);
      if(!resources.complete)throw Error('Container resource sampling incomplete; see '+key+'.resources.json');
      return measured;
    };
    const cells=resourceDiagnostic?[1,2,3].map(repeat=>({variant:'validation',scale:10000,auth:'api-key',concurrency:32,repeat,nodeMode:'dual'})):diagnostic?[]:options.mode==='validate'?['anonymous','api-key','missing-key','rs256','es256','wrong-secret','no-mapping','no-grant'].flatMap(auth=>[1,2].map(concurrency=>({variant:'validation',scale:100,auth,concurrency,repeat:1,nodeMode:'dual',validation:true}))):buildMatrix({variants:['baseline']});
    for(const cell of cells){
      const profile=cell.auth==='api-key'||cell.auth==='missing-key'||cell.auth==='wrong-secret'?'apikey':cell.auth==='no-mapping'?'rs256':cell.auth;
      await fs.copyFile(path.join(manifest.directory,`scales/${cell.scale}/${profile}.json`),path.join(manifest.directory,profile+'.json'));await fs.chmod(path.join(manifest.directory,profile+'.json'),0o600);
      sequence++;for(const name of ['gateway-a','gateway-b']){
        const response=await fetch(addresses[name]+'/__perf/apply',{method:'POST',headers:{'Content-Type':'application/json','X-Performance-Token':token},body:JSON.stringify({profile,sequence}),signal:AbortSignal.timeout(120000)});
        if(!response.ok)throw Error('Real gateway activation failed: '+response.status);
      }
      await privateJson(path.join(manifest.directory,'cell.json'),cell);
      results.push(await monitoredClient(cellKey(cell),cellKey(cell)+'.json'));
    }
    const management=[],scopeManagement=[],referenceManagement=[];
    for(const scale of resourceDiagnostic?[]:scales){
      if(scale!==100){
        await compose('stop','controlplane');
        // These commands address only the database inside this random owner project.
        await compose('exec','-T','postgres','dropdb','-U','webapi','--if-exists','webapi_performance');await compose('exec','-T','postgres','createdb','-U','webapi','webapi_performance');
        await compose('run','--rm','--no-deps','controlplane','dotnet','WebApi.PerformanceHost.dll','seed','--spec',`/private/spec-${scale}.json`,'--config','/private/host-controlplane.json','--directory','/private','--output',`/results/management-${scale}.json`);
        if(diagnostic)await analyze();
        await compose('up','-d','controlplane');addresses.controlplane='http://'+(await compose('port','controlplane','8080')).stdout.trim();await waitReady(addresses.controlplane);
      }
      management.push(await monitoredClient(`ordinary-${scale}`,`management-queries-${scale}.json`,['node','scripts/performance/management-client.mjs','ordinary',String(scale),String(options.mode==='validate'||diagnostic),String(diagnostic)]));
      if(!diagnostic)for(const commandName of ['generation','micro'])await compose('run','--rm','--no-deps','controlplane','dotnet','WebApi.PerformanceHost.dll',commandName,'--snapshot',`/private/scales/${scale}/rs256.json`,'--environment',envId,'--output',`/results/${commandName}-${scale}.json`);
      await compose('stop','controlplane');
      await compose('exec','-T','postgres','dropdb','-U','webapi','--if-exists','webapi_performance');await compose('exec','-T','postgres','createdb','-U','webapi','webapi_performance');
      await compose('run','--rm','--no-deps','controlplane','dotnet','WebApi.PerformanceHost.dll','scope-seed','--spec',`/private/spec-${scale}.json`,'--config','/private/host-controlplane.json','--directory','/private','--output',`/results/scope-dataset-${scale}.json`);
      if(diagnostic)await analyze();
      await fs.copyFile(path.join(options.output,`scope-dataset-${scale}.json`),path.join(manifest.directory,`scope-${scale}.json`));await fs.chmod(path.join(manifest.directory,`scope-${scale}.json`),0o600);
      await compose('up','-d','controlplane');addresses.controlplane='http://'+(await compose('port','controlplane','8080')).stdout.trim();await waitReady(addresses.controlplane);
      scopeManagement.push(await monitoredClient(`scope-${scale}`,`scope-queries-${scale}.json`,['node','scripts/performance/management-client.mjs','scope',String(scale),String(options.mode==='validate'||diagnostic),String(diagnostic)]));
      await compose('stop','controlplane');
      await compose('exec','-T','postgres','dropdb','-U','webapi','--if-exists','webapi_performance');await compose('exec','-T','postgres','createdb','-U','webapi','webapi_performance');
      await compose('run','--rm','--no-deps','controlplane','dotnet','WebApi.PerformanceHost.dll','reference-seed','--spec',`/private/spec-${scale}.json`,'--config','/private/host-controlplane.json','--directory','/private','--output',`/results/reference-dataset-${scale}.json`);
      if(diagnostic)await analyze();
      await fs.copyFile(path.join(options.output,`reference-dataset-${scale}.json`),path.join(manifest.directory,`reference-${scale}.json`));await fs.chmod(path.join(manifest.directory,`reference-${scale}.json`),0o600);
      await compose('up','-d','controlplane');addresses.controlplane='http://'+(await compose('port','controlplane','8080')).stdout.trim();await waitReady(addresses.controlplane);
      referenceManagement.push(await monitoredClient(`reference-${scale}`,`reference-queries-${scale}.json`,['node','scripts/performance/management-client.mjs','reference',String(scale),String(options.mode==='validate'||diagnostic),String(diagnostic)]));
    }
    const sqlAudit=options['sql-audit']?await retainSqlAudit(path.join(manifest.directory,'sql-audit'),options['sql-audit']):null;
    status={mode:options.mode,immutable:options.mode!=='validate',identity,image,environment:actualEnvironment.fingerprint,owner:manifest.owner,gatewayCells:results.length,gatewayCellsComplete:results.length>0&&results.every(r=>r.complete&&r.expected>0&&!r.unexpected&&!r.timeout&&!r.incomplete),fullBaselineComplete:false,managementBaselineComplete:!resourceDiagnostic&&!diagnostic&&management.every(m=>m.complete),scopeBaselineComplete:!resourceDiagnostic&&!diagnostic&&scopeManagement.every(m=>m.complete),referenceBaselineComplete:!resourceDiagnostic&&!diagnostic&&referenceManagement.every(m=>m.complete),sqlClassificationComplete:!resourceDiagnostic&&[...management,...scopeManagement,...referenceManagement].every(m=>m.classificationComplete),sqlClassificationManifestHash:classificationManifestHash,containerResourcesComplete:[...results,...management,...scopeManagement,...referenceManagement].every(m=>m.containerResources?.complete===true),sqlAudit};
    const readScaleEvidence=async prefix=>Promise.all(scales.map(async scale=>JSON.parse(await fs.readFile(path.join(options.output,`${prefix}-${scale}.json`),'utf8'))));
    const microEntries=diagnostic||resourceDiagnostic?[]:await readScaleEvidence('micro'),generationEntries=diagnostic||resourceDiagnostic?[]:await readScaleEvidence('generation');
    if(diagnostic){status.measurementScope='explain-diagnostic-not-performance';status.variant=options.variant??'validation';status.scales=scales;status.diagnosticComplete=false;}
    if(resourceDiagnostic){status.measurementScope='resource-window-diagnostic-not-performance';status.diagnosticComplete=false;}
    completionInput={mode:options.mode,immutable:status.immutable,identity,environment:actualEnvironment,classificationManifestHash:classificationManifestHash,cells:results,gatewayDatasets:await readScaleEvidence('gateway'),management,scopeManagement,referenceManagement,micro:microEntries.map((entries,i)=>({scale:scales[i],entries})),generation:generationEntries.map((entry,i)=>({...entry,scale:scales[i]}))};
    await evidence(path.join(options.output,'manifest.json'),status);
    succeeded=true;
    return status;
  }finally{
    {
      receipt=await cleanupOwned(manifest,composeFile);
      if(succeeded){
        const scan=await scanPublicEvidence(options.output,manifest.directory);
        await evidence(path.join(options.output,'secret-scan.json'),scan);
        if(scan.violations)throw Error('Public evidence secret scan failed; private diagnostics retained');
        await removePrivateOwner(manifest,receipt);receipt.privateInputsDeleted=true;
        // Publish completion only after actual resource cleanup, secret scan and
        // private-file removal succeed. Before this point manifest stays false.
        status.baselineVerification=verifyBaselineCompletion({...completionInput,cleanup:receipt,secretScan:scan});
        status.fullBaselineComplete=status.baselineVerification.complete;
        if(resourceDiagnostic)status.diagnosticComplete=verifyResourceDiagnosticCompletion({cells:results,cleanup:receipt,secretScan:scan});
        if(diagnostic)status.diagnosticComplete=verifyExplainCompletion({scales:status.scales,ordinary:completionInput.management,scope:completionInput.scopeManagement,reference:completionInput.referenceManagement,cleanup:receipt,secretScan:scan});
        await evidence(path.join(options.output,'cleanup.json'),receipt);
        await evidence(path.join(options.output,'manifest.json'),status);
      }
      else receipt.privateInputsRetainedForDiagnosis=true;
      await evidence(path.join(options.output,'cleanup.json'),receipt);
    }
  }
}
