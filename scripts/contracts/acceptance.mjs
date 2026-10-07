import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {loadState} from '../runtime/state.mjs';
import {loadRelease} from '../runtime/lifecycle.mjs';
import {inspectResources,docker} from '../runtime/docker.mjs';
import {assertReleaseIdentity,assertPublicFiles,validateCloneOwnership,validateContractAcceptance,assertMaterialEvidence} from './evidence.mjs';

const json=async file=>JSON.parse(await fs.readFile(file,'utf8'));
const digest=value=>createHash('sha256').update(value).digest('hex');

// This gate consumes evidence from actual executions. It never manufactures
// browser actions, successful tests, restored volumes or installation status.
export async function runContractAcceptance({revision,directory,cloneDirectory}){
 const state=await loadState(cloneDirectory);
 const resources=await inspectResources(state);
 validateCloneOwnership(state,resources,cloneDirectory);
 const release=await loadRelease(path.join(cloneDirectory,'release.json'));
 const apps=resources.filter(r=>r.Kind==='container'&&['control-plane','worker','console','gateway-a','gateway-b'].includes(r.Config?.Labels?.['com.docker.compose.service']));
 assert.equal(apps.length,5,'必须存在五个应用容器。');
 assert(apps.every(app=>app.State.Running),'应用容器必须运行。');
 const actualImages=[];
 for(const app of apps){const image=JSON.parse((await docker(['image','inspect',app.Image])).stdout)[0];actualImages.push({imageId:image.Id,sourceRevision:image.Config.Labels?.['org.opencontainers.image.revision']});}
 assertReleaseIdentity({revision,release,actualImages});
 const proof=await json(path.join(directory,'acceptance-input.json'));
 assert.equal(proof.sourceRevision,revision);
 assert.equal(proof.imageId,release.imageId);
 const files=[];
 for(const artifact of proof.artifacts??[]){
  assertPublicFiles([{path:artifact.path,type:'file'}]);
  const publicRoot=path.join(directory,'public');
  for(const parts of [[],...artifact.path.split('/').map((_,i,all)=>all.slice(0,i+1))])assert(!(await fs.lstat(path.join(publicRoot,...parts))).isSymbolicLink(),'证据目录不能包含符号链接。');
  const file=path.join(publicRoot,artifact.path),stat=await fs.lstat(file);
  assert(stat.isFile()&&!stat.isSymbolicLink(),'证据必须是普通文件。');
  const content=await fs.readFile(file);
  assert.equal(digest(content),artifact.sha256,'证据摘要不一致：'+artifact.path);
  files.push({path:artifact.path,type:'file',content});
 }
 assertPublicFiles(files,{secrets:proof.privateValues??[]});
 const paths=new Set(files.map(file=>file.path));
 for(const check of proof.checks??[])assert(check.evidence?.length&&check.evidence.every(file=>paths.has(file)),'验收引用缺少实际证据。');
 assert(proof.ui?.screenshots?.length&&proof.ui.screenshots.every(file=>paths.has(file)),'CUA截图缺失。');
 assert(proof.suites?.length>=5&&proof.suites.every(suite=>suite.sourceRevision===revision&&suite.exitCode===0&&suite.failed===0&&suite.skipped===0&&suite.passed>0&&paths.has(suite.artifact)),'本提交测试尚未完整通过。');
 assertMaterialEvidence(proof,files);
 const result=validateContractAcceptance(proof,{revision,imageId:release.imageId});
 return {...result,stage:'pre-install',originalInstallation:'not-upgraded',suites:proof.suites,ui:proof.ui,clone:proof.clone,artifacts:proof.artifacts};
}

if(process.argv[1]===import.meta.filename){
 const [revision,directory,cloneDirectory]=process.argv.slice(2);
 if(!revision||!directory||!cloneDirectory)throw Error('需要固定提交、私有证据目录及自有克隆目录。');
 const result=await runContractAcceptance({revision,directory,cloneDirectory});
 console.log(JSON.stringify(result,null,2));
}
