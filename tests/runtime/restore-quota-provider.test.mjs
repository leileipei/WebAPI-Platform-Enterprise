import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {createHash,randomUUID} from 'node:crypto';
import {EventEmitter} from 'node:events';
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import {PassThrough} from 'node:stream';
import test from 'node:test';
import * as acceptance from '../../scripts/runtime/acceptance.mjs';
import {provisionRestoreWritableVolumes} from '../../scripts/runtime/acceptance-backup.mjs';
import {docker,withLocalDockerEndpoint} from '../../scripts/runtime/docker.mjs';
import {requireRestoreStorageProof} from '../../scripts/runtime/restore-storage-budget.mjs';

const restoreBudget={maxSnapshotBytes:134217728,minHostFreeBytes:536870912,maxTargetBytes:536870912,minTargetFreeBytes:134217728};
const budgetArguments=['--max-snapshot-bytes','134217728','--min-host-free-bytes','536870912','--max-target-bytes','536870912','--min-target-free-bytes','134217728'];
const providerArguments=['--restore-quota-socket','/run/webapi-test/quota.sock','--restore-docker-socket','/run/webapi-test/docker.sock'];
const digest=value=>createHash('sha256').update(value).digest('hex');
const emptyProof={complete:false};

test('both acceptance shell modes forward explicit flags to the browser-enabled runtime entry',async t=>{
 const root=await fs.mkdtemp(path.join(await fs.realpath('/tmp'),'webapi-quota-cli-'));
 t.after(()=>fs.rm(root,{force:true,recursive:true}));
 const node=path.join(root,'argv-reader');
 await fs.writeFile(node,'#!/bin/sh\nprintf "%s\\n" "$@"\n',{mode:0o700});
 for(const mode of ['e2e','browser']){
  const result=spawnSync('bash',['scripts/check-local-runtime.sh',mode,...budgetArguments,...providerArguments],{
   env:{...process.env,WEBAPI_NODE:node},encoding:'utf8'
  });
  assert.equal(result.status,0,result.stderr);
  assert.deepEqual(result.stdout.trim().split('\n'),['scripts/runtime/acceptance.mjs','--browser',...budgetArguments,...providerArguments]);
 }
});

test('runtime CLI loads the explicit integration then runs acceptance in its Docker scope',async()=>{
 const events=[];
 const releaseManifest={sourceRevision:'a'.repeat(40)};
 async function storageVerifier(){throw Error('restore scenario owns the verifier calls');}
 const proof=await acceptance.runRuntimeAcceptanceCli([...budgetArguments,...providerArguments,'--browser','--release-file','fixtures/release.json'],{
  async configureStorage(options){
   events.push('verified');
   assert.deepEqual(options,{restoreBudget,quotaSocket:providerArguments[1],dockerSocket:providerArguments[3]});
   return {restoreBudget,storageVerifier,withDocker:async action=>{events.push('pinned');return action();}};
  },
  async loadRelease(file){events.push('release');assert.equal(file,path.resolve('fixtures/release.json'));return releaseManifest;},
  async runAcceptance(options){
   events.push('acceptance');
   assert.equal(options.browser,true);
   assert.equal(options.releaseManifest,releaseManifest);
   assert.equal(options.storageVerifier,storageVerifier);
   assert.deepEqual(options.restoreBudget,restoreBudget);
   assert.match(options.projectName,/^webapi-enterprise-local-test-[a-f0-9-]{36}$/);
   assert.equal(options.directory,path.resolve('.runtime/tests',options.projectName));
   return emptyProof;
  }
 });
 assert.equal(proof,emptyProof);
 assert.deepEqual(events,['verified','pinned','release','acceptance']);
});

