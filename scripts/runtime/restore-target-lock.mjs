import fs from 'node:fs/promises';
import {constants} from 'node:fs';
import path from 'node:path';
import {createHash,randomUUID} from 'node:crypto';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';

const run=promisify(execFile);
const safePath=value=>typeof value==='string'&&path.isAbsolute(value)&&!/[\u0000\r\n]/.test(value);
const heldLocks=new WeakMap();
export async function syncDirectory(directory){const handle=await fs.open(directory,'r');try{await handle.sync();}finally{await handle.close();}}
export async function cleanupDirectoryIdentity(directory){
 const info=await fs.lstat(directory).catch(error=>error.code==='ENOENT'?null:Promise.reject(error));
 if(!info)return null;if(!info.isDirectory()||info.isSymbolicLink())throw Error('Cleanup directory identity is unsafe');return {dev:info.dev,ino:info.ino};
}
export async function assertCleanupDirectoryIdentity(directory,expected){
 const current=await cleanupDirectoryIdentity(directory);
 if(Boolean(current)!==Boolean(expected)||current&&(current.dev!==expected.dev||current.ino!==expected.ino))throw Error('Cleanup directory identity changed');
}
export async function cleanupParentWritable(directory){
 const parent=await fs.realpath(path.dirname(path.resolve(directory)));
 try{await fs.access(parent,constants.W_OK);return true;}catch(error){if(['EACCES','EPERM'].includes(error.code))return false;throw error;}
}

// Sync every new directory entry before placing durable recovery evidence in it.
export async function ensureDirectoryDurable(directory){
 directory=path.resolve(directory);if(!safePath(directory))throw Error('Invalid restore parent directory');
 const missing=[];let current=directory;
 for(;;){try{const info=await fs.stat(current);if(!info.isDirectory())throw Error('Unsafe restore parent directory');break;}
  catch(error){if(error.code!=='ENOENT')throw error;const parent=path.dirname(current);if(parent===current)throw error;missing.unshift(path.basename(current));current=parent;}}
 current=await fs.realpath(current);
 for(const name of missing){const next=path.join(current,name);try{await fs.mkdir(next,{mode:0o700});await syncDirectory(current);}
  catch(error){if(error.code!=='EEXIST')throw error;const info=await fs.lstat(next);if(!info.isDirectory()||info.isSymbolicLink())throw Error('Unsafe restore parent directory');}
  current=next;}
 return current;
}

