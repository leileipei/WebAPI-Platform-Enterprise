import fs from 'node:fs/promises';
import {constants} from 'node:fs';
import path from 'node:path';
import {createHash,randomUUID} from 'node:crypto';
import {ownedDirectory} from './state.mjs';
import {validateRestoreStorageBudget} from './restore-storage-budget.mjs';

const maxEntries=4096,maxSafeSize=BigInt(Number.MAX_SAFE_INTEGER);
const indexName='.webapi-restore-input-index.json';
const reservedNames=new Set([indexName,'restore-destination-reference.json',...['preflight','before-initialization','before-extraction'].map(phase=>'.webapi-restore-quota-'+phase+'.json')]);
const fields=['dev','ino','size','mtimeNs','ctimeNs'];
const same=(left,right)=>fields.every(key=>left[key]===right[key]);
const unsafe=()=>new Error('Restore input is unsafe, missing, changed or not covered by the manifest');

export function restoreInputNames(manifest){
 if(!manifest||typeof manifest!=='object'||Array.isArray(manifest)||!manifest.files||typeof manifest.files!=='object'||Array.isArray(manifest.files))throw unsafe();
 const names=Object.keys(manifest.files);
 if(names.length<1||names.length>maxEntries)throw unsafe();
 for(const name of names){
  if(!name||reservedNames.has(name)||path.posix.isAbsolute(name)||name!==path.posix.normalize(name)||/[\\\x00-\x1f\x7f]/.test(name)||
     name.split('/').some(part=>!part||part==='.'||part==='..')||!/^[a-f0-9]{64}$/.test(manifest.files[name]??''))throw unsafe();
 }
 return names.sort();
}

async function rootDirectory(source){
 const info=await fs.lstat(source,{bigint:true});
 if(!info.isDirectory()||info.isSymbolicLink()||info.uid!==BigInt(process.getuid()))throw unsafe();
 return info;
}

async function privateInput(source,name){
 const parts=name.split('/');let current=source;
 for(const part of parts.slice(0,-1)){
  current=path.join(current,part);const item=await fs.lstat(current,{bigint:true});
  if(!item.isDirectory()||item.isSymbolicLink()||item.uid!==BigInt(process.getuid()))throw unsafe();
 }
 const file=path.join(source,...parts),before=await fs.lstat(file,{bigint:true});
 if(!before.isFile()||before.isSymbolicLink()||before.uid!==BigInt(process.getuid())||before.nlink!==1n||
    (before.mode&0o077n)!==0n||before.size>maxSafeSize)throw unsafe();
 return {file,before};
}

async function readHash(handle,opened,copy){
 const buffer=Buffer.alloc(65536),hash=createHash('sha256');let position=0;
 while(BigInt(position)<opened.size){
  const part=await handle.read(buffer,0,Math.min(buffer.length,Number(opened.size-BigInt(position))),position);
  if(part.bytesRead<=0)throw unsafe();
  hash.update(buffer.subarray(0,part.bytesRead));
  if(copy){let written=0;while(written<part.bytesRead){const result=await copy.write(buffer,written,part.bytesRead-written,position+written);if(result.bytesWritten<=0)throw unsafe();written+=result.bytesWritten;}}
  position+=part.bytesRead;
 }
 return hash.digest('hex');
}

export async function readVerifiedRestoreInput(source,name,manifest,{maxBytes=4*1024*1024}={}){
 if(!path.isAbsolute(source)||!Number.isSafeInteger(maxBytes)||maxBytes<1||!restoreInputNames(manifest).includes(name))throw unsafe();
 await rootDirectory(source);const {file,before}=await privateInput(source,name);
 if(before.size>BigInt(maxBytes))throw unsafe();
 const handle=await fs.open(file,constants.O_RDONLY|constants.O_NOFOLLOW);
 try{
  const opened=await handle.stat({bigint:true});if(!same(before,opened))throw unsafe();
  const bytes=await handle.readFile(),after=await handle.stat({bigint:true}),named=await fs.lstat(file,{bigint:true});
  if(!same(opened,after)||!same(opened,named)||createHash('sha256').update(bytes).digest('hex')!==manifest.files[name])throw unsafe();
  return bytes;
 }finally{await handle.close();}
}

