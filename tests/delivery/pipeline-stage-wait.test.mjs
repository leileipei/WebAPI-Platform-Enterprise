import test from 'node:test';
import assert from 'node:assert/strict';
import {waitPipelineReadyForEvidence} from '../../scripts/delivery/pipeline-stage-wait.mjs';
test('successful ACK does not authorize materialization before stage projection',async()=>{
 const readings=[{id:'stage',actualReleaseStatus:'Succeeded',runStatus:'Active',status:'Deploying',profile:{isProduction:false},eligibility:{canMaterialize:false}},
 {id:'stage',actualReleaseStatus:'Succeeded',runStatus:'Active',status:'AwaitingVerification',profile:{isProduction:false},eligibility:{canMaterialize:true}}];
 let calls=0;const stage=await waitPipelineReadyForEvidence(async()=>readings[Math.min(calls++,1)]);
 assert.equal(stage.eligibility.canMaterialize,true);assert.equal(calls,2);
});
test('production evidence waits for its projected verification window',async()=>{
 let calls=0;const stage=await waitPipelineReadyForEvidence(async()=>({actualReleaseStatus:'Succeeded',runStatus:'Active',status:calls++?'AwaitingVerification':'Deploying',profile:{isProduction:true},eligibility:{canVerifyProduction:false}}));
 assert.equal(stage.status,'AwaitingVerification');assert.equal(calls,2);
});
