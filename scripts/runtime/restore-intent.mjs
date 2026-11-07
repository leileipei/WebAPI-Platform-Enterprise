import fs from 'node:fs/promises';
import {constants} from 'node:fs';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {parseRestoreManifest} from './restore-manifest.mjs';
import {assertTrustedRestoreParent,withRestoreTargetLock} from './restore-target-lock.mjs';

const uuid=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const project=/^webapi-enterprise-local-test-[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/;
const safePath=value=>typeof value==='string'&&path.isAbsolute(value)&&!/[\u0000\r\n,:]/.test(value);
const targetDigest=directory=>createHash('sha256').update(directory).digest('hex');
async function ownedDirectory(directory){const info=await fs.lstat(directory);if(!info.isDirectory()||info.isSymbolicLink()||info.uid!==process.getuid()||(info.mode&0o077))throw Error('Unsafe restore directory');return fs.realpath(directory);}
async function writeExclusive(file,value){
 const encoded=JSON.stringify(value)+'\n';if(Buffer.byteLength(encoded)>65536)throw Error('Restore trace record exceeds the limit');
 const handle=await fs.open(file,constants.O_WRONLY|constants.O_CREAT|constants.O_EXCL|constants.O_NOFOLLOW,0o600);
 try{await handle.writeFile(encoded);await handle.sync();}finally{await handle.close();}
 const parent=await fs.open(path.dirname(file),'r');try{await parent.sync();}finally{await parent.close();}
}

export async function createRestoreIntent(parent,proof,{targetLock}={}){
 if(!safePath(parent)||await fs.realpath(parent)!==parent)throw Error('Invalid restore intent parent');
 const info=await fs.lstat(parent);if(!info.isDirectory()||info.isSymbolicLink()||info.uid!==process.getuid())throw Error('Unsafe restore intent parent');
 if(proof?.phase!=='preflight'||proof.traceFormatVersion!==2||!uuid.test(proof.operationId??'')||!project.test(proof.projectName??'')||
    !safePath(proof.directory)||path.dirname(proof.directory)!==parent||path.basename(proof.directory)!==proof.projectName)throw Error('Invalid restore intent identity');
 return withRestoreTargetLock(proof.directory,async target=>{
  if(target!==proof.directory)throw Error('Restore target identity changed');
  await assertTrustedRestoreParent(parent);await assertNoUnresolvedRestoreIntent(target);
  if(await fs.lstat(target).catch(error=>error.code==='ENOENT'?null:Promise.reject(error)))throw Error('Restore requires a fresh target directory');
  const snapshotPrefix=path.join(parent,'.webapi-restore-input-'+proof.operationId+'-');
  const record={schemaVersion:1,operationId:proof.operationId,projectName:proof.projectName,directory:proof.directory,snapshotPrefix,
   preflightSha256:createHash('sha256').update(JSON.stringify(proof)+'\n').digest('hex'),preflightProof:proof,phase:'planned'};
  const file=path.join(parent,'.webapi-restore-intent-v2-'+targetDigest(proof.directory)+'-'+proof.operationId+'.json');await writeExclusive(file,record);
  return {...record,file};
 },{token:targetLock});
}

export async function discoverRestoreIntent(file){
 if(!safePath(file))throw Error('Invalid restore intent path');const parent=path.dirname(file),before=await fs.lstat(file);
 if(!before.isFile()||before.isSymbolicLink()||before.uid!==process.getuid()||before.nlink!==1||(before.mode&0o077)||before.size>65536)throw Error('Unsafe restore intent record');
 const handle=await fs.open(file,constants.O_RDONLY|constants.O_NOFOLLOW);let record;
 try{const opened=await handle.stat();if(opened.dev!==before.dev||opened.ino!==before.ino||opened.size!==before.size)throw Error('Restore intent changed');record=parseRestoreManifest(new TextDecoder('utf-8',{fatal:true}).decode(await handle.readFile()));const after=await handle.stat(),named=await fs.lstat(file);if(['dev','ino','size','mtimeMs','ctimeMs'].some(key=>after[key]!==opened[key]||named[key]!==opened[key])||named.nlink!==1)throw Error('Restore intent changed');}finally{await handle.close();}
 const expectedKeys=['schemaVersion','operationId','projectName','directory','snapshotPrefix','preflightSha256','preflightProof','phase'];
 if(!record||typeof record!=='object'||Array.isArray(record)||Object.keys(record).length!==expectedKeys.length||expectedKeys.some(key=>!Object.hasOwn(record,key))||
    record.schemaVersion!==1||!uuid.test(record.operationId??'')||!project.test(record.projectName??'')||record.phase!=='planned'||
    record.directory!==path.join(parent,record.projectName)||record.snapshotPrefix!==path.join(parent,'.webapi-restore-input-'+record.operationId+'-')||
    !['.webapi-restore-intent-'+record.operationId+'.json','.webapi-restore-intent-v2-'+targetDigest(record.directory)+'-'+record.operationId+'.json'].includes(path.basename(file))||!/^[a-f0-9]{64}$/.test(record.preflightSha256??'')||
    record.preflightProof?.phase!=='preflight'||record.preflightProof?.traceFormatVersion!==2||
    record.preflightProof?.operationId!==record.operationId||record.preflightProof?.directory!==record.directory||record.preflightProof?.projectName!==record.projectName||
    createHash('sha256').update(JSON.stringify(record.preflightProof)+'\n').digest('hex')!==record.preflightSha256)throw Error('Restore intent identity is invalid');
 const snapshots=[];for(const name of (await fs.readdir(parent)).filter(name=>name.startsWith(path.basename(record.snapshotPrefix))).sort()){
  const candidate=path.join(parent,name),item=await fs.lstat(candidate);if(!item.isDirectory()||item.isSymbolicLink()||item.uid!==process.getuid()||(item.mode&0o077))throw Error('Unsafe restore snapshot candidate');snapshots.push(candidate);
 }
 return {...record,file,snapshots};
}

export async function writeRestoreReference(directory,state,intent,snapshotDirectory){
 directory=await ownedDirectory(directory);
 if(!uuid.test(state?.ownerId??'')||state.projectName!==intent?.projectName||directory!==intent.directory||
    !safePath(snapshotDirectory)||path.dirname(snapshotDirectory)!==path.dirname(directory)||!snapshotDirectory.startsWith(intent.snapshotPrefix)||
    await ownedDirectory(snapshotDirectory)!==snapshotDirectory)throw Error('Restore snapshot reference identity is invalid');
 const persisted=await discoverRestoreIntent(intent.file);
 if(persisted.operationId!==intent.operationId||persisted.directory!==directory||persisted.preflightSha256!==intent.preflightSha256||persisted.snapshots.length!==1||persisted.snapshots[0]!==snapshotDirectory)throw Error('Restore intent or snapshot association is ambiguous');
 const record={schemaVersion:1,operationId:intent.operationId,projectName:state.projectName,ownerId:state.ownerId,directory,snapshotDirectory,intentFile:intent.file,preflightSha256:intent.preflightSha256};
 const file=path.join(directory,'restore-input-reference.json');await writeExclusive(file,record);
 await writeExclusive(path.join(snapshotDirectory,'restore-destination-reference.json'),record);return file;
}

async function readPrivateRecord(file){
 const before=await fs.lstat(file);if(!before.isFile()||before.isSymbolicLink()||before.uid!==process.getuid()||before.nlink!==1||(before.mode&0o077)||before.size>65536)throw Error('Unsafe restore record');
 const handle=await fs.open(file,constants.O_RDONLY|constants.O_NOFOLLOW);
 try{const opened=await handle.stat();if(['dev','ino','size','mtimeMs','ctimeMs'].some(key=>opened[key]!==before[key]))throw Error('Restore record changed');
  const record=parseRestoreManifest(new TextDecoder('utf-8',{fatal:true}).decode(await handle.readFile()));
  const after=await handle.stat(),named=await fs.lstat(file);if(['dev','ino','size','mtimeMs','ctimeMs'].some(key=>after[key]!==opened[key]||named[key]!==opened[key])||named.nlink!==1)throw Error('Restore record changed');return record;
 }finally{await handle.close();}
}
async function completedIntent(record){
 const directory=await ownedDirectory(record.directory);if(directory!==record.directory||record.snapshots.length!==1)throw Error('Restore intent is pending review');
 const reference=await readPrivateRecord(path.join(directory,'restore-input-reference.json'));
 const reverse=await readPrivateRecord(path.join(record.snapshots[0],'restore-destination-reference.json'));
 const complete=await readPrivateRecord(path.join(directory,'restore-complete-'+record.operationId+'.json'));
 const owner=await readPrivateRecord(path.join(directory,'owner.json'));
 const expected={schemaVersion:1,operationId:record.operationId,projectName:record.projectName,ownerId:owner.ownerId,directory,snapshotDirectory:record.snapshots[0],intentFile:record.file,preflightSha256:record.preflightSha256};
 if(owner.schemaVersion!==1||owner.projectName!==record.projectName||!uuid.test(owner.ownerId??'')||
    JSON.stringify(reference)!==JSON.stringify(expected)||JSON.stringify(reverse)!==JSON.stringify(expected)||
    JSON.stringify(complete)!==JSON.stringify({...expected,completed:true}))throw Error('Restore completion identity mismatch');
}
// Absence of a pending marker is not positive completion evidence. This
// verifies the original local intent, both references, owner and completion;
// it is not an independent anti-rollback or storage-integrity witness.
export async function readCompletedRestoreEvidence(directory){
 const target=await ownedDirectory(directory),pending=async()=>!!await fs.lstat(path.join(target,'restore-pending.json')).catch(error=>error.code==='ENOENT'?null:Promise.reject(error));
 if(await pending())throw Error('Restore is pending review');
 const reference=await readPrivateRecord(path.join(target,'restore-input-reference.json'));
 if(reference?.directory!==target||!uuid.test(reference.operationId??'')||!uuid.test(reference.ownerId??'')||
    !safePath(reference.intentFile)||path.dirname(reference.intentFile)!==path.dirname(target)||
    reference.intentFile!==path.join(path.dirname(target),path.basename(reference.intentFile)))throw Error('Restore reference intent path is invalid');
 const record=await discoverRestoreIntent(reference.intentFile);
 if(record.directory!==target||record.operationId!==reference.operationId)throw Error('Restore intent reference mismatch');
 await completedIntent(record);
 const after=await readPrivateRecord(path.join(target,'restore-input-reference.json'));
 if(JSON.stringify(after)!==JSON.stringify(reference))throw Error('Restore reference changed during review');
 if(await pending())throw Error('Restore is pending review');
 return {operationId:reference.operationId,projectName:reference.projectName,ownerId:reference.ownerId,directory:target,snapshotDirectory:reference.snapshotDirectory,intentFile:reference.intentFile,preflightSha256:reference.preflightSha256,trust:'LocalConsistencyOnly'};
}
export async function assertNoUnresolvedRestoreIntent(directory,{allowOperationId}={}){
 const resolved=path.resolve(directory);let parent;
 try{parent=await fs.realpath(path.dirname(resolved));}catch(error){if(error.code==='ENOENT')return;throw error;}
 const target=path.join(parent,path.basename(resolved));let names=await fs.readdir(parent);
 names=names.filter(name=>name.startsWith('.webapi-restore-intent-')).sort();if(names.length>4096)throw Error('Too many restore intents require review');
 let allowed=false;
 for(const name of names){const scoped=name.match(/^\.webapi-restore-intent-v2-([a-f0-9]{64})-[a-f0-9-]{36}\.json$/i);if(scoped&&scoped[1].toLowerCase()!==targetDigest(target))continue;
  const record=await discoverRestoreIntent(path.join(parent,name));if(record.directory!==target)continue;
  if(record.operationId===allowOperationId&&!allowed){allowed=true;continue;}
  try{await completedIntent(record);}catch{throw Error('Restore intent is pending review');}
 }
 if(allowOperationId&&!allowed)throw Error('Restore intent identity requires review');
}
export async function writeRestoreCompletion(directory,state,intent,snapshotDirectory){
 const persisted=await discoverRestoreIntent(intent.file);
 if(persisted.operationId!==intent.operationId||persisted.directory!==directory||persisted.snapshots.length!==1||persisted.snapshots[0]!==snapshotDirectory)throw Error('Restore completion intent mismatch');
 const reference=await readPrivateRecord(path.join(directory,'restore-input-reference.json'));
 const reverse=await readPrivateRecord(path.join(snapshotDirectory,'restore-destination-reference.json'));
 const expected={schemaVersion:1,operationId:intent.operationId,projectName:state.projectName,ownerId:state.ownerId,directory,snapshotDirectory,intentFile:intent.file,preflightSha256:intent.preflightSha256};
 if(JSON.stringify(reference)!==JSON.stringify(expected)||JSON.stringify(reverse)!==JSON.stringify(expected))throw Error('Restore completion reference mismatch');
 await writeExclusive(path.join(directory,'restore-complete-'+intent.operationId+'.json'),{...expected,completed:true});
}
