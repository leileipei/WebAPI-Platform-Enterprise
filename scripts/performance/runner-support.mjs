import fs from "node:fs/promises";
import path from "node:path";
import {execFile} from "node:child_process";
import {promisify} from "node:util";
import {createHash} from "node:crypto";
import {hashHarnessFiles} from "./sources.mjs";
import {validateOwner} from "./owner.mjs";
import {quotas} from "./environment.mjs";
const exec=promisify(execFile);
const sdk='mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317';
export async function privateJson(file,value){await fs.writeFile(file,JSON.stringify(value,null,2)+'\n',{mode:0o600});await fs.chmod(file,0o600);}
export async function evidence(file,value){await fs.writeFile(file,JSON.stringify(value,null,2)+'\n');}
export async function command(name,args,options={}){return exec(name,args,{maxBuffer:16*1024*1024,...options});}
export async function docker(args,options={}){return command('docker',args,options);}
export async function pickSubnet(){
  const ids=(await docker(['network','ls','-q'])).stdout.trim().split(/\s+/).filter(Boolean);
  const networks=ids.length?JSON.parse((await docker(['network','inspect',...ids])).stdout):[];
  const ranges=networks.flatMap(n=>n.IPAM.Config??[]).map(c=>c.Subnet).filter(s=>s&&!s.includes(':'));
  const number=ip=>ip.split('.').reduce((value,part)=>value*256+Number(part),0);
  const bounds=cidr=>{const [ip,bits]=cidr.split('/'),size=2**(32-Number(bits));const lower=Math.floor(number(ip)/size)*size;return [lower,lower+size-1];};
  for(let i=1;i<255;i++){
    const cidr=`10.251.${i}.0/24`,[lower,upper]=bounds(cidr);
    if(!ranges.some(range=>{const [a,b]=bounds(range);return lower<=b&&a<=upper;}))return cidr;
  }
  throw Error('No isolated performance subnet available; no resources were removed');
}
export async function assembleValidation(repo,directory){
  const revision=(await command('git',['-C',repo,'rev-parse','HEAD'])).stdout.trim();
  if((await command('git',['-C',repo,'status','--porcelain','--','src'])).stdout.trim())throw Error('Validation product source must match HEAD');
  await fs.mkdir(directory,{mode:0o700});const archive=path.join(path.dirname(directory),'validation-source.tar');
  try {await command('git',['-C',repo,'archive','--output',archive,revision]);await command('tar',['-xf',archive,'-C',directory]);}
  finally{await fs.rm(archive,{force:true});}
  const roots=['tools/WebApi.PerformanceHost','scripts/performance','scripts/check-gateway-query-performance.sh','deploy/compose.performance.yml','deploy/performance'];
  for(const file of roots)await fs.cp(path.join(repo,file),path.join(directory,file),{recursive:true,filter:source=>!path.relative(repo,source).split(path.sep).some(segment=>['bin','obj','node_modules'].includes(segment))});
  const files=[];
  async function walk(relative){const info=await fs.lstat(path.join(directory,relative));if(info.isSymbolicLink())throw Error('Harness input must not be a symbolic link');if(info.isDirectory())for(const name of await fs.readdir(path.join(directory,relative)))await walk(path.join(relative,name));else files.push(relative);}
  for(const root of roots)await walk(root);
  return {source:revision,harness:'working-tree-validation',productTree:(await command('git',['-C',repo,'rev-parse',revision+':src'])).stdout.trim(),immutable:false,...await hashHarnessFiles(directory,files)};
}
export async function build(manifest,directory,identity,resultDirectory,run=docker){
  const project=manifest.project,context=path.dirname(directory),publish=path.join(context,'publish');await fs.mkdir(publish);
  const seed='webapi-enterprise-core-test_nuget-test';await run(['volume','inspect',seed]);
  const script=['set -eu','export DOTNET_CLI_TELEMETRY_OPTOUT=1 NUGET_PACKAGES=/build/.nuget','mkdir -p /build/.nuget','cp -a /nuget-seed/. /build/.nuget/',
    'dotnet restore tools/WebApi.PerformanceHost/WebApi.PerformanceHost.csproj --locked-mode',
    'dotnet publish tools/WebApi.PerformanceHost/WebApi.PerformanceHost.csproj --configuration Release --no-restore --output /build/publish -p:SourceRevisionId='+identity.source].join('\n');
  let built;
  try {built=await run(['run','--rm','--cpus','4','--memory','4g','--label','com.docker.compose.project='+project,'--label','com.webapi.performance.owner='+manifest.owner,'--workdir','/build/source','--mount',`type=bind,src=${context},dst=/build`,'--mount',`type=volume,src=${seed},dst=/nuget-seed,readonly`,sdk,'bash','-c',script]);}
  catch(error){await fs.writeFile(path.join(resultDirectory,'build.log'),(error.stdout??'')+(error.stderr??''));throw Error('Fixture build failed; see build.log');}
  await fs.writeFile(path.join(resultDirectory,'build.log'),built.stdout+built.stderr);
  await fs.copyFile(path.join(directory,'deploy/performance/Dockerfile'),path.join(context,'Dockerfile'));
  const tag=`webapi-performance:${manifest.owner}-${identity.source}-${identity.harness}`;
  await run(['build','--build-arg','SDK_IMAGE='+sdk,'--build-arg','SOURCE_REVISION='+identity.source,'--build-arg','HARNESS_REVISION='+identity.harness,'--tag',tag,context]);
  const image=JSON.parse((await run(['image','inspect',tag])).stdout)[0];
  if(image.Config.Labels['org.opencontainers.image.revision']!==identity.source||image.Config.Labels['com.webapi.performance.harness']!==identity.harness)throw Error('Image source identity mismatch');
  const hashes={};for(const file of await fs.readdir(publish))if(file.endsWith('.dll'))hashes[file]=createHash('sha256').update(await fs.readFile(path.join(publish,file))).digest('hex');
  return {imageId:image.Id,sdk,runtimeBase:sdk,fixtureUid:image.Config.User,assemblies:hashes};
}
export async function waitReady(base,pathName='/health/live'){
  for(let i=0;i<120;i++){
    try {const response=await fetch(base+pathName,{signal:AbortSignal.timeout(1000)});if(response.ok)return;}catch{}
    await new Promise(resolve=>setTimeout(resolve,250));
  }
  throw Error('Isolated host did not become ready');
}

