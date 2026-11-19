import {createHash,randomBytes} from 'node:crypto';
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import {isLocalDockerEndpointBound,restoreVolumeIdentity,withLocalDockerEndpoint} from './docker.mjs';
import {validateFixture} from './fixture-guard.mjs';
import {hasRestoreQuotaCapacity,validateRestoreStorageBudget} from './restore-storage-budget.mjs';

const verifyPath='/v1/restore-storage/verify';
const maxEvidenceBytes=256*1024;
const hash=value=>createHash('sha256').update(value).digest('hex');
const isHash=value=>typeof value==='string'&&/^[a-f0-9]{64}$/.test(value);
const isUuid=value=>typeof value==='string'&&/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(value);
const isPositiveDecimal=value=>typeof value==='string'&&/^[1-9][0-9]{0,19}$/.test(value);
const isObject=value=>value&&typeof value==='object'&&!Array.isArray(value);
const hasFields=(value,fields)=>isObject(value)&&Object.keys(value).length===fields.length&&fields.every(key=>Object.hasOwn(value,key));
const isCanonicalPath=value=>typeof value==='string'&&value.startsWith('/')&&path.posix.normalize(value)===value&&
 !value.startsWith('//')&&(value==='/'||!value.endsWith('/'))&&!/[\u0000\r\n]/.test(value);
const within=(child,parent)=>child!==parent&&child.startsWith(parent==='/'?parent:parent+'/');

async function pinSocket(file,kind,readStat){
 const unsafe=()=>Error('Restore '+kind+' socket must be a canonical root-controlled local Unix socket.');
 if(!isCanonicalPath(file)||file==='/'||Buffer.byteLength(file)>107||!/^\/[a-zA-Z0-9/_.-]+$/.test(file))throw unsafe();
 const parents=[];
 for(let directory=path.posix.dirname(file);directory!=='/';directory=path.posix.dirname(directory))parents.unshift(directory);
 parents.unshift('/');

 const inspect=async()=>{
  try{
   const identities=[];
   for(const directory of parents){
    const stat=await readStat(directory,{bigint:true});
    if(!stat.isDirectory()||stat.uid!==0n||(stat.mode&0o022n)!==0n)throw unsafe();
    identities.push([String(stat.dev),String(stat.ino)]);
   }
   const stat=await readStat(file,{bigint:true});
   if(!stat.isSocket()||stat.uid!==0n||![0o600n,0o660n].includes(stat.mode&0o777n))throw unsafe();
   const identity={path:file,device:String(stat.dev),inode:String(stat.ino)};
   return {identity,stamp:JSON.stringify([identities,identity,String(stat.ctimeNs),String(stat.gid),String(stat.mode)])};
  }catch{throw unsafe();}
 };
 const initial=await inspect();
 return {
  kind,path:file,identity:Object.freeze(initial.identity),
  async assertCurrent(){
   if((await inspect()).stamp!==initial.stamp)throw Error('Restore '+kind+' socket identity changed.');
  }
 };
}

async function requestJson(endpoint,method,requestPath,body){
 await endpoint.assertCurrent();
 const unavailable=()=>Error('Restore '+endpoint.kind+' evidence is unavailable.');
 let bytes;
 try{
  bytes=await new Promise((resolve,reject)=>{
   const request=http.request({
    socketPath:endpoint.path,method,path:requestPath,agent:false,
    signal:AbortSignal.timeout(10000),
    ...(body===undefined?{}:{headers:{'content-type':'application/json','content-length':Buffer.byteLength(body)}})
   },response=>{
    response.on('error',reject);
    if(response.statusCode!==200||!/^application\/json(?:\s*;|$)/i.test(response.headers['content-type']??'')){
     response.destroy(unavailable());
     return;
    }
    const chunks=[];
    let length=0;
    response.on('data',chunk=>{
     length+=chunk.length;
     if(length>maxEvidenceBytes)response.destroy(unavailable());
     else chunks.push(chunk);
    });
    response.on('end',()=>resolve(Buffer.concat(chunks,length)));
   });
   request.on('error',reject);
   request.end(body);
  });
 }catch{throw unavailable();}
 await endpoint.assertCurrent();
 try{return {value:JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(bytes)),digest:hash(bytes)};}
 catch{throw unavailable();}
}

async function readDockerDaemon(endpoint){
 const {value:version}=await requestJson(endpoint,'GET','/version');
 if(!isObject(version)||typeof version.ApiVersion!=='string'||!/^1\.[0-9]{1,3}$/.test(version.ApiVersion))
  throw Error('Restore Docker daemon API version is unverified.');
 const {value:info}=await requestJson(endpoint,'GET','/v'+version.ApiVersion+'/info');
 if(!isObject(info)||info.OSType!=='linux'||typeof info.ID!=='string'||info.ID.length===0||
    typeof info.ServerVersion!=='string'||!info.ServerVersion||info.ServerVersion!==version.Version||
    !isCanonicalPath(info.DockerRootDir)||info.DockerRootDir==='/'||
    !Array.isArray(info.SecurityOptions)||info.SecurityOptions.some(option=>typeof option!=='string'||option.includes('rootless')))
  throw Error('Restore Docker daemon identity is unverified.');
 return {id:info.ID,version:info.ServerVersion,apiVersion:version.ApiVersion,rootDirectory:info.DockerRootDir};
}

