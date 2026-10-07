import assert from 'node:assert/strict';
import path from 'node:path';import {validateFixture} from '../runtime/fixture-guard.mjs';import {assertOwnership} from '../runtime/docker.mjs';
const sha=/^[a-f0-9]{40}$/,image=/^sha256:[a-f0-9]{64}$/,hash=/^[a-f0-9]{64}$/;
export function assertReleaseIdentity({revision,release,actualImages}){
 if(!sha.test(revision)||release?.sourceRevision!==revision||!image.test(release.imageId)||!actualImages?.length||actualImages.some(x=>x.imageId!==release.imageId||x.sourceRevision!==revision))throw Error('固定提交、镜像或实际容器身份不一致。');return true;
}
export function assertRuleCoverage({catalog,executed,sourceRevision,logHash}){
 if(!sha.test(sourceRevision)||!hash.test(logHash)||!catalog?.length||!Array.isArray(executed))throw Error('规则覆盖来源不完整。');const ids=new Set();return catalog.map(rule=>{
  if(!rule.id||ids.has(rule.id)||!rule.testIds?.length)throw Error('规则覆盖目录不完整或重复。');ids.add(rule.id);const testIds=[...new Set(rule.testIds.flatMap(id=>executed.filter(value=>value===id||value.startsWith(id+'/'))))];
  if(rule.testIds.some(id=>!testIds.some(value=>value===id||value.startsWith(id+'/'))))throw Error('规则覆盖缺失：'+rule.id);return {ruleId:rule.id,sourceRevision,logHash,complete:true,registeredTestIds:rule.testIds,executedTestIds:testIds};
 });
}
export function assertPublicFiles(files,{secrets=[]}={}){
 if(!files?.length)throw Error('公开文件清单为空。');const seen=new Set();for(const file of files){
  const name=file.path;if(typeof name!=='string'||path.isAbsolute(name)||/[\\:\x00-\x1f]/.test(name)||name.split('/').some(x=>!x||x==='.'||x==='..')||seen.has(name)||file.type!=='file'||/(?:^|\/)(?:\.runtime|\.secrets|secrets|node_modules|\.git)(?:\/|$)/i.test(name)||/(?:password|client-secret|consumer-api-key|postgres\.dump|\.tar$)/i.test(name))throw Error('公开文件路径、类型或凭据边界不合格。');seen.add(name);
  const content=String(file.content??'');if(secrets.some(value=>typeof value==='string'&&value.length>=8&&content.includes(value)))throw Error('公开文件含实际私密值。');
  if(name.startsWith('docs/')&&(/\/Users\/|\/private\/|\.runtime\//.test(content)||/\b(?:password|api[_-]?key|client[_-]?secret)\s*[:=]\s*["']?[A-Za-z0-9_+\/-]{12,}/i.test(content)))throw Error('公开报告含私有路径或明文凭据。');
 }return true;
}
export function validateCloneOwnership(state,resources,directory){
 validateFixture(state.projectName,directory);if(!/^[a-f0-9-]{36}$/.test(state.ownerId)||![state.ports?.console,state.ports?.gatewayA,state.ports?.gatewayB].every(value=>Number.isInteger(value)&&value>1024&&value<=65535&&![4180,4181,4192,4193,4194,4196,4197,5090].includes(value))||new Set(Object.values(state.ports)).size!==3)throw Error('克隆归属或端口不安全。');if(!resources.length)throw Error('克隆资源归属尚未建立。');assertOwnership(state,resources);return true;
}
export function validateContractAcceptance(proof,{revision,imageId}){
 if(proof.sourceRevision!==revision||proof.imageId!==imageId||!sha.test(revision)||!image.test(imageId)||proof.immutableBuild!==true||!proof.clone?.actual||!proof.clone?.ownerVerified||!proof.clone?.restored)throw Error('缺少固定提交构建或实际恢复克隆。');
 if(!proof.ruleCoverage?.length||proof.ruleCoverage.some(rule=>!rule.complete||rule.sourceRevision!==revision))throw Error('规则覆盖未全部完成。');
 const required=Array.from({length:15},(_,i)=>'C'+String(i+1).padStart(2,'0'));const checks=proof.checks??[];if(checks.length!==15||new Set(checks.map(c=>c.id)).size!==15||required.some(id=>!checks.some(c=>c.id===id&&c.status==='Passed'&&c.sourceRevision===revision&&c.kind==='actual'&&c.evidence?.length)))throw Error('验收项失败、模拟或未执行。');
 if(proof.ui?.kind!=='cua'||proof.ui.actual!==true||proof.ui.sourceRevision!==revision||![1440,1280].every(width=>proof.ui.widths?.includes(width)))throw Error('缺少实际双宽度CUA操作验收。');return {sourceRevision:revision,imageId,passed:true,failed:[],notExecuted:[],checks,ruleCoverage:proof.ruleCoverage};
}
export function passedTestIds(log){
 const result=new Set();for(const line of log.split('\n')){const match=line.match(/^\s*Passed\s+(WebApi\.[^\s(]+)/);if(!match)continue;result.add(match[1].split('.').at(-1));const id=line.match(/\bid: "([^"\\]+)"/),direction=line.match(/\bdirection: "(request|response)"/);if(id)result.add(id[1]+(direction?'/'+direction[1]:''));}return [...result].sort();
}

export function assertMaterialEvidence(proof,files){
 const revision=proof.sourceRevision,imageId=proof.imageId;
 assert(sha.test(revision)&&image.test(imageId),'材料身份不合法。');
 const indexed=new Map(files.map(file=>[file.path,file]));assert.equal(indexed.size,files.length,'证据路径重复。');
 function read(file){assert(typeof file==='string'&&indexed.has(file),'缺少内部证据材料。');const entry=indexed.get(file);assert.equal(entry.type,'file');return JSON.parse(String(entry.content));}
 const required=['domain','integration','gateway','console','runtime-contracts'];
 assert.equal(proof.suites?.length,required.length,'必须是五套独立测试。');
 assert.equal(new Set(proof.suites.map(x=>x.name)).size,required.length,'测试套件名称重复。');
 assert.equal(new Set(proof.suites.map(x=>x.artifact)).size,required.length,'测试套件不能共用材料。');
 let domain;
 for(const name of required){
  const wrapper=proof.suites.find(x=>x.name===name);assert(wrapper,'缺少测试套件：'+name);
  const actual=read(wrapper.artifact);
  for(const key of ['name','sourceRevision','passed','failed','skipped','exitCode','originalLogSha256'])assert.deepEqual(actual[key],wrapper[key],'测试材料与摘要不一致：'+name+'/'+key);
  assert.equal(actual.sourceRevision,revision);assert.equal(actual.exitCode,0);assert.equal(actual.failed,0);assert.equal(actual.skipped,0);
  assert(Number.isSafeInteger(actual.passed)&&actual.passed>0&&hash.test(actual.originalLogSha256),'测试结果或日志身份缺失。');
  assert(Array.isArray(actual.passedTestLines),'缺少真实通过记录。');
  if(name==='domain'){assert.equal(actual.passedTestLines.filter(line=>/^\s*Passed\s+WebApi\./.test(line)).length,actual.passed,'Domain详细通过记录数量不一致。');domain=actual;}
 }
 const refs=proof.materialArtifacts;assert(refs,'缺少分类材料引用。');
 const catalog=read(refs.catalog);assert.equal(catalog.sourceRevision,revision,'规则目录来自旧提交。');
 const actualCoverage=read(refs.ruleCoverage);
 const expected=assertRuleCoverage({catalog:catalog.catalog?.rules,executed:passedTestIds(domain.passedTestLines.join('\n')),sourceRevision:revision,logHash:domain.originalLogSha256});
 assert.deepEqual(actualCoverage,expected,'规则覆盖与真实通过记录不一致。');assert.deepEqual(proof.ruleCoverage,expected,'规则覆盖摘要与材料不一致。');
 const ui=read(refs.ui);assert.deepEqual(ui,proof.ui,'CUA摘要与真实材料不一致。');
 assert.equal(ui.sourceRevision,revision);assert.equal(ui.kind,'cua');assert.equal(ui.actual,true);
 assert([1440,1280].every(width=>ui.widths?.includes(width))&&Array.isArray(ui.actions)&&ui.actions.length>0&&Array.isArray(ui.screenshots)&&ui.screenshots.length>0,'CUA实际操作材料不完整。');
 assert(ui.screenshots.every(file=>indexed.has(file)&&file.endsWith('.png')),'CUA截图缺失。');
 const scenario=read(refs.scenario);assert.equal(scenario.sourceRevision,revision);assert.equal(scenario.imageId,imageId);assert.equal(scenario.actual,true);assert.equal(scenario.complete,true);
 for(const name of ['baseline-published','review-two-level-publish','rollback'])assert(scenario.steps?.some(step=>step.name===name),'真实场景未完整执行：'+name);
 const protection=read(refs.protection);assert.equal(protection.sourceRevision,revision);assert.equal(protection.originalPreserved,true);
 assert(protection.backup?.complete&&protection.backup.hashReadback&&protection.backup.originalServicesRestored,'冷备或原服务恢复未完成。');
 assert(protection.restore?.complete&&protection.restore.actual&&protection.restore.restored&&protection.restore.ownerVerified,'实际恢复材料不完整。');
 assert.equal(protection.software?.complete,true);assert.equal(protection.software.sourceRevision,revision);assert.equal(protection.software.imageId,imageId);
 return true;
}