// Enumerate only inputs which the restore may copy implicitly. An unlisted
// secret or optional configuration cannot be silently copied or dropped.
export async function requireRestoreSourceCoverage(source,manifest,{directories=[],optionalFiles=[]}={}){
 const names=new Set(restoreInputNames(manifest));await rootDirectory(source);let visited=0;
 async function walk(relative,depth){
  if(depth>16)throw unsafe();const absolute=path.join(source,relative);let info;
  try{info=await fs.lstat(absolute,{bigint:true});}catch(error){if(error.code==='ENOENT')return;throw error;}
  if(info.isSymbolicLink()||!info.isDirectory()||info.uid!==BigInt(process.getuid()))throw unsafe();
  const entries=await fs.readdir(absolute,{withFileTypes:true});if(entries.length>maxEntries)throw unsafe();
  for(const entry of entries){
   if(++visited>maxEntries*2)throw unsafe();
   const name=relative?relative+'/'+entry.name:entry.name;
   if(entry.isDirectory())await walk(name,depth+1);
   else if(!entry.isFile()||!names.has(name))throw unsafe();
  }
 }
 for(const directory of directories)await walk(directory,0);
 for(const name of optionalFiles){
  try{await fs.lstat(path.join(source,name));if(!names.has(name))throw unsafe();}
  catch(error){if(error.code!=='ENOENT')throw error;}
 }
}

// Copies a bounded manifest sequentially into a new private directory. The
// digest is checked against the original manifest while copying; source and
// destination identities are then checked again. It is not a signature.
export async function freezeRestoreInputs(source,parent,manifest,{budget,statfs=fs.statfs,operationId=randomUUID(),onSnapshotCreated=async()=>{}}={}){
 if(!path.isAbsolute(source)||!path.isAbsolute(parent))throw unsafe();
 if(!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(operationId??''))throw unsafe();
 budget=validateRestoreStorageBudget(budget);
 const names=restoreInputNames(manifest),root=await rootDirectory(source),inputs=[];let total=0n;
 for(const name of names){const input=await privateInput(source,name);total+=input.before.size;if(total>maxSafeSize)throw unsafe();inputs.push({name,...input});}
 if(total>BigInt(budget.maxSnapshotBytes))throw new Error('Restore input snapshot exceeds the configured budget');
 // Account conservatively for the frozen input, a second full-size set of
 // copied secrets/metadata and an additional evidence archive copy. The
 // metadata margin is advisory; it is not an enforced reservation.
 const needed=3n*total+64n*1024n*1024n+BigInt(budget.maxTargetBytes)+BigInt(budget.minHostFreeBytes);
 const initial=await statfs(parent,{bigint:true});if(needed>initial.bavail*initial.bsize)throw new Error('Insufficient host space for restore budget and reserve');
 const directory=await fs.mkdtemp(path.join(parent,'.webapi-restore-input-'+operationId+'-'));
 try{
 await fs.chmod(directory,0o700);const parentHandle=await fs.open(parent,'r');try{await parentHandle.sync();}finally{await parentHandle.close();}
 await onSnapshotCreated(directory);
 const storage=await statfs(directory,{bigint:true});if(needed>storage.bavail*storage.bsize)throw new Error('Insufficient host space for restore budget and reserve');
 const files={};
 for(const input of inputs){
  const destination=path.join(directory,...input.name.split('/'));
  await fs.mkdir(path.dirname(destination),{recursive:true,mode:0o700});
  const sourceHandle=await fs.open(input.file,constants.O_RDONLY|constants.O_NOFOLLOW);let output;
  try{
   const opened=await sourceHandle.stat({bigint:true});if(!same(input.before,opened))throw unsafe();
   output=await fs.open(destination,constants.O_WRONLY|constants.O_CREAT|constants.O_EXCL|constants.O_NOFOLLOW,0o600);
   const hash=await readHash(sourceHandle,opened,output);
   const after=await sourceHandle.stat({bigint:true}),named=await fs.lstat(input.file,{bigint:true});
   if(!same(opened,after)||!same(opened,named)||hash!==manifest.files[input.name])throw unsafe();
   await output.sync();await output.chmod(0o400);await output.sync();const stat=await output.stat({bigint:true});
   files[input.name]={sha256:hash,sourceMode:Number(input.before.mode&0o700n),identity:Object.fromEntries(fields.map(key=>[key,stat[key].toString()]))};
  }finally{if(output)await output.close();await sourceHandle.close();}
  const targetDirectory=await fs.open(path.dirname(destination),'r');try{await targetDirectory.sync();}finally{await targetDirectory.close();}
 }
 const current=await rootDirectory(source);if(root.dev!==current.dev||root.ino!==current.ino)throw unsafe();
 const indexBytes=Buffer.from(JSON.stringify({schemaVersion:1,files:Object.fromEntries(names.map(name=>[name,{sha256:files[name].sha256,sourceMode:files[name].sourceMode}]))})+'\n');
 const indexHandle=await fs.open(path.join(directory,indexName),constants.O_WRONLY|constants.O_CREAT|constants.O_EXCL|constants.O_NOFOLLOW,0o600);let index;
 try{
  await indexHandle.writeFile(indexBytes);await indexHandle.sync();await indexHandle.chmod(0o400);await indexHandle.sync();
  const stat=await indexHandle.stat({bigint:true});index={sha256:createHash('sha256').update(indexBytes).digest('hex'),identity:Object.fromEntries(fields.map(key=>[key,stat[key].toString()]))};
 }finally{await indexHandle.close();}
 const rootHandle=await fs.open(directory,'r');try{await rootHandle.sync();}finally{await rootHandle.close();}
 const snapshot={directory,files,index};await verifyFrozenRestoreInputs(snapshot);return snapshot;
 }catch(error){
  const failure=new Error('Restore input snapshot failed; retained at '+directory,{cause:error});
  failure.retainedRestoreInputs=directory;throw failure;
 }
}