function verifyDaemonWitness(witness,socket,daemon,bootId){
 if(!hasFields(witness,['socket','bootId','pid','startTicks','mountNamespace','id','version','apiVersion','rootDirectory'])||
    !hasFields(witness.socket,['path','device','inode'])||Object.keys(socket).some(key=>witness.socket[key]!==socket[key])||
    witness.bootId!==bootId||!Number.isSafeInteger(witness.pid)||witness.pid<=0||
    !isPositiveDecimal(witness.startTicks)||!isPositiveDecimal(witness.mountNamespace)||
    Object.keys(daemon).some(key=>witness[key]!==daemon[key]))
  throw Error('Restore storage evidence does not identify the pinned Docker daemon.');
 return [socket,daemon,bootId,witness.pid,witness.startTicks,witness.mountNamespace];
}

function verifyQuotaWitness(quota,capacity,daemon,budget){
 if(!hasFields(quota,['kind','filesystem','filesystemUuid','device','mountId','mountPoint','volumeRoot','volumeRootInode','volumeRootHandle',
     'projectId','projectInheritance','accounting','enforcement','hardLimitBytes'])||
    quota.kind!=='filesystem-project-hard-quota'||!['xfs','ext4'].includes(quota.filesystem)||
    !isUuid(quota.filesystemUuid)||typeof quota.device!=='string'||!/^[0-9]+:[0-9]+$/.test(quota.device)||!isPositiveDecimal(quota.mountId)||
    !isCanonicalPath(quota.mountPoint)||quota.volumeRoot!==path.posix.join(daemon.rootDirectory,'volumes')||
    !within(quota.volumeRoot,quota.mountPoint)||!isPositiveDecimal(quota.volumeRootInode)||!isHash(quota.volumeRootHandle)||
    !Number.isSafeInteger(quota.projectId)||quota.projectId<=0||quota.projectInheritance!==true||quota.accounting!==true||quota.enforcement!==true)
  throw Error('Restore backing mount and enforced project hard quota are unverified.');
 if(!hasFields(capacity,['path','device','mountId','projectId','availableBytes'])||
    !isCanonicalPath(capacity.path)||!(capacity.path===quota.mountPoint||within(capacity.path,quota.mountPoint))||
    capacity.path===quota.volumeRoot||within(capacity.path,quota.volumeRoot)||
    capacity.device!==quota.device||capacity.mountId!==quota.mountId||capacity.projectId!==0||
    !hasRestoreQuotaCapacity({quotaBytes:quota.hardLimitBytes,availableBytes:capacity.availableBytes},budget))
  throw Error('Restore hard quota exceeds the budget or its backing capacity is unverified.');
 return Object.entries(quota).sort();
}

async function verifyVolumeWitnesses(endpoint,daemon,quota,volumes,scope){
 if(!Array.isArray(volumes)||volumes.length!==(scope.ownerId?scope.volumeNames.length:0))
  throw Error('Restore writable volume evidence is incomplete.');
 if(!scope.ownerId)return [];
 const byName=new Map(volumes.map(volume=>[volume?.name,volume]));
 const checked=await Promise.all(scope.volumeNames.map(async name=>{
  const witness=byName.get(name);
  const wrongVolume=()=>Error('Restore writable volume identity or hard quota mapping is unverified.');
  if(!hasFields(witness,['name','identity','mountpoint','inode','handle','mountId','device','projectId','projectInheritance'])||
     !isHash(witness.identity)||witness.identity!==scope.volumeIdentities[name]||
     !isCanonicalPath(witness.mountpoint)||!within(witness.mountpoint,quota.volumeRoot)||
     !isPositiveDecimal(witness.inode)||!isHash(witness.handle)||
     witness.mountId!==quota.mountId||witness.device!==quota.device||
     witness.projectId!==quota.projectId||witness.projectInheritance!==true)throw wrongVolume();
  const fullName=scope.projectName+'_'+name;
  const {value:actual}=await requestJson(endpoint,'GET','/v'+daemon.apiVersion+'/volumes/'+encodeURIComponent(fullName));
  if(!isObject(actual)||actual.Name!==fullName||actual.Driver!=='local'||actual.Scope!=='local'||
     (actual.Options!=null&&(!isObject(actual.Options)||Object.keys(actual.Options).length!==0))||
     actual.Mountpoint!==witness.mountpoint||actual.Labels?.['com.docker.compose.project']!==scope.projectName||
     actual.Labels?.['com.docker.compose.volume']!==name||actual.Labels?.['com.webapi.runtime.owner']!==scope.ownerId||
     restoreVolumeIdentity(actual)!==witness.identity)throw wrongVolume();
  return witness;
 }));
 for(const key of ['mountpoint','inode','handle']){
  if(new Set(checked.map(volume=>volume[key])).size!==checked.length)throw Error('Restore writable volume mapping is aliased.');
 }
 return checked.map(({name,identity,mountpoint,inode,handle})=>[name,identity,mountpoint,inode,handle]);
}

