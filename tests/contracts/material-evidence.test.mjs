import test from 'node:test';import assert from 'node:assert/strict';import * as evidence from '../../scripts/contracts/evidence.mjs';
const revision='a'.repeat(40),imageId='sha256:'+'b'.repeat(64),logHash='c'.repeat(64);
function fixture(){
 const files=[],add=(name,value)=>{const path='docs/evidence/'+name+'.json';files.push({path,type:'file',content:JSON.stringify(value)});return path;};
 const suites=['domain','integration','gateway','console','runtime-contracts'].map(name=>{const value={name,sourceRevision:revision,passed:1,failed:0,skipped:0,exitCode:0,originalLogSha256:logHash,passedTestLines:name==='domain'?['Passed WebApi.Tests.Rules(id: "case", direction: "request")']:['ok 1 - executed']};const artifact=add('tests-'+name,value);return {...value,artifact};});
 const rules=[{id:'schema.test',testIds:['case']}],coverage=[{ruleId:'schema.test',sourceRevision:revision,logHash,complete:true,registeredTestIds:['case'],executedTestIds:['case/request']}];
 const screenshot='docs/evidence/view.png';files.push({path:screenshot,type:'file',content:Buffer.from([137,80,78,71])});
 const ui={sourceRevision:revision,kind:'cua',actual:true,widths:[1440,1280],actions:['actually operated'],screenshots:[screenshot]};
 const materialArtifacts={catalog:add('catalog',{sourceRevision:revision,catalog:{rules}}),ruleCoverage:add('coverage',coverage),ui:add('ui',ui),scenario:add('scenario',{sourceRevision:revision,imageId,actual:true,complete:true,steps:['baseline-published','review-two-level-publish','rollback'].map(name=>({name}))}),protection:add('protection',{sourceRevision:revision,originalPreserved:true,backup:{complete:true,hashReadback:true,originalServicesRestored:true},restore:{complete:true,actual:true,restored:true,ownerVerified:true},software:{complete:true,sourceRevision:revision,imageId}})};
 return {files,proof:{sourceRevision:revision,imageId,suites,ruleCoverage:coverage,ui,materialArtifacts}};
}
function modify(f,path,update){const file=f.files.find(x=>x.path===path),value=JSON.parse(file.content);update(value);file.content=JSON.stringify(value);}
test('gate binds five distinct executed suites and actual UI/scenario/catalog materials',()=>{const f=fixture();assert.equal(evidence.assertMaterialEvidence(f.proof,f.files),true);});
for(const name of ['domain','integration','gateway','console','runtime-contracts'])test('old or failed internal '+name+' proof is rejected despite a passing current wrapper',()=>{
 const f=fixture(),artifact=f.proof.suites.find(x=>x.name===name).artifact;
 modify(f,artifact,value=>{value.sourceRevision='d'.repeat(40);value.failed=1;value.exitCode=1;});assert.throws(()=>evidence.assertMaterialEvidence(f.proof,f.files));
});
test('five wrappers cannot reuse a single material or duplicate a suite',()=>{const f=fixture();f.proof.suites=f.proof.suites.map(x=>({...x,artifact:f.proof.suites[0].artifact}));assert.throws(()=>evidence.assertMaterialEvidence(f.proof,f.files));});
for(const kind of ['ui','scenario','protection','catalog'])test('old internal '+kind+' source is rejected',()=>{const f=fixture();modify(f,f.proof.materialArtifacts[kind],value=>value.sourceRevision='d'.repeat(40));assert.throws(()=>evidence.assertMaterialEvidence(f.proof,f.files));});
test('registered rules and claimed executed IDs must match actual domain Passed records',()=>{const f=fixture();modify(f,f.proof.materialArtifacts.catalog,value=>value.catalog.rules[0].testIds.push('not-executed'));assert.throws(()=>evidence.assertMaterialEvidence(f.proof,f.files));});
test('passing wrapper cannot conceal failed same-revision internal results',()=>{const f=fixture();modify(f,f.proof.suites[0].artifact,value=>{value.failed=1;value.exitCode=1;});assert.throws(()=>evidence.assertMaterialEvidence(f.proof,f.files));});
test('coverage log hash must bind the domain log instead of another material',()=>{const f=fixture();modify(f,f.proof.materialArtifacts.ruleCoverage,value=>value[0].logHash='e'.repeat(64));assert.throws(()=>evidence.assertMaterialEvidence(f.proof,f.files));});
