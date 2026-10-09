import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID,randomBytes} from 'node:crypto';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
const exec=promisify(execFile);
export function validateOwner(manifest,labels){
  return /^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/.test(manifest?.owner??'')&&
    manifest.project===`webapi-performance-${manifest.owner}`&&
    labels?.['com.docker.compose.project']===manifest.project&&labels?.['com.webapi.performance.owner']===manifest.owner;
}
export async function createOwner(root){
  const owner=randomUUID(),project=`webapi-performance-${owner}`;
  const directory=path.join(await fs.realpath(root),project);
  await fs.mkdir(directory,{recursive:true,mode:0o700});await fs.chmod(directory,0o700);
  for(const name of ['postgres-password','login-hmac','probe-token','admin-password','gateway-secret'])
    await fs.writeFile(path.join(directory,name),randomBytes(32).toString(name==='login-hmac'?'base64':'base64url'),{mode:0o600,flag:'wx'});
  const manifest={schemaVersion:1,owner,project,directory,createdAt:new Date().toISOString()};
  await fs.writeFile(path.join(directory,'owner.json'),JSON.stringify(manifest,null,2)+'\n',{mode:0o600,flag:'wx'});
  return manifest;
}
export async function cleanupOwned(manifest,composeFile,run=exec){
  if(!validateOwner(manifest,{'com.docker.compose.project':manifest.project,'com.webapi.performance.owner':manifest.owner}))throw Error('Invalid performance owner');
  const resources=[];
  for(const kind of ['container','volume','network']){
    const args=kind==='container'?['ps','-aq','--filter',`label=com.docker.compose.project=${manifest.project}`]:[kind,'ls','-q','--filter',`label=com.docker.compose.project=${manifest.project}`];
    const names=(await run('docker',args)).stdout.trim().split(/\s+/).filter(Boolean);
    for(const name of names){
      const info=JSON.parse((await run('docker',[kind,'inspect',name])).stdout)[0];
      const labels=kind==='container'?info.Config.Labels:info.Labels;
      if(!validateOwner(manifest,labels))throw Error(`Cleanup blocked: ownership mismatch (${kind})`);
      resources.push({kind,name});
    }
  }
  const variables=Object.fromEntries(['PERF_IMAGE','PERF_SUBNET','PERF_RESULT_DIRECTORY','PERF_HARNESS_DIRECTORY'].map(name=>[name,manifest.composeEnvironment?.[name]??process.env[name]??'']));
  if(resources.length){
    if(!composeFile)throw Error('Owned resources require a verified compose configuration for cleanup');
    await run('docker',['compose','-p',manifest.project,'-f',composeFile,'down','--volumes','--remove-orphans'],{env:{...process.env,...variables,PERF_OWNER:manifest.owner,PERF_DIRECTORY:manifest.directory},maxBuffer:4*1024*1024});
  }
  const remaining=[];
  for(const kind of ['container','volume','network']){
    const args=kind==='container'?['ps','-aq','--filter',`label=com.docker.compose.project=${manifest.project}`]:[kind,'ls','-q','--filter',`label=com.docker.compose.project=${manifest.project}`];
    if((await run('docker',args)).stdout.trim())remaining.push(kind);
  }
  if(remaining.length)throw Error('Performance owner resources remain');
  return {owner:manifest.owner,project:manifest.project,resources:resources.map(x=>({kind:x.kind})),remaining:0};
}