test('runtime CLI rejects missing endpoints, incomplete budgets and malformed or duplicate flags before setup',async()=>{
 const deps={configureStorage(){assert.fail('storage integration must not open');},loadRelease(){assert.fail('release must not be read');},runAcceptance(){assert.fail('fixtures must not be created');}};
 for(const args of [
  ['--browser'],budgetArguments,[...budgetArguments.slice(0,-2),...providerArguments],
  [...budgetArguments.slice(0,-1),'1e6',...providerArguments],
  [...budgetArguments,...providerArguments,'--max-target-bytes','999'],
  [...budgetArguments,...providerArguments,'--unexpected']
 ])await assert.rejects(async()=>acceptance.runRuntimeAcceptanceCli(args,deps),/quota|budget|option/i);
});

test('isolated acceptance records unknown original-environment preservation without comparing two empty daemon snapshots',async()=>{
 const probeFailure=Error('synthetic stop before fixture creation');
 const report={};
 let originalReads=0;
 const deps={
  async loadRelease(){return {sourceRevision:'a'.repeat(40)};},
  async configureStorage(){
   return {
    restoreBudget,storageVerifier(){assert.fail('no restore operation is expected');},
    withDocker:action=>withLocalDockerEndpoint({path:providerArguments[3],assertCurrent(){assert.fail('no Docker operation is expected');}},action)
   };
  },
  async runAcceptance(options){
   return acceptance.runAcceptance(options,{
    async oldSnapshot(){originalReads++;return {containers:[],endpoints:{},archives:{}};},
    async beforeScenario(){throw probeFailure;},
    async writeReport(_directory,result){Object.assign(report,result);}
   });
  }
 };
 await assert.rejects(acceptance.runRuntimeAcceptanceCli([...budgetArguments,...providerArguments],deps),/synthetic stop/);
 assert.equal(originalReads,0);
 assert.equal(report.preservedOldEnvironment.unchanged,false);
 assert.equal(report.preservedOldEnvironment.status,'Unknown');
 assert.equal(report.complete,false);

 const completeFixture={
  ...report,chain:{passed:true},restart:{passed:true},failure:{passed:true},backupRestore:{passed:true},ui:{verified:true},
  cleanup:{containersRemaining:0,volumesRemaining:0,secretFilesRemaining:0}
 };
 assert.throws(()=>acceptance.validateAcceptance(completeFixture),/Old environment preservation not verified/);
});

async function listen(socket,handler,t){
 const server=http.createServer(handler);
 await new Promise((resolve,reject)=>{
  server.once('error',reject);
  server.listen(socket,()=>{server.removeListener('error',reject);resolve();});
 });
 t.after(()=>new Promise((resolve,reject)=>server.close(error=>error?reject(error):resolve())));
 return server;
}

function send(response,value){
 response.writeHead(200,{'content-type':'application/json'});
 response.end(JSON.stringify(value));
}

