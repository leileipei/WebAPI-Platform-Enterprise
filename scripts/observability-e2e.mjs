import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';

const directory=process.env.WEBAPI_E2E_DIRECTORY;
const project=process.env.WEBAPI_E2E_PROJECT;
assert(/^webapi-enterprise-e2e-observability-[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(project));
assert.equal(fs.realpathSync(directory),path.join(fs.realpathSync('.'),'.runtime','e2e',project));
const mode=process.argv[2];
const revision=process.env.WEBAPI_SOURCE_REVISION;
assert(/^[0-9a-f]{40}$/.test(revision),'A fixed source commit is required');
const runReceipt=path.join(directory,'verification.json');
const evidenceDirectory=path.join(directory,'observability-evidence');
const baseSecrets=['password','postgres-password','node-a','node-b','ip-hmac','cursor-key','login-hmac'];
const users=['admin','reader','approver','security'];
const secretNames=[...baseSecrets,...users.map(user=>'password-e2e-'+user),'node-cross','node-cross2','credential','browser-cookie'];
const observationMode=process.env.WEBAPI_OBS_EXPECT_EMPTY==='true'||process.env.WEBAPI_OBS_ONLY_BOUNDARIES==='true'?'debug':process.env.WEBAPI_OBS_MODE;
function startReceipt(){
 return {complete:false,project,sourceRevision:revision,startedAt:new Date().toISOString(),classification:'real-isolated-gateway-three-source',mode:observationMode,command:'./scripts/check-observability.sh '+observationMode,checks:[],cleanup:null};
}
const save=(file,record)=>fs.writeFileSync(file,JSON.stringify(record,null,2)+'\n',{mode:0o600});
const load=file=>fs.existsSync(file)?JSON.parse(fs.readFileSync(file,'utf8')):null;
if(mode==='init'){
 fs.writeFileSync(runReceipt,JSON.stringify(startReceipt(),null,2)+'\n',{flag:'wx',mode:0o600});
 fs.mkdirSync(evidenceDirectory,{mode:0o700});
 for(const name of baseSecrets){
  const base64=['ip-hmac','cursor-key','login-hmac'].includes(name);
  fs.writeFileSync(path.join(directory,name),randomBytes(32).toString(base64?'base64':'base64url'),{mode:name==='login-hmac'?0o400:0o600,flag:'wx'});
 }
}else if(mode==='ready'){
 const endpoints=Object.fromEntries(['CONTROL_PLANE','COLLECTOR','PROMETHEUS','LOKI','TEMPO','CONSOLE','GATEWAY_A','GATEWAY_B','BACKEND_A','BACKEND_B'].map(name=>[name.toLowerCase(),process.env['WEBAPI_'+(name==='CONTROL_PLANE'?'E2E_':'OBS_')+name+'_URL']]));
 for(const [name,route] of [['prometheus','/-/ready'],['loki','/ready'],['tempo','/ready']]){
  const end=Date.now()+120000;
  for(;;){
   try{if((await fetch(endpoints[name]+route,{signal:AbortSignal.timeout(3000)})).ok)break;}catch{}
   if(Date.now()>end)throw Error(name+' readiness timeout');
   await new Promise(resolve=>setTimeout(resolve,1000));
  }
 }
 const current=load(runReceipt);
 assert.equal(current?.project,project);
 save(path.join(directory,'observability-context.json'),{project,sourceRevision:revision,endpoints,settings:{intervalSeconds:2,queryDelaySeconds:5,leaseSeconds:30,metricExportIntervalMs:1000,traceSampleRatio:1,queueCapacity:64}});
 save(runReceipt,{...current,endpoints});
 console.log('All three real sources ready; closed-loop assertions still required.');
}else if(mode==='cleanup'){
 let result=Number(process.argv[3]);
 for(const name of secretNames){
  try{fs.unlinkSync(path.join(directory,name));}
  catch(error){if(error.code!=='ENOENT')result=1;}
 }
 const remaining=fs.readdirSync(directory).filter(name=>secretNames.includes(name)||name.startsWith('password-'));
 const cleanup={exitCode:result,secretFilesRemaining:remaining.length,containersRemaining:Number(process.argv[4]),volumesRemaining:Number(process.argv[5]),networksRemaining:Number(process.argv[6])};
 if(Object.values(cleanup).slice(1).some(n=>!Number.isSafeInteger(n)||n!==0))cleanup.exitCode=1;
 const current=load(runReceipt)||startReceipt();
 assert.equal(current.project,project);
 const checks=[];
 for(const name of ['loop','rule-loop','boundaries']){
  const file=name+'.json';
  const observation=load(path.join(evidenceDirectory,file));
  if(observation?.project===project&&observation.sourceRevision===revision&&observation.complete===true)checks.push({name,status:'passed',evidence:file});
 }
 const receipt={...current,checks,complete:false,criticalAcceptanceComplete:cleanup.exitCode===0&&['e2e','faults'].includes(observationMode)&&checks.length===3,exitCode:cleanup.exitCode,cleanup};
 save(runReceipt,receipt);
 if(cleanup.exitCode!==Number(process.argv[3]))process.exitCode=1;
}else if(mode==='browser'){
 fs.writeFileSync(path.join(directory,'browser.wait'),'');
 console.log('Browser fixture metadata: '+path.join(directory,'observability-context.json'));
}else throw Error('Unknown command');
