import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {createHash} from 'node:crypto';
import {build} from './runner-support.mjs';

test('two_source_builds_keep_both_images_addressable_after_tag_replacement_and_reclamation',async()=>{
 const root=await fs.mkdtemp(path.join(os.tmpdir(),'performance-build-pair-'));
 const owner='d09a7407-3d06-45be-932b-5037422a89d2',manifest={owner,project:'webapi-performance-'+owner};
 const tags=new Map(),images=new Map();
 const run=async args=>{
  if(args[0]==='volume'&&args[1]==='inspect')return {stdout:'[]',stderr:''};
  if(args[0]==='run'){
   const mount=args.find(a=>a.startsWith('type=bind,src='));assert.ok(mount);
   const context=mount.slice('type=bind,src='.length).split(',dst=')[0];
   await fs.writeFile(path.join(context,'publish','WebApi.PerformanceHost.dll'),args.at(-1));
   return {stdout:'SDK boundary completed',stderr:''};
  }
  if(args[0]==='build'){
   const source=args.find(a=>a.startsWith('SOURCE_REVISION=')).slice('SOURCE_REVISION='.length);
   const harness=args.find(a=>a.startsWith('HARNESS_REVISION=')).slice('HARNESS_REVISION='.length);
   const id='sha256:'+createHash('sha256').update(source+':'+harness).digest('hex');
   const tag=args[args.indexOf('--tag')+1],replaced=tags.get(tag);tags.set(tag,id);
   images.set(id,{Id:id,Config:{Labels:{'org.opencontainers.image.revision':source,'com.webapi.performance.harness':harness},User:'10001'}});
   // Reclamation of an unreferenced image must not destroy the first variant.
   if(replaced&&![...tags.values()].includes(replaced))images.delete(replaced);
   return {stdout:'Image boundary completed',stderr:''};
  }
  if(args[0]==='image'&&args[1]==='inspect'){
   const image=images.get(tags.get(args[2])??args[2]);if(!image)throw Error('No such local image');
   return {stdout:JSON.stringify([image]),stderr:''};
  }
  throw Error('Unexpected build boundary');
 };
 try{
  const built=[];
  for(const [variant,source,harness] of [['baseline','b'.repeat(40),'d'.repeat(40)],['candidate','c'.repeat(40),'d'.repeat(40)],['changed-harness','b'.repeat(40),'e'.repeat(40)]]){
   const context=path.join(root,variant),directory=path.join(context,'source'),output=path.join(root,'output-'+variant);
   await fs.mkdir(path.join(directory,'deploy/performance'),{recursive:true});await fs.mkdir(output);
   await fs.writeFile(path.join(directory,'deploy/performance/Dockerfile'),'FROM scratch\n');
   const identity={source,harness};
   built.push({identity,result:await build(manifest,directory,identity,output,run)});
  }
  assert.equal(new Set(built.map(b=>b.result.imageId)).size,built.length);
  for(const {identity,result} of built){
   const [actual]=JSON.parse((await run(['image','inspect',result.imageId])).stdout);
   assert.equal(actual.Config.Labels['org.opencontainers.image.revision'],identity.source);
   assert.equal(actual.Config.Labels['com.webapi.performance.harness'],identity.harness);
   assert.ok(Object.keys(result.assemblies).includes('WebApi.PerformanceHost.dll'));
  }
 }finally{await fs.rm(root,{recursive:true,force:true});}
});