async function syntheticFixture(t){
 const {configureRestoreStorage}=await import('../../scripts/runtime/restore-quota-provider.mjs');
 const root=await fs.mkdtemp(path.join(await fs.realpath('/tmp'),'webapi-quota-'));
 const quotaSocket=path.join(root,'quota.sock'),dockerSocket=path.join(root,'docker.sock');
 const bootId=randomUUID(),projectName='webapi-enterprise-local-test-'+randomUUID(),ownerId=randomUUID();
 const directory=path.join(root,projectName),volumeNames=['lkg-a','pg','secrets-migrator'],requests=[];
 const daemonInfo={ID:'fixture-daemon',ServerVersion:'29.8.2',DockerRootDir:'/storage/docker',OSType:'linux',SecurityOptions:['name=seccomp,profile=builtin','name=cgroupns']};
 const apiVersion='1.53',filesystemUuid=randomUUID();
 const volumeRoot=path.join(daemonInfo.DockerRootDir,'volumes');
 const quota={
  kind:'filesystem-project-hard-quota',filesystem:'xfs',filesystemUuid,device:'253:16',mountId:'993813',
  mountPoint:'/storage',volumeRoot,volumeRootInode:'441',volumeRootHandle:'4'.repeat(64),projectId:71,projectInheritance:true,accounting:true,enforcement:true,
  hardLimitBytes:restoreBudget.maxTargetBytes
 };
 const capacity={path:'/storage/free-space-probe',device:quota.device,mountId:quota.mountId,projectId:0,availableBytes:restoreBudget.maxTargetBytes+restoreBudget.minTargetFreeBytes};
 const engineVolumes=Object.fromEntries(volumeNames.map(name=>[name,{
  Name:projectName+'_'+name,Driver:'local',Scope:'local',CreatedAt:'2026-11-20T01:02:03Z',
  Mountpoint:path.join(volumeRoot,projectName+'_'+name,'_data'),Options:null,
  Labels:{'com.docker.compose.project':projectName,'com.docker.compose.volume':name,'com.webapi.runtime.owner':ownerId,'com.webapi.restore.creation-nonce':'f'.repeat(64)}
 }]));
 const identity=volume=>digest(JSON.stringify([
  volume.Name,volume.Driver,volume.Scope,volume.CreatedAt,volume.Mountpoint,volume.Options,
  Object.entries(volume.Labels).sort(([left],[right])=>left.localeCompare(right))
 ]));
 const volumeIdentities=Object.fromEntries(volumeNames.map(name=>[name,identity(engineVolumes[name])]));
 const fixture={quotaSocket,dockerSocket,bootId,daemonInfo,quota,capacity,engineVolumes,requests,projectName,directory,
  ownerId,volumeNames,volumeIdentities,mutateReply:null,mutateStat:null,respond:send};
 const daemon=await listen(dockerSocket,(req,res)=>{
  if(req.url==='/version'){send(res,{ApiVersion:apiVersion,Version:daemonInfo.ServerVersion});return;}
  if(req.url===`/v${apiVersion}/info`){send(res,daemonInfo);return;}
  const volume=Object.values(engineVolumes).find(item=>req.url===`/v${apiVersion}/volumes/${item.Name}`);
  if(volume){send(res,volume);return;}
  res.writeHead(404).end();
 },t);
 const verifier=await listen(quotaSocket,async(req,res)=>{
  if(req.url!=='/v1/restore-storage/verify'||req.method!=='POST'){res.writeHead(404).end();return;}
  const chunks=[];
  for await(const chunk of req)chunks.push(chunk);
  const body=Buffer.concat(chunks),query=JSON.parse(body);
  requests.push(query);
  const reply={
   schemaVersion:1,challenge:query.challenge,requestDigest:digest(body),
   daemon:{socket:query.dockerSocket,bootId,pid:4321,startTicks:'82732',mountNamespace:'4026531841',
    id:daemonInfo.ID,version:daemonInfo.ServerVersion,apiVersion,rootDirectory:daemonInfo.DockerRootDir},
   quota:{...quota},capacity:{...capacity},
   volumes:query.phase==='probe'||query.phase==='preflight'?[]:volumeNames.map((name,index)=>({
    name,identity:identity(engineVolumes[name]),mountpoint:engineVolumes[name].Mountpoint,inode:String(900+index),handle:digest(name),
    mountId:quota.mountId,device:quota.device,projectId:quota.projectId,projectInheritance:true
   }))
  };
  fixture.mutateReply?.(reply,query);
  fixture.respond(res,reply);
 },t);
 t.after(()=>fs.rm(root,{force:true,recursive:true}));
 const readStat=async(file,options)=>{
  const actual=await fs.lstat(file,options);
  actual.uid=0n;
  actual.mode=(actual.mode&~0o777n)|(actual.isSocket()?0o660n:0o755n);
  return fixture.mutateStat?.(file,actual)??actual;
 };
 const connect=(options={},deps={})=>configureRestoreStorage({restoreBudget,quotaSocket,dockerSocket,...options},{
  platform:'linux',readStat,readBootId:async()=>bootId,...deps
 });
 const check=(integration,phase,previous,overrides={})=>integration.withDocker(()=>requireRestoreStorageProof({provider:integration.storageVerifier,
  budget:restoreBudget,projectName,directory,volumeNames,phase,previous,
  ...(phase==='preflight'?{}:{ownerId,volumeIdentities}),...overrides}));
 return Object.assign(fixture,{connect,check,daemon,verifier});
}

