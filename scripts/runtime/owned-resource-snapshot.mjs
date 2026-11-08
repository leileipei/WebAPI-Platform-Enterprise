import {createHash} from 'node:crypto';
import {assertOwnership} from './docker.mjs';

// Only identities and state are returned; arbitrary Docker labels/options are
// hashed rather than copied to review evidence.
export function ownedResourceSnapshot(state,resources){
 if(!Array.isArray(resources))throw Error('Resource inventory is unavailable');
 assertOwnership(state,resources);const seen=new Set();
 const items=resources.map(resource=>{
  if(!['container','volume','network'].includes(resource.Kind))throw Error('Unexpected resource kind');
  const labels=resource.Labels??resource.Config?.Labels??{},id=resource.Kind==='volume'?resource.Name:resource.Id;
  if(typeof id!=='string'||!id||id.length>256)throw Error('Resource identity is unavailable');
  if(resource.Kind==='container'&&typeof resource.State?.Running!=='boolean')throw Error('Container state is unavailable');
  const key=resource.Kind+':'+id;if(seen.has(key))throw Error('Duplicate resource identity');seen.add(key);
  if(resource.Kind==='volume'&&(!id.startsWith(state.projectName+'_')||!/^[a-z0-9-]+$/.test(id.slice(state.projectName.length+1))))throw Error('Unexpected volume identity');
  const optionEntries=Object.entries(resource.Options??{}).sort(([a],[b])=>a.localeCompare(b));
  const mounts=Array.isArray(resource.Mounts)?resource.Mounts.map(m=>({type:m.Type??null,name:m.Name??null,source:m.Source??null,destination:m.Destination??null,readWrite:m.RW??null})).sort((a,b)=>String(a.destination).localeCompare(String(b.destination))):[];
  return {kind:resource.Kind,id,projectName:state.projectName,ownerId:state.ownerId,
   volumeNonce:labels['com.webapi.restore.creation-nonce']??null,evidenceNonce:labels['com.webapi.migration.evidence-nonce']??null,
   volumeKind:labels['com.docker.compose.volume']??null,driver:resource.Driver??null,scope:resource.Scope??null,
   createdAt:resource.CreatedAt??resource.Created??null,imageId:resource.Kind==='container'?resource.Image??null:null,
   mountsSha256:createHash('sha256').update(JSON.stringify(mounts)).digest('hex'),
   mountpointSha256:typeof resource.Mountpoint==='string'?createHash('sha256').update(resource.Mountpoint).digest('hex'):null,
   optionsSha256:createHash('sha256').update(JSON.stringify(optionEntries)).digest('hex'),
   running:resource.Kind==='container'?resource.State?.Running??null:null};
 }).sort((a,b)=>a.kind.localeCompare(b.kind)||a.id.localeCompare(b.id));
 return items;
}
export function ownedResourceDigest(state,resources){return createHash('sha256').update(JSON.stringify(ownedResourceSnapshot(state,resources))).digest('hex');}
