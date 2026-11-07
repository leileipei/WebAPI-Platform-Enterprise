import {spawn} from 'node:child_process';
import {RuntimeError} from './state.mjs';
export function createBoundedDockerOutput(limit){
 if(!Number.isSafeInteger(limit)||limit<1||limit>16*1024*1024)throw new RuntimeError('Invalid Docker output limit.',3);
 const data={stdout:Buffer.alloc(limit),stderr:Buffer.alloc(limit)},length={stdout:0,stderr:0};let remaining=limit,outputTruncated=false;
 return {push(stream,chunk){if(!Object.hasOwn(data,stream))throw new RuntimeError('Invalid Docker output stream.',3);const bytes=Buffer.isBuffer(chunk)?chunk:Buffer.from(chunk);const count=Math.min(remaining,bytes.length);if(count){bytes.copy(data[stream],length[stream],0,count);length[stream]+=count;remaining-=count;}if(count<bytes.length)outputTruncated=true;},
  result(){return {stdout:data.stdout.subarray(0,length.stdout).toString('utf8'),stderr:data.stderr.subarray(0,length.stderr).toString('utf8'),outputTruncated};}};
}
export function docker(args,{input,inputFd,env=process.env,cwd,allowFailure=false,maxOutputBytes,spawnProcess=spawn}={}){return new Promise((resolve,reject)=>{
 if(!args.every(a=>typeof a==='string'))return reject(new RuntimeError('Docker arguments must be strings.'));
 if(inputFd!==undefined&&(!Number.isInteger(inputFd)||inputFd<0||input!==undefined))return reject(new RuntimeError('Invalid Docker input descriptor.',3));
 let bounded;try{bounded=maxOutputBytes===undefined?null:createBoundedDockerOutput(maxOutputBytes);}catch(error){return reject(error);}
 const child=spawnProcess('docker',args,{cwd,env,stdio:[inputFd??'pipe','pipe','pipe']});let stdout='',stderr='';
 if(!bounded){child.stdout.setEncoding('utf8');child.stderr.setEncoding('utf8');}
 child.stdout.on('data',b=>{if(bounded)bounded.push('stdout',b);else stdout+=b;});
 child.stderr.on('data',b=>{if(bounded)bounded.push('stderr',b);else stderr+=b;});
 child.on('error',()=>reject(new RuntimeError('Docker command unavailable.',3)));
 child.on('close',exitCode=>{const result=bounded?{...bounded.result(),exitCode}:{stdout,stderr,exitCode};
  if(result.outputTruncated)return reject(Object.assign(new RuntimeError('Docker output exceeded the capture limit; inspect the retained fixture.',3),{result}));
  if(exitCode!==0&&!allowFailure)reject(Object.assign(new RuntimeError('Docker operation failed; inspect the owned project before retry.',3),{result}));else resolve(result);});
 if(inputFd===undefined)child.stdin.end(input);
});}
export function assertOwnership(state,resources){for(const r of resources){const labels=r.Labels??r.Config?.Labels??{};if(labels['com.docker.compose.project']!==state.projectName)throw new RuntimeError('Resource belongs to another project.',3);if(labels['com.webapi.runtime.owner']!==state.ownerId)throw new RuntimeError('Resource owner mismatch; refusing adoption.',3);}}
const serviceNames=['postgres','redis','collector','prometheus','loki','tempo','control-plane','worker','console','gateway-a','gateway-b','backend-a','backend-b','init-volumes','migrator','notification-fixture','init-notification-volumes'];
const volumeNames=['pg','lkg-a','lkg-b','dp-keys','prometheus-data','loki-data','tempo-data','bootstrap-password','verification-reports','migration-evidence','secrets-cache','secrets-postgres','secrets-control-plane','secrets-worker','secrets-gateway-a','secrets-gateway-b','secrets-migrator','notification-secrets','notification-fixture-secrets','notification-fixture-data'];
export async function inspectResources(state,run=docker){
 const resources=new Map();const add=(kind,values)=>{for(const r of values)resources.set(kind+':'+(r.Id??r.Name),{...r,Kind:kind});};
 for(const [kind,args,names]of [['container',['ps','-aq'],serviceNames.map(n=>state.projectName+'-'+n+'-1')],['volume',['volume','ls','-q'],volumeNames.map(n=>state.projectName+'_'+n)],['network',['network','ls','-q'],[state.projectName+'_default']]]){
  const list=await run([...args,'--filter','label=com.docker.compose.project='+state.projectName]);const ids=list.stdout.trim().split(/\s+/).filter(Boolean);
  if(ids.length){const inspected=await run([kind,'inspect',...ids]);add(kind,JSON.parse(inspected.stdout));}
  // Exact names may already exist without Compose labels. Inspect them before any mutation.
  const exact=await run([kind,'inspect',...names],{allowFailure:true});
  if(exact.exitCode!==0){const errors=(exact.stderr??'').trim().split('\n').filter(Boolean);if(!errors.length||errors.some(line=>!/no such (?:volume|container|network|object)|(?:volume|container|network|object).*not found/i.test(line)))throw new RuntimeError('Cannot establish runtime resource ownership.',3);}
  if(exact.stdout.trim())add(kind,JSON.parse(exact.stdout));
 }
 return [...resources.values()];
}
export async function ownedDocker(state,args,{inspect=inspectResources,run=docker,...options}={}){assertOwnership(state,await inspect(state,run));return run(args,options);}
