import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {createOwner,cleanupOwned} from './owner.mjs';
import {exportInputs,exportValidationInputs,validateComparisonExperiment} from './sources.mjs';
import {buildComparisonPairs} from './matrix.mjs';
import {cellKey} from './report.mjs';
import {executeComparisonPairs,verifyManagementParity,verifyPairReceipts} from './comparison.mjs';
import {captureTemplateFixture,restoreTemplateFixture} from './template-fixture.mjs';
import {prepareIndexExperiment} from './index-experiment.mjs';
import {retainSqlAudit} from './audit.mjs';
import {captureDatabaseFixture,restoreDatabaseFixture,verifyFixtureRestore} from './database-fixture.mjs';
import {scanPublicEvidence,removePrivateOwner} from './secrets.mjs';
import {resourceId} from './management.mjs';
import {validateClassificationManifest} from './classification.mjs';
import {verifyEnvironment} from './environment.mjs';
import {verifyBaselineCompletion} from './completion.mjs';
import {runWithContainerResources} from './resources.mjs';
import {privateJson,evidence,docker,pickSubnet,assembleValidation,build,command,waitReady,runPrivateWorkload,activateGatewayFixture} from './runner-support.mjs';
const appServices=['controlplane','backend','gateway-a','gateway-b'];
export async function afterManagementPreflight({preflight,prepareManagement,measureGateway}){
  const proof=await prepareManagement();if(proof?.complete!==true)throw Error('Comparison management preflight incomplete');
  if(preflight==='management')return {pairs:[],cells:[],complete:false,managementOnly:true};
  return {...await measureGateway(),managementOnly:false};
}
export async function buildOrReuseComparisonImage({variant,identities,images,buildImage}){
 const a=identities.baseline,b=identities[variant];
 for(const identity of [a,b])if(!identity||!/^([a-f0-9]{40})$/.test(identity.source??'')||!/^([a-f0-9]{40})$/.test(identity.productTree??'')||!/^([a-f0-9]{40}|working-tree-validation)$/.test(identity.harness??'')||!/^([a-f0-9]{64})$/.test(identity.harnessFingerprint??''))throw Error('Incomplete comparison build identity');if(variant==='candidate'&&a&&['source','harness','harnessFingerprint','productTree'].every(key=>a[key]===b[key])){
  if(!/^sha256:[a-f0-9]{64}$/.test(images.baseline?.imageId??''))throw Error('Actual baseline image ID missing');return {...images.baseline,reusedFrom:'baseline'};
 }return buildImage();
}
export async function runComparison(options,repo){
  validateComparisonExperiment(options);
  const mutable=options.mode==='validate',preflight=options.preflight==='management',fixedProducts=!mutable||['baseline','candidate','harness'].some(k=>options[k]!==undefined);
  const combined=options.experiment==='current-business-combined-v1',sourceAudit=combined&&!!options['sql-audit'],sameSourceProtocol=combined&&options.baseline===options.candidate;
  const scales=mutable?[100]:[100,1000,10000];
  const pairs=mutable?['anonymous','api-key','missing-key','rs256','es256','wrong-secret','no-mapping','no-grant'].flatMap((auth,index)=>[1,2].map(repeat=>({pairId:`100:${auth}:1:${repeat}:dual`,cells:((index+repeat-1)%2===0?['baseline','candidate']:['candidate','baseline']).map(variant=>({variant,scale:100,auth,concurrency:1,repeat,nodeMode:'dual',validation:true}))}))):buildComparisonPairs();
  if(options.preflight&&!preflight)throw Error('Invalid comparison preflight');
  if(fixedProducts)for(const id of [options.baseline,options.candidate,options.harness]){
    try{if(!/^[a-f0-9]{40}$/.test(id??'')||(await command('git',['-C',repo,'rev-parse','--verify',id+'^{commit}'])).stdout.trim()!==id)throw Error();}
    catch{throw Error('Immutable comparison source commit missing');}
  }
  let combinedSourceProof;
  if(combined){
    const productTree=async source=>(await command('git',['-C',repo,'rev-parse',source+':src'])).stdout.trim();
    const candidateProductTree=await productTree(options.candidate),baselineProductTree=await productTree(options.baseline);
    const approved=['src/WebApi.Gateway/Configuration/RuntimeLookupIndex.cs','src/WebApi.Gateway/Configuration/RuntimeLinearLookupMap.cs',...['Catalog/CatalogService','Applications/ApplicationService','Policies/PolicyService','Releases/ReleaseService','Governance/GovernanceService','Gateway/GatewayReadService','Routing/ClusterService','Routing/RouteService','Alerts/AlertRuleService','Alerts/AlertEventService','Alerts/AlertListReader','Policies/PolicyReferenceReader'].map(p=>'src/WebApi.Infrastructure/'+p+'.cs')].sort();
    const files=(await command('git',['-C',repo,'diff','--name-only',options.candidate,options.baseline,'--','src'])).stdout.trim().split('\n').filter(Boolean).sort();
    if(options.candidate!=='760493da5a5bc0f0674e7e6398260c8a5ac4a6c0'||candidateProductTree!=='1aa393776ac219f43c7a9de48a63632dce1c82bf'||!sameSourceProtocol&&(JSON.stringify(files)!==JSON.stringify(approved)||baselineProductTree===candidateProductTree))throw Error('Combined product source difference is outside approved current-business baseline');
    const patch=(await command('git',['-C',repo,'diff',options.candidate,options.baseline,'--','src'])).stdout;
    combinedSourceProof={sourceDiffVerified:!sameSourceProtocol,sourceDiffSha256:createHash('sha256').update(patch).digest('hex'),sourceDiffFiles:files,baselineSource:options.baseline,candidateSource:options.candidate,baselineProductTree,candidateProductTree,harness:options.harness};
    if(sourceAudit){
      const auditRoot=options['sql-audit'];await fs.mkdir(auditRoot,{mode:0o700,recursive:true});const info=await fs.lstat(auditRoot);
      if(!info.isDirectory()||info.isSymbolicLink()||(info.mode&0o777)!==0o700||await fs.realpath(auditRoot)!==path.resolve(auditRoot))throw Error('Source audit root must be a private regular directory');
      const actualOutput=path.resolve(await fs.realpath(path.dirname(options.output)),path.basename(options.output));const relative=path.relative(auditRoot,actualOutput),reverse=path.relative(actualOutput,auditRoot);if((relative!== '..'&&!relative.startsWith('..'+path.sep)&&!path.isAbsolute(relative))||(reverse!== '..'&&!reverse.startsWith('..'+path.sep)&&!path.isAbsolute(reverse)))throw Error('Private audit and public output must not overlap');
    }
  }
  await fs.mkdir(options.output,{recursive:true});if((await fs.readdir(options.output)).length)throw Error('Comparison output directory must be empty');
  await fs.mkdir(path.join(repo,'.runtime','performance'),{recursive:true,mode:0o700});const owner=await createOwner(path.join(repo,'.runtime','performance'));
  const identities={},images={},variants={},environments={},managementPairs=[],fixtureProofs=[];let composeFile,status,succeeded=false;
  try{
    for(const variant of ['baseline','candidate']){
      const directory=path.join(owner.directory,'build-'+variant);await fs.mkdir(directory,{mode:0o700});const source=path.join(directory,'source');
      identities[variant]=mutable?(fixedProducts?await exportValidationInputs({repo,source:options[variant],harness:options.harness,directory:source}):await assembleValidation(repo,source)):await exportInputs({repo,source:options[variant],harness:options.harness,directory:source});
      const output=path.join(options.output,variant);await fs.mkdir(output);
      if(variant==='candidate'&&identities.baseline.harnessFingerprint!==identities.candidate.harnessFingerprint)throw Error('Comparison harness input mismatch');
      images[variant]=await buildOrReuseComparisonImage({variant,identities,images,buildImage:()=>build(owner,source,identities[variant],output)});
      variants[variant]={mode:'compare',variant,immutable:!mutable,identity:identities[variant],cells:[],gatewayDatasets:[],management:[],scopeManagement:[],referenceManagement:[],micro:[],generation:[]};
      if(variant==='baseline')composeFile=path.join(source,'deploy/compose.performance.yml');
    }
    const sourceDirectory=path.join(owner.directory,'build-baseline','source'),classificationPath=options.classifications??path.join(sourceDirectory,'scripts/performance/sql-classifications-baseline.json');
    let manifestJson;if(sourceAudit&&!options.classifications)manifestJson=JSON.stringify({schemaVersion:1,sourceCommits:[...new Set(Object.values(identities).map(i=>i.source))],rules:{}});
    else{const classificationInfo=await fs.lstat(classificationPath);if(!classificationInfo.isFile()||classificationInfo.isSymbolicLink()||classificationInfo.size>1024*1024)throw Error('Invalid SQL classification input');manifestJson=await fs.readFile(classificationPath,'utf8');}
    const classificationManifestHash=createHash('sha256').update(manifestJson).digest('hex');
    const classifications=JSON.parse(manifestJson);for(const variant of ['baseline','candidate'])validateClassificationManifest(classifications,identities[variant].source);
    await evidence(path.join(options.output,'sql-classification-manifest.json'),classifications);
    const environment={...process.env,PERF_OWNER:owner.owner,PERF_DIRECTORY:owner.directory,PERF_RESULT_DIRECTORY:options.output,PERF_HARNESS_DIRECTORY:sourceDirectory,PERF_IMAGE:images.baseline.imageId,PERF_SUBNET:await pickSubnet()};
    owner.composeEnvironment=Object.fromEntries(['PERF_IMAGE','PERF_SUBNET','PERF_RESULT_DIRECTORY','PERF_HARNESS_DIRECTORY'].map(k=>[k,environment[k]]));await privateJson(path.join(owner.directory,'owner.json'),owner);
    const compose=(...args)=>docker(['compose','-p',owner.project,'-f',composeFile,...args],{env:environment});
    const password=(await fs.readFile(path.join(owner.directory,'postgres-password'),'utf8')).trim(),seed=20261009,envId=resourceId(seed,'environment',0);
    if(sourceAudit)for(const variant of ['baseline','candidate'])await fs.mkdir(path.join(owner.directory,'sql-audit',variant),{mode:0o700,recursive:true});
    const common={urls:'http://0.0.0.0:8080','Performance:PrivateDirectory':'/private','Redis:Connection':'redis:6379,abortConnect=false','Observability:Enabled':'false','Upstream:AllowedOrigins:0':'http://backend:8080'};
    await privateJson(path.join(owner.directory,'host-backend.json'),common);
    await privateJson(path.join(owner.directory,'host-controlplane.json'),{...common,'ConnectionStrings:WebApi':`Host=postgres;Database=webapi_performance;Username=webapi;Password=${password};Maximum Pool Size=32;Timeout=30;Include Error Detail=false`,'Authentication:LoginProtection:HmacSecretFile':'/private/login-hmac','Authentication:LoginProtection:RedisPrefix':'performance-'+owner.owner,'DataProtection:KeysDirectory':'/private/data-protection','DataProtection:ApplicationName':owner.project});
    for(const name of ['gateway-a','gateway-b'])await privateJson(path.join(owner.directory,'host-'+name+'.json'),{...common,'Gateway:EnvironmentId':envId,'Gateway:NodeName':name,'Gateway:SecretFile':'/private/gateway-secret','Gateway:LkgDirectory':'/private/lkg-'+name,'Gateway:ControlPlaneUrl':'http://controlplane:8080','Gateway:AutomaticUpdates':'false'});
    const windowStart=new Date(Date.now()-60000).toISOString(),windowEnd=new Date(Date.now()+20*3600000).toISOString();
    for(const scale of scales)await privateJson(path.join(owner.directory,'spec-'+scale+'.json'),{scale,seed,windowStart,windowEnd});
    await compose('up','-d','--wait','postgres','redis');const postgresId=(await compose('ps','--quiet','postgres')).stdout.trim();
    const hostOperation=async(variant,operation,args,file)=>{
      environment.PERF_IMAGE=images[variant].imageId;environment.PERF_RESULT_DIRECTORY=options.output;
      await compose('run','--rm','--no-deps','controlplane','dotnet','WebApi.PerformanceHost.dll',operation,...args,'--output','/results/'+file);
      return JSON.parse(await fs.readFile(path.join(options.output,file),'utf8'));
    };
    const inspect=(variant,file)=>hostOperation(variant,'migration-inspect',['--config','/private/host-controlplane.json'],file);
    const sql=async(statement,target='postgres')=>(await compose('exec','-T','postgres','psql','-X','-A','-t','-U','webapi','-d',target,'-v','ON_ERROR_STOP=1','-c',statement)).stdout.trim();
    const jsonSql=async(statement,target)=>JSON.parse(await sql(statement,target));
const meta=async()=>{
  const database=await jsonSql(`SELECT jsonb_build_object('owner',pg_get_userbyid(d.datdba),'encoding',pg_encoding_to_char(d.encoding),'collate',d.datcollate,'ctype',d.datctype,'localeProvider',d.datlocprovider,'locale',to_jsonb(d)->>'datlocale','icuRules',to_jsonb(d)->>'daticurules','collationVersion',d.datcollversion,'tablespace',(SELECT spcname FROM pg_tablespace WHERE oid=d.dattablespace),'connectionLimit',d.datconnlimit,'allowConnections',d.datallowconn,'isTemplate',d.datistemplate,'acl',COALESCE((SELECT jsonb_agg(jsonb_build_object('grantor',pg_get_userbyid(a.grantor),'grantee',CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END,'privilege',a.privilege_type,'grantable',a.is_grantable) ORDER BY a.grantor,a.grantee,a.privilege_type,a.is_grantable) FROM aclexplode(COALESCE(d.datacl,acldefault('d',d.datdba))) a),'[]'::jsonb)) FROM pg_database d WHERE d.datname='webapi_performance'`);
  const settings=await jsonSql(`SELECT COALESCE(jsonb_agg(jsonb_build_object('role',CASE WHEN s.setrole=0 THEN 'ALL' ELSE pg_get_userbyid(s.setrole) END,'settings',s.setconfig) ORDER BY s.setrole),'[]'::jsonb) FROM pg_db_role_setting s JOIN pg_database d ON d.oid=s.setdatabase WHERE d.datname='webapi_performance'`);
  const effective=await jsonSql(`SELECT jsonb_agg(jsonb_build_object('name',name,'setting',setting,'unit',unit,'source',source) ORDER BY name) FROM pg_settings WHERE name IN ('server_version','server_encoding','client_encoding','TimeZone','work_mem','maintenance_work_mem','shared_buffers','max_connections','max_parallel_workers_per_gather','default_transaction_isolation','default_transaction_read_only','statement_timeout','lock_timeout','search_path','standard_conforming_strings')`,'webapi_performance');
  const objectPermissions=await jsonSql(`SELECT jsonb_build_object('schemas',(SELECT jsonb_agg(jsonb_build_object('name',nspname,'owner',pg_get_userbyid(nspowner),'acl',nspacl::text) ORDER BY nspname) FROM pg_namespace WHERE nspname='public'),'relations',(SELECT jsonb_agg(jsonb_build_object('name',c.relname,'kind',c.relkind,'owner',pg_get_userbyid(c.relowner),'acl',c.relacl::text) ORDER BY c.relname) FROM pg_class c JOIN pg_namespace n ON c.relnamespace=n.oid WHERE n.nspname='public'),'defaultPrivileges',(SELECT COALESCE(jsonb_agg(jsonb_build_object('role',pg_get_userbyid(defaclrole),'schema',defaclnamespace,'type',defaclobjtype,'acl',defaclacl::text) ORDER BY defaclrole,defaclnamespace,defaclobjtype),'[]'::jsonb) FROM pg_default_acl))`,'webapi_performance');
  const migrationRows=await jsonSql(`SELECT jsonb_agg(jsonb_build_object('migration',"MigrationId",'productVersion',"ProductVersion") ORDER BY "MigrationId") FROM "__EFMigrationsHistory"`,'webapi_performance');
  return {database,settings,effective,objectPermissions,migrationRows};
 };
    const currentState=async file=>({contract:await hostOperation('candidate','migration-current-contract',['--config','/private/host-controlplane.json'],file+'-contract.json'),inspection:await inspect('candidate',file+'-inspection.json'),metadata:await meta()});
    const reset=async()=>{await compose('exec','-T','postgres','dropdb','-U','webapi','--if-exists','webapi_performance');await compose('exec','-T','postgres','createdb','-U','webapi','webapi_performance');};
    const analyze=()=>compose('exec','-T','postgres','psql','-U','webapi','-d','webapi_performance','-v','ON_ERROR_STOP=1','-c','ANALYZE');
    for(const scale of scales){
      const dataset=await hostOperation('baseline','prepare',['--spec',`/private/spec-${scale}.json`,'--directory',`/private/scales/${scale}`],`gateway-${scale}.json`);
      for(const variant of ['baseline','candidate'])variants[variant].gatewayDatasets.push(dataset);
    }
    await hostOperation(combined?'candidate':'baseline','seed',['--spec','/private/spec-100.json','--config','/private/host-controlplane.json','--directory','/private'],'gateway-controlplane-dataset.json');
    const token=(await fs.readFile(path.join(owner.directory,'probe-token'),'utf8')).trim();
    const dockerHost=JSON.parse((await docker(['context','inspect','--format','{{json .Endpoints.docker.Host}}'])).stdout.trim()),apiVersion=(await docker(['version','--format','{{.Server.APIVersion}}'])).stdout.trim();
    const addresses={};let sequence=0,currentVariant;
    const switchVariant=async(variant)=>{
      environment.PERF_IMAGE=images[variant].imageId;environment.PERF_RESULT_DIRECTORY=path.join(options.output,variant);
      if(sourceAudit){const file=path.join(owner.directory,'host-controlplane.json'),config=JSON.parse(await fs.readFile(file,'utf8'));config['Performance:SqlAuditDirectory']='/private/sql-audit/'+variant;await privateJson(file,config);}
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
      const output=path.join(options.output,currentVariant),resources=await runWithContainerResources({manifest:owner,dockerHost,apiVersion,output,key,resultFile,holdClientUntilSampled:true},()=>runPrivateWorkload(owner,key,()=>compose('run','--rm','--no-deps','client',...args)));
      const result=JSON.parse(await fs.readFile(path.join(output,resultFile),'utf8'));Object.assign(result,{containerResources:resources,sourceCommit:identities[currentVariant].source,harnessFingerprint:identities[currentVariant].harnessFingerprint,environmentFingerprint:environments[currentVariant].fingerprint,imageId:images[currentVariant].imageId});
      await evidence(path.join(output,resultFile),result);if(!resources.complete)throw Error('Comparison container resource window incomplete');return result;
    };
    const prepareManagement=async()=>{
    let sceneIndex=0;
    for(const scale of scales)for(const scene of ['ordinary','scope','reference']){
      await compose('stop',...appServices);await reset();const operation=scene==='ordinary'?'seed':scene+'-seed';
      const dataset=await hostOperation(combined?'candidate':'baseline',operation,['--spec',`/private/spec-${scale}.json`,'--config','/private/host-controlplane.json','--directory','/private'],`${scene}-dataset-${scale}.json`);
      if(scene!=='ordinary')await privateJson(path.join(owner.directory,`${scene}-${scale}.json`),dataset);
      const before=combined?await currentState(`${scene}-before-${scale}`):await inspect('baseline',`${scene}-before-${scale}.json`);
      const fixture=combined?await captureTemplateFixture({owner,postgresId,key:scene+'-'+scale,sourceProduct:identities.candidate.source,readState:()=>currentState(`${scene}-capture-${scale}`),docker}):await captureDatabaseFixture({owner,postgresId,key:scene+'-'+scale,state:before,docker});
      const contract=combined?before.contract:await hostOperation('baseline','migration-legacy',['--config','/private/host-controlplane.json'],`${scene}-legacy-${scale}.json`);if(!combined)await privateJson(path.join(owner.directory,`${scene}-legacy-${scale}.json`),contract);
      const order=sceneIndex++%2===0?['baseline','candidate']:['candidate','baseline'],measured={},proof={scene,scale,receipt:fixture,before,variants:{}};
      for(const variant of order){
        await compose('stop',...appServices);
        if(combined){
          const readState=()=>currentState(`${scene}-${variant}-${scale}`),restored=await restoreTemplateFixture({owner,postgresId,fixture,readState,docker});
          const roleProof=await prepareIndexExperiment({owner,postgresId,role:sameSourceProtocol?'candidate':variant,reference:restored.state,readState,docker});
          proof.variants[variant]={target:{complete:true,checkedBy:'migration-current-contract',schemaHash:before.contract.schemaHash},restored:restored.restored,receipt:fixture,roleProof,complete:true};
        }else{
          await restoreDatabaseFixture({owner,postgresId,fixture,docker});const restored=await inspect('baseline',`${scene}-restored-${variant}-${scale}.json`);if(!verifyFixtureRestore(before,restored))throw Error('Comparison data/index restore mismatch');
          const upgrade=variant==='candidate'?await hostOperation('candidate','migration-compatible',['--config','/private/host-controlplane.json','--baseline-contract',`/private/${scene}-legacy-${scale}.json`],`${scene}-upgrade-${scale}.json`):null;
          if(upgrade&&(!upgrade.complete||upgrade.legacyBefore.fingerprint!==contract.fingerprint||upgrade.legacyAfter.fingerprint!==contract.fingerprint))throw Error('Comparison compatibility upgrade changed baseline data');proof.variants[variant]={restored,upgrade,complete:true};
        }
        await analyze();await switchVariant(variant);
        const resultFile=(scene==='ordinary'?'management':scene)+`-queries-${scale}.json`;
        measured[variant]=await monitoredClient(scene+'-'+scale,resultFile,['node','scripts/performance/management-client.mjs',scene,String(scale),String(mutable),'false']);
        if(!measured[variant].complete||!sourceAudit&&!measured[variant].classificationComplete)throw Error('Comparison management coverage/classification incomplete');
        variants[variant][scene==='ordinary'?'management':scene==='scope'?'scopeManagement':'referenceManagement'].push(measured[variant]);
        if(scene==='ordinary'&&!preflight)for(const commandName of ['micro','generation']){
          const entry=await hostOperation(variant,commandName,['--snapshot',`/private/scales/${scale}/rs256.json`,'--environment',envId],`${variant}/${commandName}-${scale}.json`);
          variants[variant][commandName].push(commandName==='micro'?{scale,entries:entry}:{...entry,scale});
        }
      }
      const parity=verifyManagementParity(measured.baseline,measured.candidate);managementPairs.push({scene,scale,order,parity});fixtureProofs.push(proof);
      await evidence(path.join(options.output,'management-pairs.json'),managementPairs);await evidence(path.join(options.output,'fixture-restores.json'),fixtureProofs);if(!parity.complete)throw Error('Comparison management DTO/status parity mismatch: '+scene);
    }
      return {complete:managementPairs.length===scales.length*3&&managementPairs.every(p=>p.parity.complete)&&fixtureProofs.every(p=>Object.values(p.variants).every(v=>v.complete))};
    };
    const measureGateway=async()=>{
      // Restore the same small baseline before each version switch. Candidate
      // ControlPlane must have its declared business schema before any timing.
      await compose('stop',...appServices);await reset();
      await hostOperation(combined?'candidate':'baseline','seed',['--spec','/private/spec-100.json','--config','/private/host-controlplane.json','--directory','/private'],'gateway-controlplane-prepared.json');
      const beforeGateway=combined?await currentState('gateway-schema-before'):await inspect('baseline','gateway-schema-before.json');
      const gatewayFixture=combined?await captureTemplateFixture({owner,postgresId,key:'gateway-controlplane-100',sourceProduct:identities.candidate.source,readState:()=>currentState('gateway-schema-capture'),docker}):await captureDatabaseFixture({owner,postgresId,key:'gateway-controlplane-100',state:beforeGateway,docker});
      const gatewayContract=combined?beforeGateway.contract:await hostOperation('baseline','migration-legacy',['--config','/private/host-controlplane.json'],'gateway-schema-legacy.json');if(!combined)await privateJson(path.join(owner.directory,'gateway-schema-legacy.json'),gatewayContract);
      let schemaVariant;
      const switchGateway=async variant=>{
        if(schemaVariant!==variant){
          await compose('stop',...appServices);let restored,upgrade=null;
          if(combined){const readState=()=>currentState('gateway-schema-'+variant),result=await restoreTemplateFixture({owner,postgresId,fixture:gatewayFixture,readState,docker});restored=result.state;const roleProof=await prepareIndexExperiment({owner,postgresId,role:variant,reference:restored,readState,docker});variants[variant].combinedGateway={target:{complete:true,checkedBy:'migration-current-contract',schemaHash:beforeGateway.contract.schemaHash},restored:true,receipt:gatewayFixture,roleProof};}
          else{await restoreDatabaseFixture({owner,postgresId,fixture:gatewayFixture,docker});restored=await inspect('baseline',`gateway-schema-restored-${variant}.json`);if(!verifyFixtureRestore(beforeGateway,restored))throw Error('Gateway schema fixture restore mismatch');upgrade=variant==='candidate'?await hostOperation('candidate','migration-compatible',['--config','/private/host-controlplane.json','--baseline-contract','/private/gateway-schema-legacy.json'],'gateway-schema-upgrade.json'):null;if(upgrade&&(!upgrade.complete||upgrade.legacyBefore.fingerprint!==gatewayContract.fingerprint||upgrade.legacyAfter.fingerprint!==gatewayContract.fingerprint))throw Error('Gateway compatibility schema changed baseline data');}
          await evidence(path.join(options.output,'gateway-schema-'+variant+'.json'),{variant,source:identities[variant].source,restored,upgrade,baselineContractFingerprint:gatewayContract.fingerprint,preparedOutsideTiming:true});await analyze();schemaVariant=variant;
        }
        await switchVariant(variant);
      };
    const gateway=await executeComparisonPairs({pairs,switchVariant:c=>switchGateway(c.variant),measure:async cell=>{
      const profile=['api-key','missing-key','wrong-secret'].includes(cell.auth)?'apikey':cell.auth==='no-mapping'?'rs256':cell.auth;
      await fs.copyFile(path.join(owner.directory,`scales/${cell.scale}/${profile}.json`),path.join(owner.directory,profile+'.json'));await fs.chmod(path.join(owner.directory,profile+'.json'),0o600);sequence++;
      for(const name of ['gateway-a','gateway-b'])await activateGatewayFixture({manifest:owner,output:path.join(options.output,currentVariant),url:addresses[name],node:name,token,profile,sequence});
      await privateJson(path.join(owner.directory,'cell.json'),cell);const result=await monitoredClient(cellKey(cell),cellKey(cell)+'.json');result.datasetFingerprint=variants[currentVariant].gatewayDatasets.find(d=>d.spec.scale===cell.scale).fingerprint;await evidence(path.join(options.output,currentVariant,cellKey(cell)+'.json'),result);variants[cell.variant].cells.push(result);return result;
    },onPair:receipt=>evidence(path.join(options.output,'gateway-pairs.json'),[...(status?.gatewayPairs??[]),receipt]).then(()=>{status??={gatewayPairs:[]};status.gatewayPairs.push(receipt);})});
      return gateway;
    };
    const gateway=await afterManagementPreflight({preflight:options.preflight,prepareManagement,measureGateway});
    status={mode:options.mode,measurementScope:preflight?'shared-fixture-management-preflight-not-performance':mutable?'comparison-protocol-selfcheck-not-optimization':'immutable-paired-comparison',immutable:!mutable,owner:owner.owner,identities,images,environment:environments.baseline.fingerprint,sqlClassificationManifestHash:classificationManifestHash,gatewayPairs:gateway.pairs,gatewayCells:gateway.cells.length,gatewayCellsComplete:!preflight&&gateway.complete,managementOnly:preflight,managementPreflightComplete:false,managementPairs,pairVerification:preflight?{complete:false,diagnosticComplete:false,missing:['management-only-preflight']}:verifyPairReceipts({pairs:gateway.pairs,cells:gateway.cells,...mutable?{validation:true,validationSchedule:pairs}:{}}),fullBaselineComplete:false,fullComparisonComplete:false,optimizationAccepted:false,complete:false,diagnosticComplete:false};
    if(combined){status.experiment=options.experiment;status.formalQualified=false;status.diagnosticPurpose=sameSourceProtocol?'same-source-protocol-check':sourceAudit?'source-audit':'current-business-combined';status.sourceAuditComplete=false;status.sourceDifference=combinedSourceProof;
      for(const variant of ['baseline','candidate']){variants[variant].experiment=options.experiment;variants[variant].combinedProof={...combinedSourceProof,harness:identities[variant].harness,harnessFingerprint:identities[variant].harnessFingerprint,fixtures:fixtureProofs.map(p=>({scene:p.scene,scale:p.scale,...p.variants[variant]})),gateway:variants[variant].combinedGateway};}
      if(sourceAudit){status.sqlAudit={};for(const variant of ['baseline','candidate'])status.sqlAudit[variant]=await retainSqlAudit(path.join(owner.directory,'sql-audit',variant),path.join(options['sql-audit'],owner.owner+'-'+variant));status.sourceAuditComplete=Object.values(status.sqlAudit).every(p=>p.complete);}
    }
    await evidence(path.join(options.output,'manifest.json'),status);succeeded=true;return status;
  }finally{
    const cleanup=await cleanupOwned(owner,composeFile);
    if(succeeded){
      const scan=await scanPublicEvidence(options.output,owner.directory);await evidence(path.join(options.output,'secret-scan.json'),scan);if(scan.violations)throw Error('Comparison public secret scan failed; private inputs retained');
      await removePrivateOwner(owner,cleanup);cleanup.privateInputsDeleted=true;
      status.variantVerification=Object.fromEntries(['baseline','candidate'].map(v=>[v,verifyBaselineCompletion({...variants[v],cleanup,secretScan:scan})]));
      status.managementPreflightComplete=!sourceAudit&&status.managementPairs.length===scales.length*3&&status.managementPairs.every(p=>p.parity.complete)&&fixtureProofs.every(p=>Object.values(p.variants).every(v=>v.complete));
      status.diagnosticComplete=(sourceAudit?status.sourceAuditComplete:status.managementPreflightComplete)&&(preflight||status.gatewayCellsComplete&&status.pairVerification.diagnosticComplete);
      status.fullComparisonComplete=!sourceAudit&&!sameSourceProtocol&&!mutable&&!preflight&&status.pairVerification.complete&&status.diagnosticComplete&&Object.values(status.variantVerification).every(v=>v.complete);status.complete=status.fullComparisonComplete;
      await evidence(path.join(options.output,'manifest.json'),status);
    }else cleanup.privateInputsRetainedForDiagnosis=true;
    await evidence(path.join(options.output,'cleanup.json'),cleanup);
  }
}
