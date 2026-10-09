import fs from 'node:fs/promises';
import path from 'node:path';
import {createOwner,cleanupOwned} from './owner.mjs';
import {exportInputs} from './sources.mjs';
import {scanPublicEvidence,removePrivateOwner} from './secrets.mjs';
import {verifyMigrationCompletion} from './migration.mjs';
import {captureDatabaseFixture,restoreDatabaseFixture,verifyFixtureRestore} from './database-fixture.mjs';
import {privateJson,evidence,docker,pickSubnet,assembleValidation,build,command} from './runner-support.mjs';
export async function runMigration(options,repo){
  const mutable=options.mode==='validate';
  if(!mutable)for(const id of [options.baseline,options.candidate,options.harness])if(!/^[a-f0-9]{40}$/.test(id??'')||(await command('git',['-C',repo,'rev-parse','--verify',id+'^{commit}'])).stdout.trim()!==id)throw Error('Immutable migration source commit missing');
  await fs.mkdir(options.output,{recursive:true});if((await fs.readdir(options.output)).length)throw Error('Migration output directory must be empty');
  await fs.mkdir(path.join(repo,'.runtime','performance'),{recursive:true,mode:0o700});
  const owner=await createOwner(path.join(repo,'.runtime','performance'));const identities={},images={};let composeFile,status,succeeded=false;
  try{
    for(const variant of ['baseline','candidate']){
      const directory=path.join(owner.directory,'build-'+variant);await fs.mkdir(directory,{mode:0o700});const source=path.join(directory,'source');
      identities[variant]=mutable?await assembleValidation(repo,source):await exportInputs({repo,source:options[variant],harness:options.harness,directory:source});
      const result=path.join(options.output,variant);await fs.mkdir(result);
      images[variant]=mutable&&variant==='candidate'?images.baseline:await build(owner,source,identities[variant],result);
      if(variant==='baseline')composeFile=path.join(source,'deploy/compose.performance.yml');
    }
    const environment={...process.env,PERF_OWNER:owner.owner,PERF_DIRECTORY:owner.directory,PERF_RESULT_DIRECTORY:options.output,PERF_HARNESS_DIRECTORY:path.join(owner.directory,'build-baseline','source'),PERF_IMAGE:images.baseline.imageId,PERF_SUBNET:await pickSubnet()};
    owner.composeEnvironment=Object.fromEntries(['PERF_IMAGE','PERF_SUBNET','PERF_RESULT_DIRECTORY','PERF_HARNESS_DIRECTORY'].map(k=>[k,environment[k]]));await privateJson(path.join(owner.directory,'owner.json'),owner);
    const compose=(...args)=>docker(['compose','-p',owner.project,'-f',composeFile,...args],{env:environment});
    const password=(await fs.readFile(path.join(owner.directory,'postgres-password'),'utf8')).trim();
    await privateJson(path.join(owner.directory,'host-controlplane.json'),{'ConnectionStrings:WebApi':`Host=postgres;Database=webapi_performance;Username=webapi;Password=${password};Maximum Pool Size=32;Include Error Detail=false`});
    await privateJson(path.join(owner.directory,'spec-100.json'),{scale:100,seed:20261009,windowStart:new Date(Date.now()-60000).toISOString(),windowEnd:new Date(Date.now()+20*3600000).toISOString()});
    await compose('up','-d','--wait','postgres','redis');
    const run=async(variant,operation,file)=>{
      environment.PERF_IMAGE=images[variant].imageId;
      await compose('run','--rm','--no-deps','controlplane','dotnet','WebApi.PerformanceHost.dll',operation,'--config','/private/host-controlplane.json','--output','/results/'+file);
      return JSON.parse(await fs.readFile(path.join(options.output,file),'utf8'));
    };
    const reset=async()=>{await compose('exec','-T','postgres','dropdb','-U','webapi','--if-exists','webapi_performance');await compose('exec','-T','postgres','createdb','-U','webapi','webapi_performance');};
    const seedBaseline=async()=>{environment.PERF_IMAGE=images.baseline.imageId;await compose('run','--rm','--no-deps','controlplane','dotnet','WebApi.PerformanceHost.dll','seed','--spec','/private/spec-100.json','--config','/private/host-controlplane.json','--directory','/private','--output','/results/baseline/seed.json');};
    const empty=await run('candidate','migration-install','candidate/empty.json');
    await reset();await seedBaseline();const baselineState=await run('baseline','migration-inspect','baseline/before-upgrade.json');
    const postgresId=(await compose('ps','--quiet','postgres')).stdout.trim();
    const fixture=await captureDatabaseFixture({owner,postgresId,key:'ordinary-100',state:baselineState,docker});
    await compose('exec','-T','postgres','psql','-U','webapi','-d','webapi_performance','-v','ON_ERROR_STOP=1','-c',"UPDATE organizations SET name=name || '-fixture-restore-canary' WHERE id=(SELECT id FROM organizations ORDER BY id LIMIT 1)");
    const changed=await run('baseline','migration-inspect','baseline/mutated-before-restore.json');
    await restoreDatabaseFixture({owner,postgresId,fixture,docker});
    const restored=await run('baseline','migration-inspect','baseline/restored.json');
    const fixtureRestore={receipt:fixture,before:baselineState,after:restored,mutationObserved:baselineState.businessFingerprint!==changed.businessFingerprint,complete:verifyFixtureRestore(baselineState,restored)};
    if(!fixtureRestore.complete||!fixtureRestore.mutationObserved)throw Error('Actual fixture restoration did not preserve baseline data');
    const upgrade=await run('candidate','migration-upgrade','candidate/upgrade.json');
    await restoreDatabaseFixture({owner,postgresId,fixture,docker});const failure=await run('candidate','migration-failure','candidate/failure.json');
    status={mode:options.mode,measurementScope:'migration-diagnostic-not-performance',immutable:!mutable,owner:owner.owner,identities,images,baselineState,empty,upgrade,failure,fixtureRestore,fullBaselineComplete:false,complete:false,diagnosticComplete:false};
    await evidence(path.join(options.output,'manifest.json'),status);succeeded=true;return status;
  }finally{
    const cleanup=await cleanupOwned(owner,composeFile);
    if(succeeded){
      const scan=await scanPublicEvidence(options.output,owner.directory);await evidence(path.join(options.output,'secret-scan.json'),scan);
      if(scan.violations)throw Error('Migration public secret scan failed; private inputs retained');
      await removePrivateOwner(owner,cleanup);cleanup.privateInputsDeleted=true;
      status.verification=verifyMigrationCompletion({...status,cleanup,secretScan:scan});status.complete=status.verification.complete;status.diagnosticComplete=status.verification.diagnosticComplete;
      await evidence(path.join(options.output,'manifest.json'),status);
    }else cleanup.privateInputsRetainedForDiagnosis=true;
    await evidence(path.join(options.output,'cleanup.json'),cleanup);
  }
}
