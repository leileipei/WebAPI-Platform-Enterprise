import fs from 'node:fs/promises';
import {constants} from 'node:fs';
import {createHash} from 'node:crypto';

const blockSize=512;
const maxArchiveBytes=2*1024*1024*1024;
// The journal quota reserves attempt records; its separate configuration can
// add at most one further record to the archive.
const maxDataBytes=1024*1024*1024+65536;
// The journal permits 131072 directory entries; tar adds the root directory.
const maxEntries=131073;
const maxRecordBytes=65536;
const bad=()=>new Error('Migration evidence archive is unsafe or incomplete');
const same=(a,b)=>a.dev===b.dev&&a.ino===b.ino&&a.size===b.size&&a.mtimeNs===b.mtimeNs&&a.ctimeNs===b.ctimeNs;

async function openPrivateArchive(file){
  const before=await fs.lstat(file,{bigint:true});
  if(!before.isFile()||before.isSymbolicLink()||before.uid!==BigInt(process.getuid())||before.nlink!==1n||
     (before.mode&0o077n)!==0n||before.size<1024n||before.size>BigInt(maxArchiveBytes)||before.size%512n!==0n)throw bad();
  const handle=await fs.open(file,constants.O_RDONLY|constants.O_NOFOLLOW);
  try{
    const opened=await handle.stat({bigint:true});if(!same(before,opened))throw bad();
    return {handle,opened};
  }catch(error){await handle.close();throw error;}
}
async function verifyUnchanged(file,handle,opened){
  const after=await handle.stat({bigint:true}),current=await fs.lstat(file,{bigint:true});
  if(!same(opened,after)||!same(opened,current))throw bad();
}

// Hash incrementally; an evidence quota of up to 1 GiB must not become an
// equally large managed allocation during backup or restore.
export async function hashMigrationEvidenceArchive(file){
  const {handle,opened}=await openPrivateArchive(file);
  try{
    const hash=createHash('sha256'),buffer=Buffer.alloc(65536),size=Number(opened.size);let offset=0;
    while(offset<size){
      const part=await handle.read(buffer,0,Math.min(buffer.length,size-offset),offset);
      if(part.bytesRead<=0)throw bad();
      hash.update(buffer.subarray(0,part.bytesRead));offset+=part.bytesRead;
    }
    await verifyUnchanged(file,handle,opened);
    return hash.digest('hex');
  }finally{await handle.close();}
}

function octal(header,start,length){
  const value=header.subarray(start,start+length).toString('ascii').replace(/\0.*$/s,'').trim();
  if(!/^[0-7]+$/.test(value))throw bad();
  const parsed=Number.parseInt(value,8);
  if(!Number.isSafeInteger(parsed))throw bad();
  return parsed;
}
function text(header,start,length){
  const bytes=header.subarray(start,start+length),zero=bytes.indexOf(0);
  if(zero>=0&&bytes.subarray(zero).some(value=>value!==0))throw bad();
  return bytes.subarray(0,zero<0?bytes.length:zero).toString('utf8');
}

// Validates a bounded, uncompressed GNU/ustar archive before any extraction.
// Checksums are corruption detection, not a signature or a continuity witness.
export async function validateMigrationEvidenceArchive(file){
  const {handle,opened}=await openPrivateArchive(file);
  try{
    const size=Number(opened.size),names=new Set();let offset=0,files=0,starts=0,total=0,zeroBlocks=0;
    while(offset<size){
      const header=Buffer.alloc(blockSize);const read=await handle.read(header,0,blockSize,offset);
      if(read.bytesRead!==blockSize)throw bad();offset+=blockSize;
      if(header.every(value=>value===0)){
        zeroBlocks=1;
        while(offset<size){
          const tail=Buffer.alloc(Math.min(65536,size-offset));const part=await handle.read(tail,0,tail.length,offset);
          if(part.bytesRead!==tail.length||tail.some(value=>value!==0))throw bad();
          offset+=tail.length;zeroBlocks+=tail.length/blockSize;
        }
        break;
      }
      if(names.size>=maxEntries||header.subarray(257,262).toString('ascii')!=='ustar')throw bad();
      const expected=octal(header,148,8);let checksum=0;
      for(let i=0;i<blockSize;i++)checksum+=(i>=148&&i<156)?32:header[i];
      if(checksum!==expected||text(header,345,155)!==''||text(header,157,100)!=='')throw bad();
      const name=text(header,0,100),mode=octal(header,100,8),uid=octal(header,108,8),gid=octal(header,116,8),length=octal(header,124,12),kind=header[156];
      if(names.has(name)||uid!==10001||gid!==10001)throw bad();names.add(name);
      if(name==='./'){
        if(kind!==53||mode!==0o700||length!==0)throw bad();
      }else{
        if(!/^\.\/(?:\.journal\.(?:lock|config)|[a-f0-9]{32}\.(?:start|migration|bootstrap|seed)\.json)$/.test(name)||
           !(kind===0||kind===48)||mode!==0o600||length>maxRecordBytes)throw bad();
        if(name==='./.journal.lock'&&length!==0)throw bad();
        if(name.endsWith('.start.json'))starts++;
        files++;total+=length;if(total>maxDataBytes)throw bad();
      }
      offset+=Math.ceil(length/blockSize)*blockSize;
      if(offset>size)throw bad();
    }
    if(zeroBlocks<2||!names.has('./')||!names.has('./.journal.lock')||!names.has('./.journal.config')||starts===0)throw bad();
    await verifyUnchanged(file,handle,opened);
    return {files,attemptStarts:starts,dataBytes:total};
  }finally{await handle.close();}
}
