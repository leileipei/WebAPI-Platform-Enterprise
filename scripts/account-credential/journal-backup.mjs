import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {safeFile,writePrivate} from '../runtime/state.mjs';
import {assertAccountDeploymentCompatibility} from './deployment-guard.mjs';

const exec=promisify(execFile),digest=bytes=>createHash('sha256').update(bytes).digest('hex');
const maxCheckpoint=128*1024*1024+20000*1024+2*1024*1024;
const uuid=/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/;
function keys(value,names){assert(value&&typeof value==='object'&&!Array.isArray(value));assert.deepEqual(Object.keys(value).sort(),[...names].sort(),'Unknown or missing journal fields');}
function integer(value){assert(JSON.isRawJSON(value)&&/^[1-9][0-9]{0,18}$/.test(value.rawJSON),'Invalid journal numeric token');const n=BigInt(value.rawJSON);assert(n<=9223372036854775807n,'Journal integer exceeds Int64');return n;}
function utc(value){assert(typeof value==='string'&&/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?Z$/.test(value)&&Number.isFinite(Date.parse(value)),'Invalid journal UTC');}
function ticks(value){utc(value);const match=/^(.*?)(?:\.(\d{1,7}))?Z$/.exec(value);return BigInt(Date.parse(match[1]+'Z'))*10000n+BigInt((match[2]??'').padEnd(7,'0'));}
function unique(raw){const stack=[];for(const match of raw.matchAll(/"(?:\\[\s\S]|[^"\\])*"|[{}\[\],:]/g)){const token=match[0];if(token==='{'||token==='['){stack.push({object:token==='{',key:token==='{',names:new Set()});assert(stack.length<=16,'Journal depth exceeds limit');}else if(token==='}'||token===']')stack.pop();else if(token===','&&stack.at(-1)?.object)stack.at(-1).key=true;else if(token.startsWith('"')&&stack.at(-1)?.object&&stack.at(-1).key){const name=JSON.parse(token),frame=stack.at(-1);assert(!frame.names.has(name),'Duplicate journal property');frame.names.add(name);frame.key=false;}}}

// This validates the exact canonical bytes written by UsageJournal, without parsing any
// Int64 through JS Number or reserializing its checksum input. It is not node authentication.
export function inspectUsageCheckpoint(bytes) {
  assert(Buffer.isBuffer(bytes)&&bytes.length>0&&bytes.length<=maxCheckpoint);
  const raw=new TextDecoder('utf-8',{fatal:true}).decode(bytes);unique(raw);
  const value=JSON.parse(raw,(_key,v,context)=>typeof v==='number'?JSON.rawJSON(context.source):v);
  keys(value,['format','identity','writerInstanceId','checkpoint','checksum']);
  assert.equal(integer(value.format),1n);assert.match(value.checksum,/^[a-f0-9]{64}$/);
  const suffix=',"checksum":"'+value.checksum+'"}';assert(raw.endsWith(suffix),'Journal must use canonical checksum layout');
  assert.equal(digest(Buffer.from(raw.slice(0,-suffix.length)+'}')),value.checksum,'Journal checksum mismatch');
  const identity=value.identity,c=value.checkpoint;
  keys(identity,['environmentId','nodeName','controlPlane','enrollmentHash']);
  assert(uuid.test(identity.environmentId)&&uuid.test(value.writerInstanceId));
  assert(typeof identity.nodeName==='string'&&identity.nodeName.length>0&&identity.nodeName.length<=128);
  assert.match(identity.enrollmentHash,/^[a-f0-9]{64}$/);
  const url=new URL(identity.controlPlane);assert(['http:','https:'].includes(url.protocol));assert.equal(url.username+url.password+url.search+url.hash,'');
  keys(c,['environmentId','nodeName','collectorId','nextSequence','durableThrough','active','pending','gaps']);
  assert.equal(c.environmentId,identity.environmentId);assert.equal(c.nodeName,identity.nodeName);assert(uuid.test(c.collectorId));
  const next=integer(c.nextSequence);utc(c.durableThrough);
  assert(Array.isArray(c.active)&&c.active.length<=20000&&Array.isArray(c.pending)&&Array.isArray(c.gaps));
  assert(Buffer.byteLength(JSON.stringify(c.gaps))<=1024*1024);
  let previous=0n;
  for(const batch of c.pending){assert.equal(batch.collectorId,c.collectorId);assert(uuid.test(batch.producerInstanceId));assert.equal(batch.protocol,'1.0');const sequence=integer(batch.sequence);assert(sequence>previous&&sequence<next,'Invalid pending journal order');previous=sequence;utc(batch.sealedAt);utc(batch.durableThrough);assert(Array.isArray(batch.items)&&batch.items.length<=1000);for(const item of batch.items)integer(item.count);}
  for(const observation of c.active){assert.equal(observation.environmentId,c.environmentId);integer(observation.item?.count);}
  for(const gap of c.gaps){assert(ticks(gap.from)<ticks(gap.to));assert(typeof gap.reason==='string'&&gap.reason.length>0);}
  return {sha256:digest(bytes),environmentId:c.environmentId,nodeName:c.nodeName,collectorId:c.collectorId,nextSequence:c.nextSequence.rawJSON,pendingBatches:c.pending.length,activeKeys:c.active.length};
}

export async function inspectUsageBackups(directory,volumes) {
  assert(Array.isArray(volumes)&&volumes.length<=32);
  const result={schemaVersion:1,volumes:{}};
  for(const name of volumes.filter(v=>/^(?:lkg-[ab]|delivery-lkg-[ab]|pipeline-stage-[1-8]-lkg-[ab])$/.test(v))) {
    const archive=path.join(directory,name+'.tar');await safeFile(archive);
    const stat=await fs.lstat(archive);assert(stat.isFile()&&!stat.isSymbolicLink()&&stat.size<=1024*1024*1024,'Invalid LKG backup archive');
    const options={env:{...process.env,LC_ALL:'C'},maxBuffer:2*1024*1024};
    const names=(await exec('tar',['-tf',archive],options)).stdout.trimEnd().split('\n').filter(Boolean);
    assert(names.length<=5000&&names.every(n=>!/[\x00-\x1f\x7f\\]/.test(n)&&!path.isAbsolute(n)&&!n.split('/').includes('..')),'Invalid LKG archive path');
    const entries=(await exec('tar',['-tvf',archive],options)).stdout.trimEnd().split('\n').filter(Boolean);
    assert.equal(entries.length,names.length,'Ambiguous LKG archive entries');
    assert(entries.every(n=>n.startsWith('-')||n.startsWith('d')),'LKG archive contains a link or non-regular entry');
    assert.equal(new Set(names).size,names.length,'Duplicate LKG archive entry');
    const checkpoints=names.filter(n=>path.posix.normalize(n)==='credential-usage/checkpoint.json');
    assert(checkpoints.length<=1);
    if(!checkpoints.length){result.volumes[name]={status:'legacy-or-no-journal'};continue;}
    const bytes=(await exec('tar',['-xOf',archive,'--',checkpoints[0]],{...options,encoding:'buffer',maxBuffer:maxCheckpoint+1024})).stdout;
    result.volumes[name]={status:'present',checkpoint:inspectUsageCheckpoint(bytes)};
  }
  return result;
}

export async function writeUsageBackupInventory(directory,volumes) {
  const inventory=await inspectUsageBackups(directory,volumes),name='usage-journal-inventory.json';
  await writePrivate(path.join(directory,name),inventory);
  return {path:name,sha256:digest(await fs.readFile(path.join(directory,name)))};
}

export async function verifyUsageBackupInventory(directory,volumes,record) {
  const actual=await inspectUsageBackups(directory,volumes);
  if(!record)return actual; // Older backups still have every present journal validated.
  assert.equal(record.path,'usage-journal-inventory.json');assert.match(record.sha256,/^[a-f0-9]{64}$/);
  const file=path.join(directory,record.path);await safeFile(file);assert((await fs.stat(file)).size<=256*1024);
  const bytes=await fs.readFile(file);assert.equal(digest(bytes),record.sha256,'Usage inventory checksum mismatch');
  assert.deepEqual(JSON.parse(bytes),actual,'Usage inventory differs from archived queues');
  return actual;
}

export function requireSafeUsageCloneRestore(inventory,capability) {
  assert.equal(inventory.schemaVersion,1);assert(inventory.volumes&&typeof inventory.volumes==='object');
  for(const value of Object.values(inventory.volumes)) {
    if(value.status==='legacy-or-no-journal')continue;
    assert.equal(value.status,'present');
    assert(capability,'Usage queue clone requires verified per-owner collector rebinding; retain the private backup and do not replay it into another owner');
    assertAccountDeploymentCompatibility({...capability,hasLifecycleSchema:true});
    assert(capability.buildIdentity.features.includes('credential-usage-clone-rebind-v1'),'Usage clone requires compatible collector rebinding');
  }
  return true;
}

export function usageRestoreVolumeNames(manifest,requiredVolumes) {
  assert(manifest.files&&typeof manifest.files==='object','Missing hashed backup manifest');
  for(const name of requiredVolumes.filter(n=>n.includes('lkg-'))) {
    assert(/^(?:lkg-[ab]|delivery-lkg-[ab]|pipeline-stage-[1-8]-lkg-[ab])$/.test(name),'Invalid required usage volume');
    assert(/^[a-f0-9]{64}$/.test(manifest.files[name+'.tar']??''),'Required LKG volume missing from hashed manifest');
  }
  return Object.keys(manifest.files).filter(n=>/^(?:lkg-[ab]|delivery-lkg-[ab]|pipeline-stage-[1-8]-lkg-[ab])\.tar$/.test(n)).map(n=>n.slice(0,-4)).sort();
}