export async function verifyFrozenRestoreInputs(snapshot){
 if(!snapshot||!path.isAbsolute(snapshot.directory??'')||!snapshot.files||typeof snapshot.files!=='object'||!snapshot.index)throw unsafe();
 const directory=await ownedDirectory(snapshot.directory),names=Object.keys(snapshot.files).sort();
 if(names.length<1||names.length>maxEntries)throw unsafe();
 restoreInputNames({files:Object.fromEntries(names.map(name=>[name,snapshot.files[name]?.sha256]))});
 for(const name of names){
  const record=snapshot.files[name];if(!record||!/^[a-f0-9]{64}$/.test(record.sha256??'')||!record.identity)throw unsafe();
  const {file,before}=await privateInput(directory,name);
  if((before.mode&0o777n)!==0o400n||fields.some(key=>before[key].toString()!==record.identity[key]))throw unsafe();
  const handle=await fs.open(file,constants.O_RDONLY|constants.O_NOFOLLOW);
  try{
   const opened=await handle.stat({bigint:true});if(!same(before,opened)||await readHash(handle,opened)!==record.sha256)throw unsafe();
   const after=await handle.stat({bigint:true}),named=await fs.lstat(file,{bigint:true});if(!same(opened,after)||!same(opened,named))throw unsafe();
  }finally{await handle.close();}
 }
 const index=await privateInput(directory,indexName);
 if((index.before.mode&0o777n)!==0o400n||!/^[a-f0-9]{64}$/.test(snapshot.index.sha256??'')||!snapshot.index.identity||
    fields.some(key=>index.before[key].toString()!==snapshot.index.identity[key]))throw unsafe();
 const handle=await fs.open(index.file,constants.O_RDONLY|constants.O_NOFOLLOW);
 try{
  const opened=await handle.stat({bigint:true});if(!same(index.before,opened)||await readHash(handle,opened)!==snapshot.index.sha256)throw unsafe();
  const after=await handle.stat({bigint:true}),named=await fs.lstat(index.file,{bigint:true});if(!same(opened,after)||!same(opened,named))throw unsafe();
 }finally{await handle.close();}
}

// Snapshot files are read-only; copied runtime secrets retain their original
// private owner permissions so a normal 0600 secret does not become 0400.
export async function restoreInputSecretModes(destination,snapshot,prefix='secrets/'){
 if(!['secrets/','notification-secrets/'].includes(prefix))throw unsafe();
 const directory=await ownedDirectory(destination);
 for(const [name,record] of Object.entries(snapshot.files)){
  if(!name.startsWith(prefix))continue;
  if(!Number.isInteger(record.sourceMode)||record.sourceMode<0||record.sourceMode>0o700||(record.sourceMode&0o077)!==0)throw unsafe();
  const {file,before}=await privateInput(directory,name),handle=await fs.open(file,constants.O_RDONLY|constants.O_NOFOLLOW);
  try{
   const opened=await handle.stat({bigint:true});if(!same(before,opened))throw unsafe();
   await handle.chmod(record.sourceMode);await handle.sync();const named=await fs.lstat(file,{bigint:true});
   if(named.dev!==opened.dev||named.ino!==opened.ino)throw unsafe();
  }finally{await handle.close();}
 }
}
