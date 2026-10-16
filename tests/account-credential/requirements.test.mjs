import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,mkdtempSync,writeFileSync,symlinkSync,rmSync,unlinkSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {createHash} from 'node:crypto';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {validateRequirementsCloseout} from '../../scripts/account-credential/requirements.mjs';
const source=JSON.parse(readFileSync(new URL('../../docs/evidence/work-package4/2026-10-15-readiness/original-40-page-requirements.json',import.meta.url)));
const revision='a'.repeat(40),owner='work-package4-test-owner',at='2026-10-15T01:00:00Z';
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const preserved=({id,page,kind,originalRequirement,originalParagraphIndexZeroBased})=>({id,page,kind,originalRequirement,originalParagraphIndexZeroBased});
function sample(){return {schemaVersion:1,originalDocumentSha256:source.originalDocument.sha256,requirements:source.requirements.map(u=>({...preserved(u),status:'待复验',reason:'当前版本尚无本轮完整运行证据',implementationPaths:[],testNames:[],evidenceRefs:[],sourceRevision:null,runtimeOwner:null,verifiedAt:null}))};}
function closed(){
 const closeout=sample(),unit=closeout.requirements[0];
 const implementation=Buffer.from('export const login = true;');
 const tests=Buffer.from(JSON.stringify({kind:'tests',sourceRevision:revision,runtimeOwner:owner,verifiedAt:at,testNames:['PasswordLifecycleFlow'],passed:1,failed:0,skipped:0}));
 const runtime=Buffer.from(JSON.stringify({kind:'runtime',sourceRevision:revision,runtimeOwner:owner,verifiedAt:at,requirementIds:[unit.id],scenario:'P4-01',result:'passed'}));
 const ui=Buffer.from(JSON.stringify({kind:'ui',sourceRevision:revision,runtimeOwner:owner,verifiedAt:at,requirementIds:[unit.id],viewport:{width:1440,height:900},screenshots:[{path:'login.png',sha256:hash(Buffer.from('image'))}]}));
 const artifacts=new Map([['console/src/login.ts',{bytes:implementation}],['tests.json',{bytes:tests}],['runtime.json',{bytes:runtime}],['ui.json',{bytes:ui}],['login.png',{bytes:Buffer.from('image')}]]);
 Object.assign(unit,{status:'已关闭',reason:'固定版本账号流程已验证',implementationPaths:[{path:'console/src/login.ts',sha256:hash(implementation)}],testNames:['PasswordLifecycleFlow'],evidenceRefs:['tests.json','runtime.json','ui.json'].map(path=>({path,sha256:hash(artifacts.get(path).bytes)})),sourceRevision:revision,runtimeOwner:owner,verifiedAt:at});
 return {closeout,artifacts};
}
test('all_original_units_are_preserved_once',()=>{const result=validateRequirementsCloseout(source,sample(),new Map());assert.equal(result.pages,40);assert.equal(result.units,454);assert.equal(result.statusCounts['待复验'],454);});
test('missing duplicate changed text and foreign page are rejected',()=>{
 for(const change of [c=>c.requirements.pop(),c=>c.requirements.push(c.requirements[0]),c=>c.requirements[0].originalRequirement='changed',c=>c.requirements[0].page=2,c=>c.requirements[0].originalParagraphIndexZeroBased++]){const c=sample();change(c);assert.throws(()=>validateRequirementsCloseout(source,c,new Map()));}
});
test('closed_requires_matching_source_test_and_runtime_evidence',()=>{const {closeout,artifacts}=closed();const result=validateRequirementsCloseout(source,closeout,artifacts);assert.equal(result.statusCounts['已关闭'],1);assert.equal(result.statusCounts['待复验'],453);});
test('closed without evidence or with stale owner revision skipped test is rejected',()=>{
 for(const change of [c=>c.evidenceRefs=[],c=>c.testNames=['OtherTest'],c=>c.sourceRevision='b'.repeat(40),c=>c.runtimeOwner='other-owner',c=>c.implementationPaths=[],c=>c.verifiedAt=null]){const {closeout,artifacts}=closed();change(closeout.requirements[0]);assert.throws(()=>validateRequirementsCloseout(source,closeout,artifacts));}
 for(const field of ['failed','skipped']){const {closeout,artifacts}=closed();const json=JSON.parse(artifacts.get('tests.json').bytes);json[field]=1;const bytes=Buffer.from(JSON.stringify(json));artifacts.set('tests.json',{bytes});closeout.requirements[0].evidenceRefs.find(x=>x.path==='tests.json').sha256=hash(bytes);assert.throws(()=>validateRequirementsCloseout(source,closeout,artifacts));}
});
test('missing files altered content stale screenshots and missing runtime scenario are rejected',()=>{
 for(const path of ['tests.json','runtime.json','ui.json','login.png','console/src/login.ts']){const {closeout,artifacts}=closed();artifacts.delete(path);assert.throws(()=>validateRequirementsCloseout(source,closeout,artifacts));}
 const {closeout,artifacts}=closed();artifacts.set('runtime.json',{bytes:Buffer.from('{}')});assert.throws(()=>validateRequirementsCloseout(source,closeout,artifacts));
});
test('prototype-only evidence cannot close an item',()=>{const {closeout,artifacts}=closed();closeout.requirements[0].evidenceRefs=closeout.requirements[0].evidenceRefs.filter(x=>x.path!=='runtime.json');assert.throws(()=>validateRequirementsCloseout(source,closeout,artifacts));closeout.requirements[0].status='待复验';closeout.requirements[0].reason='原型截图不能代表当前功能验收';assert.equal(validateRequirementsCloseout(source,closeout,artifacts).statusCounts['待复验'],454);});
test('deferred_requires_recorded_user_decision',()=>{
 const c=sample(),unit=c.requirements[0];unit.status='经确认延期';assert.throws(()=>validateRequirementsCloseout(source,c,new Map()));
 const bytes=Buffer.from(JSON.stringify({kind:'user-decision',actor:'user',requirementIds:[unit.id],decision:'defer',text:'本需求暂不处理',recordedAt:at}));unit.evidenceRefs=[{path:'decision.json',sha256:hash(bytes)}];
 assert.equal(validateRequirementsCloseout(source,c,new Map([['decision.json',{bytes}]] )).statusCounts['经确认延期'],1);
});
test('unknown status source document mismatch and empty disposition are rejected',()=>{for(const change of [c=>c.requirements[0].status='完成',c=>c.originalDocumentSha256='x',c=>c.requirements[0].reason='']){const c=sample();change(c);assert.throws(()=>validateRequirementsCloseout(source,c,new Map()));}});
test('CLI checks real files and rejects traversal symlink and corrupt bytes',()=>{
 const root=mkdtempSync(join(tmpdir(),'webapi-p4-req-'));try{
  const filename=join(root,'closeout.json');writeFileSync(filename,JSON.stringify(sample()));const tool=fileURLToPath(new URL('../../scripts/account-credential/requirements.mjs',import.meta.url));
  const result=spawnSync(process.execPath,[tool,filename,root],{encoding:'utf8'});assert.equal(result.status,0,result.stderr);assert.equal(JSON.parse(result.stdout).units,454);
  const {closeout}=closed();closeout.requirements[0].implementationPaths[0].path='../outside';writeFileSync(filename,JSON.stringify(closeout));assert.notEqual(spawnSync(process.execPath,[tool,filename,root]).status,0);
  const c=sample(),bytes=Buffer.from('{}'),evidence=join(root,'proof.json');c.requirements[0].evidenceRefs=[{path:'proof.json',sha256:hash(bytes)}];writeFileSync(filename,JSON.stringify(c));writeFileSync(evidence,bytes);
  assert.equal(spawnSync(process.execPath,[tool,filename,root]).status,0);
  writeFileSync(evidence,'{"changed":true}');assert.notEqual(spawnSync(process.execPath,[tool,filename,root]).status,0);
  unlinkSync(evidence);writeFileSync(join(root,'real.json'),bytes);symlinkSync(join(root,'real.json'),evidence);assert.notEqual(spawnSync(process.execPath,[tool,filename,root]).status,0);
 }finally{rmSync(root,{recursive:true,force:true});}
});