const osBootId=async()=> (await fs.readFile('/proc/sys/kernel/random/boot_id','utf8')).trim();

// The service is deployed under separate administrative control. Its observations
// must come from the live daemon and filesystem described in the runbook.
export async function configureRestoreStorage({restoreBudget,quotaSocket,dockerSocket},{
 platform=process.platform,readStat=fs.lstat,readBootId=osBootId
}={}){
 const budget=validateRestoreStorageBudget(restoreBudget);
 if(platform!=='linux')throw Error('Restore hard quota verifier requires Linux and an independent local Docker daemon.');
 const [verifier,endpoint]=await Promise.all([
  pinSocket(quotaSocket,'quota verifier',readStat),pinSocket(dockerSocket,'Docker daemon',readStat)
 ]);
 if(verifier.identity.device===endpoint.identity.device&&verifier.identity.inode===endpoint.identity.inode)
  throw Error('Restore quota verifier and Docker daemon must have different sockets.');
 const bootId=await readBootId();
 if(!isUuid(bootId))throw Error('Restore Docker host boot identity is unverified.');

 async function observe(phase,scope){
  const daemon=await readDockerDaemon(endpoint);
  const challenge=randomBytes(32).toString('hex');
  const body=JSON.stringify({schemaVersion:1,challenge,phase,budget,dockerSocket:endpoint.identity,...scope});
  const {value:evidence,digest}=await requestJson(verifier,'POST',verifyPath,body);
  if(!hasFields(evidence,['schemaVersion','challenge','requestDigest','daemon','quota','capacity','volumes'])||
     evidence.schemaVersion!==1||evidence.challenge!==challenge||evidence.requestDigest!==hash(body))
   throw Error('Restore quota evidence does not match the current verification request.');
  const daemonWitness=verifyDaemonWitness(evidence.daemon,endpoint.identity,daemon,bootId);
  const quotaWitness=verifyQuotaWitness(evidence.quota,evidence.capacity,daemon,budget);
  const volumes=await verifyVolumeWitnesses(endpoint,daemon,evidence.quota,evidence.volumes,scope);
  await endpoint.assertCurrent();
  return {
   quotaId:hash(JSON.stringify([daemonWitness,quotaWitness])),
   quotaBytes:evidence.quota.hardLimitBytes,availableBytes:evidence.capacity.availableBytes,evidenceDigest:digest,
   volumes:JSON.stringify(volumes)
  };
 }

 const initial=await observe('probe',{});
 const initializedVolumes=new Map();
 async function storageVerifier({phase,projectName,directory,volumeNames,budget:requested,ownerId,volumeIdentities}){
  if(!isLocalDockerEndpointBound(endpoint))throw Error('Restore quota verifier requires its pinned Docker daemon scope.');
  validateFixture(projectName,directory);
  if(!isCanonicalPath(directory)||!Array.isArray(volumeNames)||!volumeNames.length||
     new Set(volumeNames).size!==volumeNames.length||volumeNames.some(name=>typeof name!=='string'||!/^[a-z0-9-]+$/.test(name))||
     Object.keys(budget).some(key=>requested?.[key]!==budget[key]))
   throw Error('Restore quota verifier scope or budget changed.');
  const scope={projectName,directory,volumeNames:[...volumeNames].sort()};
  if(phase==='before-initialization'||phase==='before-extraction'){
   if(!isUuid(ownerId)||!hasFields(volumeIdentities,scope.volumeNames)||Object.values(volumeIdentities).some(value=>!isHash(value)))
    throw Error('Restore writable volume identity is missing.');
   Object.assign(scope,{ownerId,volumeIdentities});
  }else if(phase!=='preflight')throw Error('Invalid restore quota verification phase.');
  const {volumes,...proof}=await observe(phase,scope);
  if(proof.quotaId!==initial.quotaId)throw Error('Restore Docker daemon, backing mount, or quota identity changed.');
  if(phase==='before-initialization')initializedVolumes.set(directory,volumes);
  if(phase==='before-extraction'){
   if(initializedVolumes.get(directory)!==volumes)throw Error('Restore writable volume filesystem identity changed after initialization.');
   initializedVolumes.delete(directory);
  }
  return {schemaVersion:1,kind:'enforced-hard-quota',phase,...scope,...proof};
 }

 return Object.freeze({restoreBudget:budget,storageVerifier,withDocker:action=>withLocalDockerEndpoint(endpoint,action)});
}