export async function runPrivateWorkload(manifest,key,operation){
  if(!/^[a-zA-Z0-9:-]+$/.test(key??''))throw Error('Invalid private workload key');
  try{return await operation();}
  catch(error){
    await privateJson(path.join(manifest.directory,key+'.workload-failure.json'),{schemaVersion:1,recordedAt:new Date().toISOString(),exitCode:Number.isInteger(error.code)?error.code:null,signal:typeof error.signal==='string'?error.signal:null,stdout:error.stdout??null,stderr:error.stderr??null});
    throw error;
  }
}

export async function retainOwnerFailureDiagnostics(manifest,composeFile,environment,run=docker){
  const ids=(await run(['compose','-p',manifest.project,'-f',composeFile,'ps','--all','--quiet'],{env:environment})).stdout.trim().split(/\s+/).filter(Boolean);
  if(ids.some(id=>!/^[a-f0-9]{64}$/.test(id)))throw Error('Invalid failure container identity');
  const format='{"id":{{json .Id}},"labels":{{json .Config.Labels}},"running":{{json .State.Running}},"exitCode":{{json .State.ExitCode}},"oomKilled":{{json .State.OOMKilled}},"restartCount":{{json .RestartCount}}}';
  const inspected=ids.length?(await run(['inspect','--format',format,...ids])).stdout.trim().split('\n').map(line=>JSON.parse(line)):[];
  if(inspected.length!==ids.length||new Set(inspected.map(x=>x.id)).size!==ids.length||inspected.some(x=>!ids.includes(x.id)||!validateOwner(manifest,x.labels)||!quotas[x.labels?.['com.docker.compose.service']]||typeof x.running!=='boolean'||typeof x.oomKilled!=='boolean'||!Number.isInteger(x.exitCode)||!Number.isSafeInteger(x.restartCount)||x.restartCount<0))throw Error('Failure diagnostic owner or state mismatch');
  // Validate every identity before reading any log. Log bodies can contain
  // credentials or request details, so retain them only in this owner-private directory.
  for(const info of inspected){
    const log=await run(['logs','--tail','100',info.id]);const file=path.join(manifest.directory,'failure-'+info.labels['com.docker.compose.service']+'-'+info.id+'.log');
    await fs.writeFile(file,log.stdout+log.stderr,{mode:0o600});await fs.chmod(file,0o600);
  }
  return {owner:manifest.owner,collected:true,containers:inspected.map(x=>({service:x.labels['com.docker.compose.service'],running:x.running,exitCode:x.exitCode,oomKilled:x.oomKilled,restartCount:x.restartCount})),privateLogsRetained:true};
}

export async function activateGatewayFixture({manifest,output,url,node,token,profile,sequence}){
  if(!validateOwner(manifest,{'com.docker.compose.project':manifest.project,'com.webapi.performance.owner':manifest.owner})||!['gateway-a','gateway-b'].includes(node)||!['anonymous','apikey','rs256','es256','no-grant'].includes(profile)||!Number.isSafeInteger(sequence)||sequence<=0)throw Error('Invalid private activation operation');
  const response=await fetch(url+'/__perf/apply',{method:'POST',headers:{'Content-Type':'application/json','X-Performance-Token':token},body:JSON.stringify({profile,sequence}),signal:AbortSignal.timeout(120000)});
  if(response.ok)return;
  const body=await response.text();await privateJson(path.join(manifest.directory,'activation-response-'+node+'-'+sequence+'.json'),{status:response.status,body});
  let value;try{value=JSON.parse(body);}catch{}
  const stages=['request-parse','file-check','file-read','snapshot-parse','activate'];
  const types=['JsonException','IOException','FileNotFoundException','DirectoryNotFoundException','InvalidDataException','ApiException','ArgumentException','ArgumentNullException','ArgumentOutOfRangeException','PathTooLongException'];
  const selected={owner:manifest.owner,node,profile,sequence,status:response.status,stage:stages.includes(value?.stage)?value.stage:null,errorType:types.includes(value?.errorType)?value.errorType:null,diagnosticRetained:value?.diagnosticRetained===true};
  await evidence(path.join(output,'gateway-activation-failure.json'),selected);
  throw Error('Real gateway activation failed: '+response.status+(selected.stage?' at '+selected.stage:''));
}
