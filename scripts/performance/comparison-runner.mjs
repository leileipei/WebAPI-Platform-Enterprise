import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {createOwner,cleanupOwned} from './owner.mjs';
import {exportInputs} from './sources.mjs';
import {buildComparisonPairs} from './matrix.mjs';
import {cellKey} from './report.mjs';
import {executeComparisonPairs,verifyManagementParity,verifyPairReceipts} from './comparison.mjs';
import {captureDatabaseFixture,restoreDatabaseFixture,verifyFixtureRestore} from './database-fixture.mjs';
import {scanPublicEvidence,removePrivateOwner} from './secrets.mjs';
import {resourceId} from './management.mjs';
import {validateClassificationManifest} from './classification.mjs';
import {verifyEnvironment} from './environment.mjs';
import {verifyBaselineCompletion} from './completion.mjs';
import {runWithContainerResources} from './resources.mjs';
import {privateJson,evidence,docker,pickSubnet,assembleValidation,build,command,waitReady} from './runner-support.mjs';
const appServices=['controlplane','backend','gateway-a','gateway-b'];
export async function runComparison(options,repo){
  const mutable=options.mode==='validate';
  if(!mutable)for(const id of [options.baseline,options.candidate,options.harness]){
    try{if(!/^[a-f0-9]{40}$/.test(id??'')||(await command('git',['-C',repo,'rev-parse','--verify',id+'^{commit}'])).stdout.trim()!==id)throw Error();}
    catch{throw Error('Immutable comparison source commit missing');}
  }
  await fs.mkdir(options.output,{recursive:true});if((await fs.readdir(options.output)).length)throw Error('Comparison output directory must be empty');
  await fs.mkdir(path.join(repo,'.runtime','performance'),{recursive:true,mode:0o700});const owner=await createOwner(path.join(repo,'.runtime','performance'));
  const identities={},images={},variants={},environments={},managementPairs=[],fixtureProofs=[];let composeFile,status,succeeded=false;
  try{
    for(const variant of ['baseline','candidate']){
      const directory=path.join(owner.directory,'build-'+variant);await fs.mkdir(directory,{mode:0o700});const source=path.join(directory,'source');
      identities[variant]=mutable?await assembleValidation(repo,source):await exportInputs({repo,source:options[variant],harness:options.harness,directory:source});
      const output=path.join(options.output,variant);await fs.mkdir(output);
      if(variant==='candidate'&&identities.baseline.harnessFingerprint!==identities.candidate.harnessFingerprint)throw Error('Comparison harness input mismatch');
      images[variant]=mutable&&variant==='candidate'?images.baseline:await build(owner,source,identities[variant],output);
      variants[variant]={mode:'compare',variant,immutable:!mutable,identity:identities[variant],cells:[],gatewayDatasets:[],management:[],scopeManagement:[],referenceManagement:[],micro:[],generation:[]};
      if(variant==='baseline')composeFile=path.join(source,'deploy/compose.performance.yml');
    }
    const sourceDirectory=path.join(owner.directory,'build-baseline','source'),classificationPath=options.classifications??path.join(sourceDirectory,'scripts/performance/sql-classifications-baseline.json');
    const classificationInfo=await fs.lstat(classificationPath);if(!classificationInfo.isFile()||classificationInfo.isSymbolicLink()||classificationInfo.size>1024*1024)throw Error('Invalid SQL classification input');
    const manifestJson=await fs.readFile(classificationPath,'utf8'),classificationManifestHash=createHash('sha256').update(manifestJson).digest('hex');
    const classifications=JSON.parse(manifestJson);for(const variant of ['baseline','candidate'])validateClassificationManifest(classifications,identities[variant].source);
    await evidence(path.join(options.output,'sql-classification-manifest.json'),classifications);
    const environment={...process.env,PERF_OWNER:owner.owner,PERF_DIRECTORY:owner.directory,PERF_RESULT_DIRECTORY:options.output,PERF_HARNESS_DIRECTORY:sourceDirectory,PERF_IMAGE:images.baseline.imageId,PERF_SUBNET:await pickSubnet()};
    owner.composeEnvironment=Object.fromEntries(['PERF_IMAGE','PERF_SUBNET','PERF_RESULT_DIRECTORY','PERF_HARNESS_DIRECTORY'].map(k=>[k,environment[k]]));await privateJson(path.join(owner.directory,'owner.json'),owner);
    const compose=(...args)=>docker(['compose','-p',owner.project,'-f',composeFile,...args],{env:environment});
    const password=(await fs.readFile(path.join(owner.directory,'postgres-password'),'utf8')).trim(),seed=20261009,envId=resourceId(seed,'environment',0);
    const common={urls:'http://0.0.0.0:8080','Performance:PrivateDirectory':'/private','Redis:Connection':'redis:6379,abortConnect=false','Observability:Enabled':'false','Upstream:AllowedOrigins:0':'http://backend:8080'};
    await privateJson(path.join(owner.directory,'host-backend.json'),common);
    await privateJson(path.join(owner.directory,'host-controlplane.json'),{...common,'ConnectionStrings:WebApi':`Host=postgres;Database=webapi_performance;Username=webapi;Password=${password};Maximum Pool Size=32;Timeout=30;Include Error Detail=false`,'Authentication:LoginProtection:HmacSecretFile':'/private/login-hmac','Authentication:LoginProtection:RedisPrefix':'performance-'+owner.owner,'DataProtection:KeysDirectory':'/private/data-protection','DataProtection:ApplicationName':owner.project});
    for(const name of ['gateway-a','gateway-b'])await privateJson(path.join(owner.directory,'host-'+name+'.json'),{...common,'Gateway:EnvironmentId':envId,'Gateway:NodeName':name,'Gateway:SecretFile':'/private/gateway-secret','Gateway:LkgDirectory':'/private/lkg-'+name,'Gateway:ControlPlaneUrl':'http://controlplane:8080','Gateway:AutomaticUpdates':'false'});
    const scales=mutable?[100]:[100,1000,10000],windowStart=new Date(Date.now()-60000).toISOString(),windowEnd=new Date(Date.now()+20*3600000).toISOString();
    for(const scale of scales)await privateJson(path.join(owner.directory,'spec-'+scale+'.json'),{scale,seed,windowStart,windowEnd});
    await compose('up','-d','--wait','postgres','redis');const postgresId=(await compose('ps','--quiet','postgres')).stdout.trim();
    const hostOperation=async(variant,operation,args,file)=>{
      environment.PERF_IMAGE=images[variant].imageId;environment.PERF_RESULT_DIRECTORY=options.output;
      await compose('run','--rm','--no-deps','controlplane','dotnet','WebApi.PerformanceHost.dll',operation,...args,'--output','/results/'+file);
      return JSON.parse(await fs.readFile(path.join(options.output,file),'utf8'));
    };
    const inspect=(variant,file)=>hostOperation(variant,'migration-inspect',['--config','/private/host-controlplane.json'],file);
    const reset=async()=>{await compose('exec','-T','postgres','dropdb','-U','webapi','--if-exists','webapi_performance');await compose('exec','-T','postgres','createdb','-U','webapi','webapi_performance');};
    const analyze=()=>compose('exec','-T','postgres','psql','-U','webapi','-d','webapi_performance','-v','ON_ERROR_STOP=1','-c','ANALYZE');
    for(const scale of scales){
      const dataset=await hostOperation('baseline','prepare',['--spec',`/private/spec-${scale}.json`,'--directory',`/private/scales/${scale}`],`gateway-${scale}.json`);
      for(const variant of ['baseline','candidate'])variants[variant].gatewayDatasets.push(dataset);
    }
    await hostOperation('baseline','seed',['--spec','/private/spec-100.json','--config','/private/host-controlplane.json','--directory','/private'],'gateway-controlplane-dataset.json');
    const token=(await fs.readFile(path.join(owner.directory,'probe-token'),'utf8')).trim();
    const dockerHost=JSON.parse((await docker(['context','inspect','--format','{{json .Endpoints.docker.Host}}'])).stdout.trim()),apiVersion=(await docker(['version','--format','{{.Server.APIVersion}}'])).stdout.trim();
    const addresses={};let sequence=0,currentVariant;
    const switchVariant=async(variant)=>{
      environment.PERF_IMAGE=images[variant].imageId;environment.PERF_RESULT_DIRECTORY=path.join(options.output,variant);
      await privateJson(path.join(owner.directory,'sql-classification-input.json'),{manifestJson,manifestHash:classificationManifestHash,sourceCommit:identities[variant].source});
      await compose('up','-d','--no-deps','--force-recreate',...appServices);await compose('create','--force-recreate','client');
      for(const name of ['gateway-a','gateway-b','controlplane']){addresses[name]='http://'+(await compose('port',name,'8080')).stdout.trim();await waitReady(addresses[name]);}
      const ids=(await compose('ps','--all','--quiet')).stdout.trim().split(/\s+/).filter(Boolean),format='{"labels":{{json .Config.Labels}},"nanoCpus":{{json .HostConfig.NanoCpus}},"memoryBytes":{{json .HostConfig.Memory}},"imageId":{{json .Image}},"user":{{json .Config.User}}}';
      const actual=(await docker(['inspect','--format',format,...ids])).stdout.trim().split('\n').map(JSON.parse);
      const host=JSON.parse((await docker(['info','--format','{"version":{{json .ServerVersion}},"architecture":{{json .Architecture}},"cpus":{{json .NCPU}},"memoryBytes":{{json .MemTotal}},"operatingSystem":{{json .OperatingSystem}}}'])).stdout);
      const verified=verifyEnvironment(owner,actual,host);if(appServices.some(s=>verified.services[s].imageId!==images[variant].imageId))throw Error('Measured application source image mismatch');
      verified.runtimeState={};for(const name of ['gateway-a','gateway-b','controlplane']){
        const response=await fetch(addresses[name]+'/__perf/state',{headers:{'X-Performance-Token':token}});if(!response.ok)throw Error('Runtime state unavailable');const value=await response.json();
        if(value.processorCount!==2||!value.serverGc||value.totalAvailableMemoryBytes<=0)throw Error('Actual GC configuration mismatch');verified.runtimeState[name]={processorCount:value.processorCount,serverGc:value.serverGc,totalAvailableMemoryBytes:value.totalAvailableMemoryBytes};
      }
      verified.databaseActual=JSON.parse((await compose('exec','-T','postgres','psql','-U','webapi','-d','webapi_performance','-At','-c',"SELECT json_build_object('maxConnections',current_setting('max_connections'),'sharedBuffers',current_setting('shared_buffers'),'workMem',current_setting('work_mem'),'maintenanceWorkMem',current_setting('maintenance_work_mem'),'parallelWorkersPerGather',current_setting('max_parallel_workers_per_gather'),'timezone',current_setting('TimeZone'))")).stdout.trim());
      if(Object.entries({maxConnections:'100',sharedBuffers:'512MB',workMem:'16MB',maintenanceWorkMem:'256MB',parallelWorkersPerGather:'0',timezone:'UTC'}).some(([k,v])=>verified.databaseActual[k]!==v))throw Error('Database settings mismatch');
      verified.containerSampler={source:'docker-engine-stats-stream',apiVersion,memoryPeakScope:'sampled-points-inside-window',cpuScope:'intervals-wholly-inside-window'};
      if(environments[variant]&&environments[variant].fingerprint!==verified.fingerprint||Object.values(environments).some(e=>e.fingerprint!==verified.fingerprint))throw Error('Comparison environment changed');
      environments[variant]=verified;variants[variant].environment=verified;variants[variant].classificationManifestHash=classificationManifestHash;await evidence(path.join(options.output,variant,'environment.json'),verified);currentVariant=variant;
    };
    const monitoredClient=async(key,resultFile,args=[])=>{
      const output=path.join(options.output,currentVariant),resources=await runWithContainerResources({manifest:owner,dockerHost,apiVersion,output,key,resultFile},()=>compose('run','--rm','--no-deps','client',...args));
      const result=JSON.parse(await fs.readFile(path.join(output,resultFile),'utf8'));Object.assign(result,{containerResources:resources,sourceCommit:identities[currentVariant].source,harnessFingerprint:identities[currentVariant].harnessFingerprint,environmentFingerprint:environments[currentVariant].fingerprint,imageId:images[currentVariant].imageId});
      await evidence(path.join(output,resultFile),result);if(!resources.complete)throw Error('Comparison container resource window incomplete');return result;
    };
    const pairs=mutable?['anonymous','api-key','missing-key','rs256','es256','wrong-secret','no-mapping','no-grant'].flatMap((auth,index)=>[1,2].map(repeat=>({pairId:`100:${auth}:1:${repeat}:dual`,cells:((index+repeat-1)%2===0?['baseline','candidate']:['candidate','baseline']).map(variant=>({variant,scale:100,auth,concurrency:1,repeat,nodeMode:'dual',validation:true}))}))):buildComparisonPairs();
    const gateway=await executeComparisonPairs({pairs,switchVariant:c=>switchVariant(c.variant),measure:async cell=>{
      const profile=['api-key','missing-key','wrong-secret'].includes(cell.auth)?'apikey':cell.auth==='no-mapping'?'rs256':cell.auth;
      await fs.copyFile(path.join(owner.directory,`scales/${cell.scale}/${profile}.json`),path.join(owner.directory,profile+'.json'));await fs.chmod(path.join(owner.directory,profile+'.json'),0o600);sequence++;
      for(const name of ['gateway-a','gateway-b']){const response=await fetch(addresses[name]+'/__perf/apply',{method:'POST',headers:{'Content-Type':'application/json','X-Performance-Token':token},body:JSON.stringify({profile,sequence}),signal:AbortSignal.timeout(120000)});if(!response.ok)throw Error('Comparison snapshot activation failed');}
      await privateJson(path.join(owner.directory,'cell.json'),cell);const result=await monitoredClient(cellKey(cell),cellKey(cell)+'.json');result.datasetFingerprint=variants[currentVariant].gatewayDatasets.find(d=>d.spec.scale===cell.scale).fingerprint;await evidence(path.join(options.output,currentVariant,cellKey(cell)+'.json'),result);variants[cell.variant].cells.push(result);return result;
    },onPair:receipt=>evidence(path.join(options.output,'gateway-pairs.json'),[...(status?.gatewayPairs??[]),receipt]).then(()=>{status??={gatewayPairs:[]};status.gatewayPairs.push(receipt);})});
    let sceneIndex=0;
    for(const scale of scales)for(const scene of ['ordinary','scope','reference']){
      await compose('stop',...appServices);await reset();const operation=scene==='ordinary'?'seed':scene+'-seed';
      const dataset=await hostOperation('baseline',operation,['--spec',`/private/spec-${scale}.json`,'--config','/private/host-controlplane.json','--directory','/private'],`${scene}-dataset-${scale}.json`);
      if(scene!=='ordinary')await privateJson(path.join(owner.directory,`${scene}-${scale}.json`),dataset);
      const before=await inspect('baseline',`${scene}-before-${scale}.json`),fixture=await captureDatabaseFixture({owner,postgresId,key:scene+'-'+scale,state:before,docker});
      const order=sceneIndex++%2===0?['baseline','candidate']:['candidate','baseline'],measured={},proof={scene,scale,receipt:fixture,before,variants:{}};
      for(const variant of order){
        await compose('stop',...appServices);await restoreDatabaseFixture({owner,postgresId,fixture,docker});
        const restored=await inspect('baseline',`${scene}-restored-${variant}-${scale}.json`);if(!verifyFixtureRestore(before,restored))throw Error('Comparison data/index restore mismatch');
        const upgrade=variant==='candidate'?await hostOperation('candidate','migration-upgrade',['--config','/private/host-controlplane.json'],`${scene}-upgrade-${scale}.json`):null;
        if(upgrade&&(!upgrade.complete||upgrade.before.businessFingerprint!==before.businessFingerprint||upgrade.after.businessFingerprint!==before.businessFingerprint))throw Error('Comparison index upgrade changed business data');
        proof.variants[variant]={restored,upgrade,complete:true};await analyze();await switchVariant(variant);
        const resultFile=(scene==='ordinary'?'management':scene)+`-queries-${scale}.json`;
        measured[variant]=await monitoredClient(scene+'-'+scale,resultFile,['node','scripts/performance/management-client.mjs',scene,String(scale),String(mutable),'false']);
        if(!measured[variant].complete||!measured[variant].classificationComplete)throw Error('Comparison management coverage/classification incomplete');
        variants[variant][scene==='ordinary'?'management':scene==='scope'?'scopeManagement':'referenceManagement'].push(measured[variant]);
        if(scene==='ordinary')for(const commandName of ['micro','generation']){
          const entry=await hostOperation(variant,commandName,['--snapshot',`/private/scales/${scale}/rs256.json`,'--environment',envId],`${variant}/${commandName}-${scale}.json`);
          variants[variant][commandName].push(commandName==='micro'?{scale,entries:entry}:{...entry,scale});
        }
      }
      const parity=verifyManagementParity(measured.baseline,measured.candidate);managementPairs.push({scene,scale,order,parity});fixtureProofs.push(proof);
      await evidence(path.join(options.output,'management-pairs.json'),managementPairs);await evidence(path.join(options.output,'fixture-restores.json'),fixtureProofs);if(!parity.complete)throw Error('Comparison management DTO/status parity mismatch: '+scene);
    }
    status={mode:options.mode,measurementScope:mutable?'comparison-protocol-selfcheck-not-optimization':'immutable-paired-comparison',immutable:!mutable,owner:owner.owner,identities,images,environment:environments.baseline.fingerprint,sqlClassificationManifestHash:classificationManifestHash,gatewayPairs:gateway.pairs,gatewayCells:gateway.cells.length,gatewayCellsComplete:gateway.complete,managementPairs,pairVerification:verifyPairReceipts({pairs:gateway.pairs,cells:gateway.cells,...mutable?{validation:true,validationSchedule:pairs}:{}}),fullBaselineComplete:false,fullComparisonComplete:false,optimizationAccepted:false,complete:false,diagnosticComplete:false};
    await evidence(path.join(options.output,'manifest.json'),status);succeeded=true;return status;
  }finally{
    const cleanup=await cleanupOwned(owner,composeFile);
    if(succeeded){
      const scan=await scanPublicEvidence(options.output,owner.directory);await evidence(path.join(options.output,'secret-scan.json'),scan);if(scan.violations)throw Error('Comparison public secret scan failed; private inputs retained');
      await removePrivateOwner(owner,cleanup);cleanup.privateInputsDeleted=true;
      status.variantVerification=Object.fromEntries(['baseline','candidate'].map(v=>[v,verifyBaselineCompletion({...variants[v],cleanup,secretScan:scan})]));
      status.diagnosticComplete=status.gatewayCellsComplete&&status.pairVerification.diagnosticComplete&&status.managementPairs.every(p=>p.parity.complete)&&fixtureProofs.every(p=>Object.values(p.variants).every(v=>v.complete));
      status.fullComparisonComplete=!mutable&&status.pairVerification.complete&&status.diagnosticComplete&&Object.values(status.variantVerification).every(v=>v.complete);status.complete=status.fullComparisonComplete;
      await evidence(path.join(options.output,'manifest.json'),status);
    }else cleanup.privateInputsRetainedForDiagnosis=true;
    await evidence(path.join(options.output,'cleanup.json'),cleanup);
  }
}