test('independent service attaches a distinct live witness to probe, preflight, initialization and extraction',async t=>{
 const f=await syntheticFixture(t),integration=await f.connect();
 const first=await f.check(integration,'preflight');
 const initialized=await f.check(integration,'before-initialization',first);
 const extracted=await f.check(integration,'before-extraction',initialized);
 assert.equal(first.quotaId,extracted.quotaId);
 assert.equal(first.operationId,extracted.operationId);
 assert.equal(extracted.quotaBytes,restoreBudget.maxTargetBytes);
 assert.deepEqual(extracted.volumeIdentities,f.volumeIdentities);
 assert.equal(new Set([first.evidenceDigest,initialized.evidenceDigest,extracted.evidenceDigest]).size,3);
 assert.deepEqual(f.requests.map(request=>request.phase),['probe','preflight','before-initialization','before-extraction']);
 assert.equal(new Set(f.requests.map(request=>request.challenge)).size,4);
 for(const request of f.requests)assert.deepEqual(request.budget,restoreBudget);
});

test('specialized CLIs bind all restore phases to their supplied synthetic Unix services',async t=>{
 const {promotionCli}=await import('../../scripts/delivery/cli.mjs');
 const {pipelineCli}=await import('../../scripts/delivery/pipeline-cli.mjs');
 const {securityAcceptanceCli}=await import('../../scripts/security/acceptance.mjs');
 const f=await syntheticFixture(t),modes=['e2e','faults','browser'];
 const flags=[...budgetArguments,'--restore-quota-socket',f.quotaSocket,'--restore-docker-socket',f.dockerSocket];
 const deps={
  configureStorage:options=>f.connect(options),
  async runAcceptance({restoreBudget:budget,storageVerifier:provider}){
   const scope={provider,budget,projectName:f.projectName,directory:f.directory,volumeNames:f.volumeNames};
   const identity={ownerId:f.ownerId,volumeIdentities:f.volumeIdentities};
   const first=await requireRestoreStorageProof({...scope,phase:'preflight'});
   const initialized=await requireRestoreStorageProof({...scope,...identity,phase:'before-initialization',previous:first});
   return requireRestoreStorageProof({...scope,...identity,phase:'before-extraction',previous:initialized});
  }
 };
 for(const [cli,commands] of [[promotionCli,modes],[pipelineCli,modes],[securityAcceptanceCli,['--revision']]]){
  for(const mode of commands){
   const start=f.requests.length;
   const result=await cli([mode,'a'.repeat(40),...flags],deps);
   assert.equal(result.quotaBytes,restoreBudget.maxTargetBytes);
   assert.deepEqual(f.requests.slice(start).map(request=>request.phase),['probe','preflight','before-initialization','before-extraction']);
  }
 }
});

test('a detached verifier cannot authorize a restore outside its own pinned Docker scope',async t=>{
 const f=await syntheticFixture(t),integration=await f.connect(),other=await f.connect();
 const preflight=()=>requireRestoreStorageProof({
  provider:integration.storageVerifier,budget:restoreBudget,phase:'preflight',
  projectName:f.projectName,directory:f.directory,volumeNames:f.volumeNames
 });
 await assert.rejects(preflight(),/pinned Docker daemon scope/i);
 await assert.rejects(other.withDocker(preflight),/pinned Docker daemon scope/i);
 assert.deepEqual(f.requests.map(request=>request.phase),['probe','probe']);
 await assert.doesNotReject(integration.withDocker(preflight));
});

