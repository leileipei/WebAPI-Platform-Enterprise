import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {assertFixedAccountSources} from './fixed-source.mjs';
import {readOwnedRuntime,readPrivateArtifact} from './scenario.mjs';
import {validateAccountCredentialEvidence,digest} from './evidence.mjs';
import {ownedDirectory,writePrivate} from '../runtime/state.mjs';

const services=['control-plane','worker','console','gateway-a','gateway-b','prod-gateway-a','prod-gateway-b'];
function ownedApplications(resources,state,imageId) {
  return services.map(service=>{
    const matches=resources.filter(r=>r.Kind==='container'&&r.Config?.Labels?.['com.docker.compose.service']===service);
    assert.equal(matches.length,1,'Missing or duplicate live service '+service);
    const app=matches[0];
    assert.equal(app.Image,imageId);assert.equal(app.State?.Running,true,'Stopped live service');
    assert.equal(app.Config.Labels['com.webapi.runtime.owner'],state.ownerId);
    assert.equal(app.Config.Labels['com.docker.compose.project'],state.projectName);
    const networks=Object.keys(app.NetworkSettings?.Networks??{}).sort();
    assert(networks.includes(state.projectName+'_default'));
    assert(networks.every(n=>n.startsWith(state.projectName+'_')),'Foreign live network');
    return {service,id:app.Id,image:app.Image,networks,bindings:app.HostConfig?.PortBindings??{}};
  });
}

export function assertLiveAccountMaterial({state,actual,captured,imageId,actualStatics,capturedStatics}) {
  assert.deepEqual(ownedApplications(captured,state,imageId),ownedApplications(actual,state,imageId),'Captured containers do not match live owner');
  assert(Object.keys(actualStatics).length>0,'Empty static inventory');
  assert.deepEqual(capturedStatics.expected,actualStatics,'Static inventory differs from fixed release');
  assert.deepEqual(capturedStatics.actual,actualStatics,'Captured static resources differ from live responses');
  return true;
}

export async function runAccountCredentialAcceptance({revision,imageId,directory,fixtureDirectory,privateValues=[]}) {
  await assertFixedAccountSources(revision);
  await ownedDirectory(directory);
  const {state,release,resources}=await readOwnedRuntime({revision,imageId,fixtureDirectory});
  const origin=JSON.parse(await readPrivateArtifact(fixtureDirectory,'empty-origin.json'));
  for(const [key,value] of Object.entries({sourceRevision:revision,imageId,ownerId:state.ownerId,projectName:state.projectName}))assert.equal(origin[key],value);
  assert.equal(origin.initializedEmpty,true);assert.equal(origin.restored,false);assert.deepEqual(origin.resourcesBefore,[]);
  const input=await readPrivateArtifact(directory,'acceptance-input.json'),proof=JSON.parse(input);
  assert.deepEqual(proof.runtime.state,state);
  assert.equal(await fs.realpath(proof.runtime.directory),await fs.realpath(fixtureDirectory));
  const values=JSON.parse(await readPrivateArtifact(directory,'private-values.json'));
  assert(Array.isArray(values)&&values.length>0&&values.every(v=>typeof v==='string'&&v.length>=8));
  assert(Array.isArray(proof.artifacts)&&proof.artifacts.length>0&&proof.artifacts.length<=2000);
  const publicDirectory=await fs.realpath(path.join(directory,'public')),files=[];
  assert(!(await fs.lstat(path.join(directory,'public'))).isSymbolicLink(),'Symlink public evidence root');
  let total=0;
  for(const ref of proof.artifacts) {
    const content=await readPrivateArtifact(publicDirectory,ref.path);total+=content.length;
    assert(total<=256*1024*1024,'Evidence size exceeds bounded input');
    files.push({path:ref.path,type:'file',content});
  }
  const read=name=>JSON.parse(String(files.find(f=>f.path===name)?.content??''));
  const captured=read(proof.material.images),capturedStatics=read(proof.material.staticFiles),actualStatics={};
  for(const [name,expected] of Object.entries(release.staticFiles??{})) {
    assert(/^console\/dist\/[a-zA-Z0-9_./-]+$/.test(name)&&!name.split('/').includes('..'),'Invalid static release path');
    const response=await fetch('http://127.0.0.1:'+state.ports.console+'/'+name.slice('console/dist/'.length),{redirect:'error',signal:AbortSignal.timeout(10000)});
    assert.equal(response.status,200,'Fixed Console asset unavailable');
    const chunks=[];let size=0;
    for await(const chunk of response.body){size+=chunk.length;assert(size<=16*1024*1024,'Static resource exceeds limit');chunks.push(chunk);}
    actualStatics[name]=digest(Buffer.concat(chunks));assert.equal(actualStatics[name],expected,'Live Console differs from fixed release');
  }
  assertLiveAccountMaterial({state,actual:resources,captured:captured.resources,imageId,actualStatics,capturedStatics});
  const result=validateAccountCredentialEvidence(proof,files,{revision,imageId,privateValues:[...privateValues,...values]});
  await writePrivate(path.join(directory,'acceptance-result.json'),{...result,actualContainers:ownedApplications(resources,state,imageId),staticHashes:actualStatics,proofSha256:digest(input),validatedAt:new Date().toISOString()});
  return result;
}

if(process.argv[1]===import.meta.filename) {
  const [revision,imageId,directory,fixtureDirectory]=process.argv.slice(2);
  console.log(JSON.stringify(await runAccountCredentialAcceptance({revision,imageId,directory,fixtureDirectory}),null,2));
}
