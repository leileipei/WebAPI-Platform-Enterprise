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