test('service probe rejects nonexistent, caller-writable, symlinked or substituted endpoint paths',async t=>{
 const f=await syntheticFixture(t);
 await assert.rejects(f.connect({quotaSocket:f.quotaSocket+'-missing'}),/socket/i);
 await assert.rejects(f.connect({quotaSocket:f.dockerSocket}),/socket/i);
 await assert.rejects(f.connect({}, {readStat:fs.lstat}),/root-controlled/i);
 const symlink=f.quotaSocket+'-link';
 await fs.symlink(f.quotaSocket,symlink);
 await assert.rejects(f.connect({quotaSocket:symlink}),/socket/i);
 f.mutateStat=(file,actual)=>{if(file===path.dirname(f.quotaSocket))actual.mode|=0o020n;};
 await assert.rejects(f.connect(),/socket/i);
 f.mutateStat=(file,actual)=>{if(file===f.quotaSocket)actual.uid=1000n;};
 await assert.rejects(f.connect(),/socket/i);
 f.mutateStat=(file,actual)=>{if(file===f.quotaSocket)actual.mode|=0o002n;};
 await assert.rejects(f.connect(),/socket/i);
 f.mutateStat=null;
 const original=await f.connect();
 const newSocket=f.quotaSocket+'-new';
 await listen(newSocket,(_request,response)=>send(response,{}),t);
 await fs.rename(f.quotaSocket,f.quotaSocket+'-old');
 await fs.rename(newSocket,f.quotaSocket);
 await assert.rejects(f.check(original,'preflight'),/socket identity changed/i);
});

test('service probe refuses an unavailable verifier or an oversized response',async t=>{
 const f=await syntheticFixture(t);
 f.respond=response=>response.writeHead(503).end();
 await assert.rejects(f.connect(),/quota verifier evidence is unavailable/i);
 f.respond=response=>response.writeHead(200,{'content-type':'application/json'}).end(Buffer.alloc(256*1024+1,32));
 await assert.rejects(f.connect(),/quota verifier evidence is unavailable/i);
});

test('service probe rejects absent hard enforcement or usable backing capacity before a fixture can run',async t=>{
 const f=await syntheticFixture(t);
 for(const change of [
  reply=>{reply.quota.enforcement=false;},reply=>{reply.quota.accounting=false;},
  reply=>{reply.quota.hardLimitBytes=0;},reply=>{reply.quota.hardLimitBytes=restoreBudget.maxTargetBytes+1;},
  reply=>{reply.capacity.availableBytes--;},reply=>{reply.capacity.device='252:8';},
  reply=>{reply.capacity.path=reply.quota.volumeRoot+'/inside-project';},
  reply=>{reply.daemon.id='different-daemon';},reply=>{reply.daemon.version='29.9.4';},
  reply=>{reply.daemon.socket.inode='4421';},
  reply=>{reply.challenge='replayed';},reply=>{delete reply.requestDigest;}
 ]){
  f.mutateReply=change;
  await assert.rejects(f.connect(),/quota|daemon|evidence/i);
 }
});

test('storage evidence fails on a changed daemon process, mount, quota domain or hard limit',async t=>{
 const f=await syntheticFixture(t),integration=await f.connect();
 const first=await f.check(integration,'preflight');
 for(const change of [
  reply=>{reply.daemon.startTicks='4026531000';},reply=>{reply.daemon.mountNamespace='4026531934';},
  reply=>{reply.quota.mountId='993814';reply.capacity.mountId='993814';},
  reply=>{reply.quota.filesystemUuid=randomUUID();},reply=>{reply.quota.volumeRootInode='499';},
  reply=>{reply.quota.projectId++;},reply=>{reply.quota.hardLimitBytes--;}
 ]){
  f.mutateReply=change;
  await assert.rejects(f.check(integration,'before-initialization',first),/quota|daemon|volume|evidence/i);
 }
});

test('initialization requires every engine volume on the verified project, inode path and mount',async t=>{
 const f=await syntheticFixture(t),integration=await f.connect();
 const first=await f.check(integration,'preflight');
 for(const change of [
  reply=>{reply.volumes.pop();},reply=>{reply.volumes[0]=reply.volumes[1];},
  reply=>{reply.volumes[0].name='unexpected';},reply=>{reply.volumes[0].projectId++;},
  reply=>{reply.volumes[0].device='253:17';},reply=>{reply.volumes[0].mountId='98456';},
  reply=>{reply.volumes[0].projectInheritance=false;},
  reply=>{reply.volumes[0].mountpoint=reply.quota.volumeRoot+'/unrelated/_data';},
  reply=>{reply.volumes[0].identity='b'.repeat(64);}
 ]){
  f.mutateReply=change;
  await assert.rejects(f.check(integration,'before-initialization',first),/volume/i);
 }
 f.mutateReply=null;
 f.engineVolumes.pg.Options={type:'none',device:'/unrelated',o:'bind'};
 await assert.rejects(f.check(integration,'before-initialization',first),/volume/i);
});

