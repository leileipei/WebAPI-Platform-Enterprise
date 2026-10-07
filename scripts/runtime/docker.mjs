import {spawn} from 'node:child_process';
import {RuntimeError} from './state.mjs';
export function docker(args,{input,env=process.env,cwd,allowFailure=false}={}){return new Promise((resolve,reject)=>{if(!args.every(a=>typeof a==='string'))return reject(new RuntimeError('Docker arguments must be strings.'));const child=spawn('docker',args,{cwd,env,stdio:['pipe','pipe','pipe']});let stdout='',stderr='';child.stdout.on('data',b=>stdout+=b);child.stderr.on('data',b=>stderr+=b);child.on('error',()=>reject(new RuntimeError('Docker command unavailable.',3)));child.on('close',exitCode=>{const result={stdout,stderr,exitCode};if(exitCode!==0&&!allowFailure)reject(Object.assign(new RuntimeError('Docker operation failed; inspect the owned project before retry.',3),{result}));else resolve(result);});child.stdin.end(input);});}
export function assertOwnership(state,resources){for(const r of resources){const labels=r.Labels??r.Config?.Labels??{};if(labels['com.docker.compose.project']!==state.projectName)throw new RuntimeError('Resource belongs to another project.',3);if(labels['com.webapi.runtime.owner']!==state.ownerId)throw new RuntimeError('Resource owner mismatch; refusing adoption.',3);}}
const serviceNames=['postgres','redis','collector','prometheus','loki','tempo','control-plane','worker','console','gateway-a','gateway-b','backend-a','backend-b','init-volumes','migrator','notification-fixture','init-notification-volumes'];
const volumeNames=['pg','lkg-a','lkg-b','dp-keys','prometheus-data','loki-data','tempo-data','bootstrap-password','secrets-cache','secrets-postgres','secrets-control-plane','secrets-worker','secrets-gateway-a','secrets-gateway-b','secrets-migrator','notification-secrets','notification-fixture-secrets','notification-fixture-data'];
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
