import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import {randomBytes,createHash} from 'node:crypto';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {validGuid,validateState,ownedDirectory,safeFile,writePrivate,RuntimeError} from '../runtime/state.mjs';
import {inspectResources,assertOwnership} from '../runtime/docker.mjs';
const exec=promisify(execFile),digest=b=>createHash('sha256').update(b).digest('hex');
export const notificationStateName='notification-runtime.json';
export const notificationVolumes=['notification-secrets','notification-fixture-secrets','notification-fixture-data'];
const receiptName='notification-secrets/receipt.json';
const expectedSecretFiles=['sender/smtp.json','sender/webhook.json','sender/ca.crt','fixture/smtp.json','fixture/webhook.json','fixture/server.crt','fixture/server.key','fixture/owner-token'].sort();
function identity(owner,projectName){if(!validGuid(owner)||!(projectName==='webapi-enterprise-local'||/^webapi-enterprise-local-test-[a-f0-9-]{36}$/.test(projectName)))throw new RuntimeError('Invalid notification owner.',3);}
export function validateNotificationRuntime(state,core){
 validateState(core);identity(state?.ownerId,state?.projectName);
 if(state.schemaVersion!==1||state.ownerId!==core.ownerId||state.projectName!==core.projectName||Object.keys(state).some(k=>!['schemaVersion','ownerId','projectName','enabled','fixtureEnabled','ports','secretReceipt'].includes(k))||typeof state.enabled!=='boolean'||typeof state.fixtureEnabled!=='boolean'||state.secretReceipt!==receiptName||state.fixtureEnabled&&!state.enabled)throw new RuntimeError('Invalid notification sidecar identity/schema.',3);
 const ports=Object.values(state.ports??{});if(Object.keys(state.ports??{}).sort().join(',')!=='https,management,smtp'||ports.some(p=>!Number.isInteger(p)||p<1025||p>65535)||new Set(ports).size!==3||ports.some(p=>Object.values(core.ports).includes(p)))throw new RuntimeError('Invalid notification ports.',3);
 validateState({...core,ports:{console:state.ports.https,gatewayA:state.ports.management,gatewayB:core.ports.console}});return state;
}
export async function loadNotificationRuntime(directory,core){
 directory=await ownedDirectory(directory);const file=path.join(directory,notificationStateName);
 try{await safeFile(file);}catch(e){if(e.code==='ENOENT')return null;throw e;}
 return validateNotificationRuntime(JSON.parse(await fs.readFile(file,'utf8')),core);
}
export async function loadNotificationSecrets(directory,owner,projectName){
 identity(owner,projectName);await ownedDirectory(directory);const file=path.join(directory,'receipt.json');if((await safeFile(file)).size>16384)throw new RuntimeError('Notification receipt too large.',3);const receipt=JSON.parse(await fs.readFile(file,'utf8'));
 if(receipt.schemaVersion!==1||receipt.ownerId!==owner||receipt.projectName!==projectName||receipt.provider!=='LocalFileMapping'||!receipt.files||Object.keys(receipt.files).sort().join(',')!==expectedSecretFiles.join(','))throw new RuntimeError('Notification secret receipt owner mismatch.',3);
 for(const [name,hash]of Object.entries(receipt.files)){if(!/^(sender|fixture)\/[a-z0-9.-]+$/.test(name)||!/^([a-f0-9]{64})$/.test(hash))throw new RuntimeError('Invalid notification secret receipt.',3);await ownedDirectory(path.join(directory,path.dirname(name)));if((await safeFile(path.join(directory,name))).size>16384)throw new RuntimeError('Notification secret too large.',3);if(digest(await fs.readFile(path.join(directory,name)))!==hash)throw new RuntimeError('Notification secret changed without an owned receipt.',3);}return receipt;
}
export async function prepareNotificationSecrets({directory,owner,projectName}){
 identity(owner,projectName);let exists=true;try{await fs.lstat(directory);}catch(e){if(e.code!=='ENOENT')throw e;exists=false;}if(exists)return loadNotificationSecrets(directory,owner,projectName);
 await ownedDirectory(path.dirname(directory));await fs.mkdir(directory,{mode:0o700});for(const role of ['sender','fixture'])await fs.mkdir(path.join(directory,role),{mode:0o700});
 const temp=path.join(directory,'certificate-build');await fs.mkdir(temp,{mode:0o700});
 try{
  await exec('openssl',['req','-x509','-newkey','rsa:2048','-nodes','-keyout',path.join(temp,'ca.key'),'-out',path.join(temp,'ca.crt'),'-days','730','-subj','/CN=WebAPI Owned Local Fixture CA','-addext','basicConstraints=critical,CA:TRUE']);
  await exec('openssl',['req','-new','-newkey','rsa:2048','-nodes','-keyout',path.join(temp,'server.key'),'-out',path.join(temp,'server.csr'),'-subj','/CN=notification-fixture']);
  await writePrivate(path.join(temp,'extensions'),'subjectAltName=DNS:notification-fixture\nbasicConstraints=critical,CA:FALSE\nextendedKeyUsage=serverAuth\nkeyUsage=digitalSignature,keyEncipherment\n');
  await exec('openssl',['x509','-req','-in',path.join(temp,'server.csr'),'-CA',path.join(temp,'ca.crt'),'-CAkey',path.join(temp,'ca.key'),'-CAserial',path.join(temp,'ca.srl'),'-CAcreateserial','-out',path.join(temp,'server.crt'),'-days','365','-extfile',path.join(temp,'extensions')]);
  const smtp={username:'fixture-'+owner,password:randomBytes(32).toString('base64')},webhook={keyBase64:randomBytes(32).toString('base64')};
  for(const role of ['sender','fixture']){await writePrivate(path.join(directory,role,'smtp.json'),smtp);await writePrivate(path.join(directory,role,'webhook.json'),webhook);}
  await writePrivate(path.join(directory,'sender','ca.crt'),await fs.readFile(path.join(temp,'ca.crt'),'utf8'));
  await writePrivate(path.join(directory,'fixture','server.crt'),await fs.readFile(path.join(temp,'server.crt'),'utf8'));await writePrivate(path.join(directory,'fixture','server.key'),await fs.readFile(path.join(temp,'server.key'),'utf8'));
  await writePrivate(path.join(directory,'fixture','owner-token'),randomBytes(32).toString('base64'));
  const files={};for(const role of ['sender','fixture'])for(const name of await fs.readdir(path.join(directory,role)))files[role+'/'+name]=digest(await fs.readFile(path.join(directory,role,name)));
  const receipt={schemaVersion:1,ownerId:owner,projectName,provider:'LocalFileMapping',files};await writePrivate(path.join(directory,'receipt.json'),receipt);return receipt;
 }finally{await fs.rm(temp,{recursive:true,force:true});}
}
export async function assertNotificationPorts(state,core,{inspect=inspectResources}={}){
 validateNotificationRuntime(state,core);const resources=await inspect(core);assertOwnership(core,resources);
 const owned=new Set(resources.flatMap(r=>Object.values(r.NetworkSettings?.Ports??{}).flatMap(v=>v??[]).filter(p=>p.HostIp==='127.0.0.1').map(p=>Number(p.HostPort))));
 for(const port of Object.values(state.ports)){if(owned.has(port))continue;await new Promise((resolve,reject)=>{const server=net.createServer();server.once('error',()=>reject(new RuntimeError('Notification port '+port+' is unavailable; no owner was stopped.',3)));server.listen(port,'127.0.0.1',()=>server.close(resolve));});}
}
export async function copyNotificationBackup(directory,target,core){
 const n=await loadNotificationRuntime(directory,core);if(!n)return {};const source=path.join(directory,'notification-secrets');await loadNotificationSecrets(source,core.ownerId,core.projectName);await ownedDirectory(target);await fs.cp(source,path.join(target,'notification-secrets'),{recursive:true,errorOnExist:true,force:false});await writePrivate(path.join(target,notificationStateName),n);const files={};for(const name of [notificationStateName,receiptName,...expectedSecretFiles.map(f=>'notification-secrets/'+f)])files[name]=digest(await fs.readFile(path.join(target,name)));return files;
}
export async function restoreNotificationBackup(target,directory,core,ports){
 const previous=JSON.parse(await fs.readFile(path.join(target,'runtime.json'),'utf8'));const n=await loadNotificationRuntime(target,previous);if(!n)return null;const receipt=await loadNotificationSecrets(path.join(target,'notification-secrets'),previous.ownerId,previous.projectName);
 if(!ports)throw new RuntimeError('Explicit independent restore notification ports required.',3);
 await ownedDirectory(directory);const next=validateNotificationRuntime({...n,ownerId:core.ownerId,projectName:core.projectName,ports},core);await fs.cp(path.join(target,'notification-secrets'),path.join(directory,'notification-secrets'),{recursive:true,errorOnExist:true,force:false});await writePrivate(path.join(directory,receiptName),{...receipt,ownerId:core.ownerId,projectName:core.projectName});await writePrivate(path.join(directory,notificationStateName),next);return next;
}