test('wrong backing mount is rejected before the restore provisions any initializer write permit',async t=>{
 const f=await syntheticFixture(t),integration=await f.connect();
 const first=await f.check(integration,'preflight');
 const created=[];
 f.mutateReply=(reply,query)=>{
  if(query.phase==='before-initialization')reply.volumes[0].mountId='98541';
 };
 await assert.rejects(integration.withDocker(()=>provisionRestoreWritableVolumes({state:{projectName:f.projectName,ownerId:f.ownerId},directory:f.directory},{},null,{
  names:f.volumeNames,budget:restoreBudget,storageVerifier:integration.storageVerifier,previous:first,
  inspect:async()=>created.map(name=>({...f.engineVolumes[name],Kind:'volume'})),
  async run(args){
   assert.deepEqual(args.slice(0,2),['volume','create']);
   const fullName=args.at(-1),name=fullName.slice(f.projectName.length+1),labels={};
   assert.ok(f.volumeNames.includes(name));
   for(let index=2;index<args.length-1;index+=2){
    assert.equal(args[index],'--label');
    const [key,value]=args[index+1].split('=');
    labels[key]=value;
   }
   f.engineVolumes[name].Labels=labels;
   created.push(name);
   return {stdout:fullName};
  },
  persist(){assert.fail('wrong mount must not persist a verified initialization proof');},
  initialize(){assert.fail('wrong mount must not enter the initializer');}
 })),/volume/i);
 assert.deepEqual(created,f.volumeNames);
});

test('extraction refuses an inode replacement even when engine metadata and volume names remain equal',async t=>{
 const f=await syntheticFixture(t),integration=await f.connect();
 const first=await f.check(integration,'preflight');
 const initialized=await f.check(integration,'before-initialization',first);
 f.mutateReply=reply=>{reply.volumes[0].inode='998';};
 await assert.rejects(f.check(integration,'before-extraction',initialized),/volume/i);
 f.mutateReply=reply=>{reply.volumes[0].handle='9'.repeat(64);};
 await assert.rejects(f.check(integration,'before-extraction',initialized),/volume/i);
});

test('Docker child commands use only the pinned daemon while ordinary callers preserve their environment',async t=>{
 const f=await syntheticFixture(t),integration=await f.connect();
 const env={DOCKER_CONTEXT:'different-context',DOCKER_HOST:'tcp://untrusted.invalid:2376',
  DOCKER_TLS:'1',DOCKER_TLS_VERIFY:'1',DOCKER_CERT_PATH:'/unrelated',DOCKER_API_VERSION:'1.20',OTHER:'kept'};
 const calls=[];
 const spawnProcess=(command,args,options)=>{
  calls.push({command,args,env:options.env});
  const child=Object.assign(new EventEmitter(),{stdin:new PassThrough(),stdout:new PassThrough(),stderr:new PassThrough()});
  queueMicrotask(()=>child.emit('close',0));
  return child;
 };
 const run=()=>docker(['ps','-q'],{env,spawnProcess});
 await integration.withDocker(run);
 await run();
 assert.deepEqual(calls,[
  {command:'docker',args:['--host','unix://'+f.dockerSocket,'ps','-q'],env:{DOCKER_HOST:'unix://'+f.dockerSocket,OTHER:'kept'}},
  {command:'docker',args:['ps','-q'],env}
 ]);
 f.mutateStat=(file,actual)=>{if(file===f.dockerSocket)actual.ino+=1n;};
 await assert.rejects(integration.withDocker(run),/socket/i);
 assert.equal(calls.length,2);
});
