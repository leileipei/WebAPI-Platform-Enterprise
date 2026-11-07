import fs from 'node:fs/promises';
import {constants} from 'node:fs';
import {createHash,randomUUID} from 'node:crypto';
import path from 'node:path';
import {ownedDirectory} from './state.mjs';
import {hashMigrationEvidenceArchive} from './migration-evidence-archive.mjs';

const maximum=2n*1024n*1024n*1024n;
const same=(a,b)=>a.dev===b.dev&&a.ino===b.ino&&a.size===b.size&&a.mtimeNs===b.mtimeNs&&a.ctimeNs===b.ctimeNs;

// The copy is a new private, non-overwriting input. Its digest and identity
// still need to be rechecked before/after extraction; it is not a signature.
export async function freezeMigrationEvidenceArchive(source,directory,expectedHash){
 if(!path.isAbsolute(source)||/[\u0000\r\n,]/.test(source)||!path.isAbsolute(directory)||
    !/^[a-f0-9]{64}$/.test(expectedHash??''))throw Error('Invalid migration evidence input identity');
 directory=await ownedDirectory(directory);
 const before=await fs.lstat(source,{bigint:true});
 if(!before.isFile()||before.isSymbolicLink()||before.uid!==BigInt(process.getuid())||before.nlink!==1n||
    (before.mode&0o077n)!==0n||before.size>maximum)throw Error('Unsafe migration evidence source or link');
 const input=await fs.open(source,constants.O_RDONLY|constants.O_NOFOLLOW);
 let output,created=false;
 const target=path.join(directory,'migration-evidence-input-'+randomUUID()+'.tar');
 try{
  const opened=await input.stat({bigint:true});if(!same(before,opened))throw Error('Migration evidence source changed');
  output=await fs.open(target,constants.O_WRONLY|constants.O_CREAT|constants.O_EXCL|constants.O_NOFOLLOW,0o600);created=true;
  const buffer=Buffer.alloc(65536),digest=createHash('sha256');let position=0;
  while(BigInt(position)<opened.size){
   const part=await input.read(buffer,0,Math.min(buffer.length,Number(opened.size-BigInt(position))),position);
   if(part.bytesRead<=0)throw Error('Migration evidence source changed');
   digest.update(buffer.subarray(0,part.bytesRead));let written=0;
   while(written<part.bytesRead){const n=await output.write(buffer,written,part.bytesRead-written,position+written);if(n.bytesWritten<=0)throw Error('Migration evidence copy failed');written+=n.bytesWritten;}
   position+=part.bytesRead;
  }
  const after=await input.stat({bigint:true}),named=await fs.lstat(source,{bigint:true});
  if(!same(opened,after)||!same(opened,named))throw Error('Migration evidence source changed');
  const actual=digest.digest('hex');
  if(actual!==expectedHash)throw Error('Migration evidence archive hash mismatch');
  await output.sync();await output.chmod(0o400);await output.sync();
  const identity=await output.stat({bigint:true});
  return {path:target,sha256:actual,identity:Object.fromEntries(['dev','ino','size','mtimeNs','ctimeNs'].map(key=>[key,identity[key].toString()]))};
 }finally{
  if(output){try{await output.sync();}catch{/* A failed sync remains an unverified retained copy. */}await output.close();}
  await input.close();
  if(created){const parent=await fs.open(directory,'r');try{await parent.sync();}finally{await parent.close();}}
 }
}

export async function verifyFrozenMigrationEvidenceArchive(frozen){
 if(!frozen||!path.isAbsolute(frozen.path??'')||!/^migration-evidence-input-[a-f0-9-]{36}\.tar$/.test(path.basename(frozen.path))||
    !/^[a-f0-9]{64}$/.test(frozen.sha256??'')||!frozen.identity)throw Error('Invalid frozen migration evidence identity');
 await ownedDirectory(path.dirname(frozen.path));
 const before=await fs.lstat(frozen.path,{bigint:true});
 if(!before.isFile()||before.isSymbolicLink()||before.uid!==BigInt(process.getuid())||before.nlink!==1n||
    (before.mode&0o777n)!==0o400n||['dev','ino','size','mtimeNs','ctimeNs'].some(key=>before[key].toString()!==frozen.identity[key]))
  throw Error('Frozen migration evidence identity changed');
 if(await hashMigrationEvidenceArchive(frozen.path)!==frozen.sha256)throw Error('Frozen migration evidence hash changed');
 const after=await fs.lstat(frozen.path,{bigint:true});
 if(!same(before,after))throw Error('Frozen migration evidence changed');
}
