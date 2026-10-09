import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash} from 'node:crypto';
export async function retainSqlAudit(source,destination){
  const root=await fs.lstat(source);if(!root.isDirectory()||root.isSymbolicLink()||(root.mode&0o777)!==0o700)throw Error('SQL audit source is not private');
  const templates=[];
  for(const name of await fs.readdir(source)){
    if(!/^[a-f0-9]{64}\.sql$/.test(name))throw Error('Invalid SQL audit template name');
    const file=path.join(source,name),info=await fs.lstat(file);
    if(!info.isFile()||info.isSymbolicLink()||(info.mode&0o777)!==0o600||info.size>1024*1024)throw Error('SQL audit file is not private');
    const bytes=await fs.readFile(file);if(createHash('sha256').update(bytes).digest('hex')!==name.slice(0,-4))throw Error('SQL audit template hash mismatch');
    templates.push({name,bytes});
  }
  if(!templates.length)throw Error('SQL audit contains no actual templates');
  await fs.mkdir(destination,{mode:0o700});
  for(const {name,bytes} of templates)await fs.writeFile(path.join(destination,name),bytes,{mode:0o600,flag:'wx'});
  return {templates:templates.length,complete:true,hashes:templates.map(t=>t.name.slice(0,-4)).sort()};
}
