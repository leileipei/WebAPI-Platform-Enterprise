import {createHash} from 'node:crypto';
import {validateOwner} from './owner.mjs';
export const quotas=Object.freeze({
  'gateway-a':{cpus:2,memoryBytes:2*1024**3},'gateway-b':{cpus:2,memoryBytes:2*1024**3},
  controlplane:{cpus:2,memoryBytes:2*1024**3},backend:{cpus:1,memoryBytes:256*1024**2},
  postgres:{cpus:2,memoryBytes:3*1024**3},redis:{cpus:0.5,memoryBytes:256*1024**2},client:{cpus:2,memoryBytes:1024**3}
});
export function verifyEnvironment(manifest,containers,host){
  const services={};
  for(const item of containers){
    if(!validateOwner(manifest,item.labels))throw Error('Environment owner mismatch');
    const service=item.labels['com.docker.compose.service'],expected=quotas[service];
    if(!expected||services[service])throw Error('Unknown or duplicate environment service');
    if(item.nanoCpus!==expected.cpus*1e9||item.memoryBytes!==expected.memoryBytes)throw Error('Actual container quota mismatch: '+service);
    if(!/^sha256:[a-f0-9]{64}$/.test(item.imageId??''))throw Error('Immutable runtime image identity missing');
    services[service]={imageId:item.imageId,cpus:item.nanoCpus/1e9,memoryBytes:item.memoryBytes,fixtureUid:item.user};
  }
  if(Object.keys(services).length!==Object.keys(quotas).length)throw Error('Environment service missing');
  const configuration={services,host,gc:{server:true,heapHardLimitPercent:70},pool:{maximum:32},database:{maxConnections:100,sharedBuffers:'512MB',workMem:'16MB',maintenanceWorkMem:'256MB',parallelWorkersPerGather:0,timezone:'UTC'},telemetry:false};
  // Key order is fixed above and service order is sorted for variant comparison.
  configuration.services=Object.fromEntries(Object.entries(services).sort(([a],[b])=>a.localeCompare(b)));
  // Variant application images necessarily differ; quota/host/config identity must not.
  const comparison={...configuration,services:Object.fromEntries(Object.entries(configuration.services).map(([name,value])=>[name,{...value,imageId:['controlplane','gateway-a','gateway-b','backend'].includes(name)?'variant-application':value.imageId}]))};
  return {...configuration,fingerprint:createHash('sha256').update(JSON.stringify(comparison)).digest('hex'),verified:true};
}
