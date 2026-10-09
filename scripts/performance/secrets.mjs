import fs from 'node:fs/promises';
import path from 'node:path';
import {validateOwner} from './owner.mjs';
async function files(directory){
  const result=[];
  for(const entry of await fs.readdir(directory,{withFileTypes:true})){
    if(entry.isSymbolicLink())throw Error('Evidence/private directory contains a symbolic link');
    const full=path.join(directory,entry.name);
    if(entry.isDirectory())result.push(...await files(full));else if(entry.isFile())result.push(full);
  }
  return result;
}
export async function scanPublicEvidence(publicDirectory,privateDirectory){
  const values=new Set();
  for(const name of ['postgres-password','login-hmac','probe-token','admin-password','gateway-secret','rsa.pem','ec.pem']){
    try {const value=(await fs.readFile(path.join(privateDirectory,name),'utf8')).trim();if(value)values.add(value);}catch(error){if(error.code!=='ENOENT')throw error;}
  }
  const scales=path.join(privateDirectory,'scales');
  try {
    for(const file of await files(scales))if(path.basename(file)==='requests.json'){
      const requests=JSON.parse(await fs.readFile(file,'utf8'));
      for(const list of Object.values(requests))for(const request of list)for(const value of Object.values(request.headers??{})){
        if(value.startsWith('Bearer '))values.add(value.slice(7));
        if(value.startsWith('ak_')&&!value.endsWith('.invalid-secret')){values.add(value);values.add(value.slice(value.indexOf('.')+1));}
      }
    }
  }catch(error){if(error.code!=='ENOENT')throw error;}
  const findings=[];
  for(const file of await files(publicDirectory)){
    const value=await fs.readFile(file,'utf8');let count=0;
    for(const secret of values)if(value.includes(secret))count++;
    if(/Bearer [A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.|-----BEGIN (?:RSA |EC )?PRIVATE KEY-----|Password=[^;\s]+/i.test(value))count++;
    if(count)findings.push({file:path.relative(publicDirectory,file),count});
  }
  return {filesScanned:(await files(publicDirectory)).length,violations:findings.reduce((sum,f)=>sum+f.count,0),findings};
}
export async function removePrivateOwner(manifest,receipt){
  const directory=path.resolve(manifest.directory??'');
  if(!validateOwner(manifest,{'com.docker.compose.project':manifest.project,'com.webapi.performance.owner':manifest.owner})||
    receipt?.owner!==manifest.owner||receipt.project!==manifest.project||receipt.remaining!==0||path.basename(directory)!==manifest.project)
    throw Error('Private cleanup owner mismatch');
  const info=await fs.lstat(directory);if(!info.isDirectory()||info.isSymbolicLink()||await fs.realpath(directory)!==directory)throw Error('Invalid private owner directory');
  const ownerFile=path.join(directory,'owner.json');if((await fs.lstat(ownerFile)).isSymbolicLink())throw Error('Invalid owner manifest');
  const saved=JSON.parse(await fs.readFile(ownerFile,'utf8'));
  if(saved.owner!==manifest.owner||saved.project!==manifest.project||path.resolve(saved.directory)!==directory)throw Error('Private manifest owner mismatch');
  await fs.rm(directory,{recursive:true,force:false});
}
