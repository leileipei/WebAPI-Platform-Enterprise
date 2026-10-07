import test from 'node:test';import assert from 'node:assert/strict';import {assertReleaseIdentity,assertRuleCoverage,assertPublicFiles,validateCloneOwnership,validateContractAcceptance} from '../../scripts/contracts/evidence.mjs';
const revision='a'.repeat(40),image='sha256:'+'b'.repeat(64),project='webapi-enterprise-local-test-11111111-1111-4111-8111-111111111111',owner='22222222-2222-4222-8222-222222222222';
const release={sourceRevision:revision,imageId:image};
test('release must match fixed Git revision, actual image ID and container labels',()=>{
 assert.equal(assertReleaseIdentity({revision,release,actualImages:[{imageId:image,sourceRevision:revision}]}),true);
 for(const wrong of [{revision:'c'.repeat(40)},{release:{...release,imageId:'sha256:'+'c'.repeat(64)}},{actualImages:[{imageId:image,sourceRevision:'c'.repeat(40)}]}])assert.throws(()=>assertReleaseIdentity({revision,release,actualImages:[{imageId:image,sourceRevision:revision}],...wrong}));
});
test('every registered rule and each declared case must link to a test actually passed in this revision',()=>{
 const catalog=[{id:'schema.types',testIds:['types.integer_number','types.boolean']}],executed=['types.integer_number/request','types.integer_number/response','types.boolean/request'];const coverage=assertRuleCoverage({catalog,executed,sourceRevision:revision,logHash:'d'.repeat(64)});assert.equal(coverage.length,1);assert.equal(coverage[0].complete,true);
 assert.throws(()=>assertRuleCoverage({catalog,executed:['types.integer_number/request'],sourceRevision:revision,logHash:'d'.repeat(64)}),/覆盖/);
});
test('public delivery accepts only public relative paths and rejects private credentials, traversal and links',()=>{
 assert.equal(assertPublicFiles([{path:'docs/evidence/contract-management-completion/result.md',content:'passed',type:'file'}]),true);
 for(const file of [{path:'.runtime/clone/secrets/bootstrap-password',content:'private',type:'file'},{path:'../private.json',content:'secret',type:'file'},{path:'/Users/person/secret',content:'secret',type:'file'},{path:'docs/result.md',type:'symlink'},{path:'docs/result.md',type:'file',content:'password=REAL-PASSWORD'}])assert.throws(()=>assertPublicFiles([file]),/公开/);
});
test('clone mutations and cleanup reject unowned resources or original service ports',()=>{
 const state={projectName:project,ownerId:owner,ports:{console:45001,gatewayA:45002,gatewayB:45003}},resource={Config:{Labels:{'com.docker.compose.project':project,'com.webapi.runtime.owner':owner}}};assert.equal(validateCloneOwnership(state,[resource],'/tmp/'+project),true);
 for(const bad of [{...state,projectName:'webapi-enterprise-local'},{...state,ownerId:'33333333-3333-4333-8333-333333333333'},{...state,ports:{...state.ports,console:4192}}])assert.throws(()=>validateCloneOwnership(bad,[resource],'/tmp/'+project));assert.throws(()=>validateCloneOwnership(state,[{Config:{Labels:{}}}],'/tmp/'+project));
});
test('simulated UI success and missing/failed checks cannot become actual complete acceptance',()=>{
 const proof={sourceRevision:revision,imageId:image,checks:Array.from({length:15},(_,i)=>({id:'C'+String(i+1).padStart(2,'0'),status:'Passed',sourceRevision:revision,kind:'actual',evidence:['actual-log']})),ruleCoverage:[{ruleId:'schema.types',complete:true,sourceRevision:revision}],ui:{kind:'cua',actual:true,widths:[1440,1280],sourceRevision:revision},clone:{actual:true,ownerVerified:true,restored:true},immutableBuild:true};assert.equal(validateContractAcceptance(proof,{revision,imageId:image}).passed,true);
 for(const wrong of [{ui:{...proof.ui,kind:'mock'}},{ui:{...proof.ui,widths:[1440]}},{checks:proof.checks.slice(1)},{checks:[{...proof.checks[0],status:'NotExecuted'},...proof.checks.slice(1)]},{sourceRevision:'c'.repeat(40)},{clone:{...proof.clone,restored:false}},{ruleCoverage:[]}])assert.throws(()=>validateContractAcceptance({...proof,...wrong},{revision,imageId:image}));
});