async function rejectGrantingAcl(directory){
 let result;
 if(process.platform==='darwin'){
  result=await run('/bin/ls',['-lde','--',directory],{maxBuffer:65536});
  const lines=result.stdout.trimEnd().split('\n');
  if(!lines.length||!/^d/.test(lines[0])||lines.slice(1).some(line=>!/^\s*\d+:\s+.+\sdeny\s/.test(line)))throw Error('Restore parent ACL cannot be trusted');
 }else if(process.platform==='linux'){
  let executable;for(const candidate of ['/usr/bin/getfacl','/bin/getfacl']){try{await fs.access(candidate,constants.X_OK);executable=candidate;break;}catch(error){if(error.code!=='ENOENT')throw error;}}
  if(!executable)throw Error('Restore parent ACL inspection is unavailable');
  result=await run(executable,['-cp','--',directory],{maxBuffer:65536});
  if(result.stdout.split('\n').some(line=>line&&!/^\s*(?:#|user::|group::|other::)/.test(line)))throw Error('Restore parent ACL cannot be trusted');
 }else throw Error('Restore parent ACL inspection is unavailable');
}

// Root and the current user are trusted; sticky ancestors may be writable only
// if the protected child is owned by one of them. ACL grants fail closed.
export async function assertTrustedRestoreParent(parent,{privateParent=true,protectedChild}={}){
 if(!safePath(parent)||await fs.realpath(parent)!==parent)throw Error('Unsafe restore parent identity');
 let current=parent,child=null;
 if(!privateParent&&protectedChild){if(await fs.realpath(path.dirname(path.resolve(protectedChild)))!==parent)throw Error('Cleanup parent identity changed');child=await fs.lstat(protectedChild).catch(error=>error.code==='ENOENT'?null:Promise.reject(error));if(child&&(!child.isDirectory()||child.isSymbolicLink()))throw Error('Cleanup directory identity is unsafe');}
 for(;;){const info=await fs.lstat(current);
  if(!info.isDirectory()||info.isSymbolicLink()||![0,process.getuid()].includes(info.uid))throw Error('Unsafe restore parent owner');
  if(current===parent&&privateParent&&(info.uid!==process.getuid()||(info.mode&0o077)))throw Error('Restore parent permissions are unsafe');
  if((info.mode&0o022)&&(!(info.mode&0o1000)||!child||![0,process.getuid()].includes(child.uid)))throw Error('Unsafe writable restore ancestor');
  await rejectGrantingAcl(current);
  const next=path.dirname(current);if(next===current)break;child=info;current=next;
 }
}

// Lock names bind the canonical target, even through a symlinked ancestor.
// No stale-lock cleanup is automatic: a crash retains its private record.
export class RestoreTargetLockHeldError extends Error{constructor(){super('Restore target lock requires review');this.code='RESTORE_TARGET_LOCK_HELD';}}
export async function withRestoreTargetLock(directory,action,{token,retainOnError=false,forceParent=false}={}){
 const resolved=path.resolve(directory),parent=await ensureDirectoryDurable(path.dirname(resolved));
 const target=path.join(parent,path.basename(resolved));
 if(!safePath(target))throw Error('Invalid restore target');
 if(token){if(heldLocks.get(token)!==target)throw Error('Restore target lock identity mismatch');return action(target,token);}
 const digest=createHash('sha256').update(target).digest('hex');
 const name='.webapi-restore-target-lock-'+digest+'.json',parentFile=path.join(parent,name),targetFile=path.join(target,name);
 const present=async file=>!!await fs.lstat(file).catch(error=>error.code==='ENOENT'?null:Promise.reject(error));
 const targetInfo=await fs.lstat(target).catch(error=>error.code==='ENOENT'?null:Promise.reject(error));
 if(targetInfo&&(!targetInfo.isDirectory()||targetInfo.isSymbolicLink()||targetInfo.uid!==process.getuid()||(targetInfo.mode&0o077)))throw Error('Runtime target must be owned, private and not a symlink');
 if(targetInfo&&await present(forceParent?targetFile:parentFile))throw new RestoreTargetLockHeldError();
 const lockDirectory=targetInfo&&!forceParent?target:parent,file=path.join(lockDirectory,name);let handle,created,primary,result,cleanupFailure,actionStarted=false,failed=false,cleanupFailed=false;
 try{handle=await fs.open(file,constants.O_WRONLY|constants.O_CREAT|constants.O_EXCL|constants.O_NOFOLLOW,0o600);}
 catch(error){if(error.code==='EEXIST')throw new RestoreTargetLockHeldError();throw error;}
 try{
  await handle.writeFile(JSON.stringify({schemaVersion:1,directory:target,pid:process.pid,nonce:randomUUID()})+'\n');await handle.sync();created=await handle.stat();await syncDirectory(lockDirectory);
  if(lockDirectory===target&&await present(parentFile))throw new RestoreTargetLockHeldError();
  if(lockDirectory===parent){const currentTarget=await fs.lstat(target).catch(error=>error.code==='ENOENT'?null:Promise.reject(error));
   if(currentTarget&&(!currentTarget.isDirectory()||currentTarget.isSymbolicLink()||currentTarget.uid!==process.getuid()||(currentTarget.mode&0o077)))throw Error('Runtime target must be owned, private and not a symlink');
   if(currentTarget&&await present(targetFile))throw new RestoreTargetLockHeldError();}
  const grant=Object.freeze({});heldLocks.set(grant,target);
  try{actionStarted=true;result=await action(target,grant);}finally{heldLocks.delete(grant);}
 }catch(error){failed=true;primary=error;}
 try{
  if(created&&!(failed&&retainOnError&&actionStarted)){
   const current=await fs.lstat(file);if(current.dev!==created.dev||current.ino!==created.ino||current.nlink!==1)throw Error('Restore target lock identity changed');
   // The protected action is already durable before release. An unsynced
   // unlink may leave a conservative stale lock after power loss, but avoids
   // reporting a failed action after its barrier has already disappeared.
   await handle.close();handle=null;await fs.unlink(file);
  }
 }catch(error){cleanupFailed=true;cleanupFailure=error;}
 try{if(handle)await handle.close();}catch(error){if(!cleanupFailed){cleanupFailed=true;cleanupFailure=error;}}
 if(failed){if(cleanupFailed){let attached=false;if(primary&&typeof primary==='object')try{primary.cleanupFailure=cleanupFailure;attached=primary.cleanupFailure===cleanupFailure;}catch{}if(!attached)throw new AggregateError([primary,cleanupFailure],'Restore action and lock cleanup both failed');}throw primary;}
 if(cleanupFailed)throw cleanupFailure;
 return result;
}
