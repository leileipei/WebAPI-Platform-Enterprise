import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID} from 'node:crypto';
export class RuntimeError extends Error {constructor(message,exitCode=2){super(message);this.exitCode=exitCode;}}
export const persistentProject='webapi-enterprise-local';
const uuid=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
export const validGuid=value=>typeof value==='string'&&uuid.test(value)&&value!=='00000000-0000-0000-0000-000000000000';
function fields(value,allowed){if(!value||Array.isArray(value)||typeof value!=='object'||Object.keys(value).some(k=>!allowed.includes(k)))throw new RuntimeError('Invalid runtime schema field.');}
export function validateState(s){
 fields(s,['schemaVersion','projectName','ownerId','ports','bootstrapUsername','binding','demoEnabled','releaseId','initialized','bindingPhase']);
 if(s.schemaVersion!==1||!validGuid(s.ownerId)||!(s.projectName===persistentProject||/^webapi-enterprise-local-test-[a-f0-9-]{36}$/.test(s.projectName)))throw new RuntimeError('Invalid runtime identity.');
 fields(s.ports,['console','gatewayA','gatewayB']);const ports=Object.values(s.ports);if(ports.length!==3||ports.some(p=>!Number.isInteger(p)||p<1||p>65535)||new Set(ports).size!==3)throw new RuntimeError('Invalid runtime ports.');
 if(typeof s.bootstrapUsername!=='string'||!s.bootstrapUsername.trim()||s.bootstrapUsername.length>128||/[\r\n\0]/.test(s.bootstrapUsername))throw new RuntimeError('Explicit administrator username required.');
 if(typeof s.demoEnabled!=='boolean'||typeof s.initialized!=='boolean'||!(s.releaseId===null||typeof s.releaseId==='string')||![null,'Validated','Applied'].includes(s.bindingPhase))throw new RuntimeError('Invalid runtime schema value.');
 if(s.binding!==null){fields(s.binding,['environmentId','nodeNames','allowedOrigins']);if(!validGuid(s.binding.environmentId))throw new RuntimeError('Invalid environment identity.');if(JSON.stringify(s.binding.nodeNames)!==JSON.stringify(['local-gateway-a','local-gateway-b']))throw new RuntimeError('Invalid node names.');validateOrigins(s.binding.allowedOrigins);}
 return s;
}
export function validateOrigins(values){if(!Array.isArray(values)||values.length>128||new Set(values).size!==values.length)throw new RuntimeError('Invalid upstream origins.');for(const v of values){let u;try{u=new URL(v);}catch{throw new RuntimeError('Invalid upstream origin.');}if(!['http:','https:'].includes(u.protocol)||u.username||u.password||u.search||u.hash||u.pathname!=='/'||v!==u.origin)throw new RuntimeError('Upstream origin must be canonical HTTP(S) origin without a path or credentials.');}return values;}
export async function safeFile(file){const st=await fs.lstat(file);if(st.isSymbolicLink()||!st.isFile()||st.uid!==process.getuid?.()||(st.mode&0o077))throw new RuntimeError('Runtime file must be owned, private and not a symlink.',3);return st;}
export async function ownedDirectory(directory){const st=await fs.lstat(directory);if(st.isSymbolicLink())throw new RuntimeError('Runtime directory cannot be a symlink.',3);if(!st.isDirectory()||st.uid!==process.getuid?.()||(st.mode&0o077))throw new RuntimeError('Runtime directory must be owned and private.',3);return fs.realpath(directory);}
export async function writePrivate(file,value){const tmp=file+'.'+randomUUID()+'.tmp';const handle=await fs.open(tmp,'wx',0o600);try{await handle.writeFile(typeof value==='string'?value:JSON.stringify(value,null,2)+'\n');await handle.sync();}finally{await handle.close();}try{await fs.rename(tmp,file);const parent=await fs.open(path.dirname(file),'r');try{await parent.sync();}finally{await parent.close();}}catch(e){await fs.rm(tmp,{force:true});throw e;}}
export async function loadState(directory){directory=await ownedDirectory(directory);await safeFile(path.join(directory,'owner.json'));await safeFile(path.join(directory,'runtime.json'));const owner=JSON.parse(await fs.readFile(path.join(directory,'owner.json'),'utf8'));const state=validateState(JSON.parse(await fs.readFile(path.join(directory,'runtime.json'),'utf8')));if(owner.schemaVersion!==1||owner.projectName!==state.projectName||owner.ownerId!==state.ownerId)throw new RuntimeError('Foreign or inconsistent runtime owner.',3);return state;}
export async function saveState(directory,state){validateState(state);const previous=await loadState(directory);if(previous.ownerId!==state.ownerId||previous.projectName!==state.projectName||previous.bootstrapUsername!==state.bootstrapUsername)throw new RuntimeError('Cannot replace runtime owner or bootstrap administrator.',3);await writePrivate(path.join(directory,'runtime.json'),state);}
export async function initializeState(directory,{bootstrapUsername,ports={console:4190,gatewayA:4196,gatewayB:4197},projectName=persistentProject}={}){
 try{await fs.lstat(directory);try{return await loadState(directory);}catch(e){throw new RuntimeError('Foreign or non-owned runtime directory; symlink is not allowed.',3);}}catch(e){if(e.code!=='ENOENT')throw e;}
 const state=validateState({schemaVersion:1,projectName,ownerId:randomUUID(),ports,bootstrapUsername,binding:null,demoEnabled:false,releaseId:null,initialized:false,bindingPhase:null});
 await fs.mkdir(path.dirname(directory),{recursive:true});await fs.mkdir(directory,{mode:0o700});await writePrivate(path.join(directory,'owner.json'),{schemaVersion:1,projectName,ownerId:state.ownerId});await writePrivate(path.join(directory,'runtime.json'),state);return state;
}
const pause=ms=>new Promise(r=>setTimeout(r,ms));
export async function withRuntimeLock(directory,action){
 directory=await ownedDirectory(directory);const file=path.join(directory,'operation.lock');const until=Date.now()+60000;let handle;
 while(!handle){try{handle=await fs.open(file,'wx',0o600);await handle.writeFile(JSON.stringify({pid:process.pid,ownerId:(await loadState(directory)).ownerId}));}catch(e){if(e.code!=='EEXIST')throw e;await safeFile(file);let lock;try{lock=JSON.parse(await fs.readFile(file,'utf8'));}catch{if(Date.now()>until)throw new RuntimeError('Runtime lock is incomplete; inspect before retry.',3);await pause(25);continue;}if(!Number.isInteger(lock.pid)||lock.ownerId!==(await loadState(directory)).ownerId)throw new RuntimeError('Foreign runtime lock.',3);let dead=false;try{process.kill(lock.pid,0);}catch(k){dead=k.code==='ESRCH';}if(dead){await fs.unlink(file);continue;}if(Date.now()>until)throw new RuntimeError('Another runtime operation is active.',3);await pause(25);}}
 try{return await action();}finally{await handle.close();await fs.unlink(file);}
}
