import fs from 'node:fs/promises';
import {constants} from 'node:fs';

const maxBytes=4*1024*1024;
const same=(a,b)=>['dev','ino','size','mtimeNs','ctimeNs'].every(key=>a[key]===b[key]);

export function parseRestoreManifest(raw){
 if(typeof raw!=='string'||Buffer.byteLength(raw)>maxBytes)throw new Error('Restore manifest exceeds the bounded format');
 const stack=[];
 for(const match of raw.matchAll(/"(?:\\[\s\S]|[^"\\])*"|[{}\[\],:]/g)){
  const token=match[0];
  if(token==='{'||token==='['){stack.push({object:token==='{',key:token==='{',names:new Set()});if(stack.length>32)throw new Error('Restore manifest nesting exceeds the limit');}
  else if(token==='}'||token===']')stack.pop();
  else if(token===','&&stack.at(-1)?.object)stack.at(-1).key=true;
  else if(token.startsWith('"')&&stack.at(-1)?.object&&stack.at(-1).key){
   const name=JSON.parse(token),frame=stack.at(-1);if(frame.names.has(name))throw new Error('Restore manifest has duplicate keys');frame.names.add(name);frame.key=false;
  }
 }
 return JSON.parse(raw);
}

export async function readRestoreManifest(file){
 const before=await fs.lstat(file,{bigint:true});
 if(!before.isFile()||before.isSymbolicLink()||before.uid!==BigInt(process.getuid())||before.nlink!==1n||
    (before.mode&0o077n)!==0n||before.size>BigInt(maxBytes))throw new Error('Restore manifest file is unsafe');
 const handle=await fs.open(file,constants.O_RDONLY|constants.O_NOFOLLOW);
 try{
  const opened=await handle.stat({bigint:true});if(!same(before,opened))throw new Error('Restore manifest file changed');
  const bytes=await handle.readFile(),after=await handle.stat({bigint:true}),named=await fs.lstat(file,{bigint:true});
  if(!same(opened,after)||!same(opened,named))throw new Error('Restore manifest file changed');
  return parseRestoreManifest(new TextDecoder('utf-8',{fatal:true}).decode(bytes));
 }finally{await handle.close();}
}
