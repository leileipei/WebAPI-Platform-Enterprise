import {test} from 'node:test';import assert from 'node:assert/strict';
import {completeInitialPasswordChange} from '../../scripts/account-credential/auth.mjs';
test('existing account login never changes its password',async()=>{
 const identity={id:'old',mustChangePassword:false,authSource:'local'},client={request:()=>{throw Error('Existing account must not be changed');}};
 assert.equal(await completeInitialPasswordChange({client,identity,currentPassword:'old',newPassword:'new'}),identity);
});
test('old protocol without a flag and IdP users retain their login behavior',async()=>{
 for(const identity of [{id:'old'}, {id:'sso',authSource:'sso',mustChangePassword:false}])assert.equal(await completeInitialPasswordChange({client:{},identity}),identity);
});
test('new fixture user completes exactly one HTTP change using raw strings',async()=>{
 const calls=[],identity={id:'new',authSource:'local',mustChangePassword:true},updated={...identity,mustChangePassword:false};
 const client={request:async(path,options)=>{calls.push({path,options});return updated;}};
 const result=await completeInitialPasswordChange({client,identity,currentPassword:' old ',newPassword:' new-e\u0301-secret '});
 assert.equal(result,updated);assert.equal(calls.length,1);assert.equal(calls[0].path,'/auth/change-password');
 assert.deepEqual(calls[0].options.body,{currentPassword:' old ',newPassword:' new-e\u0301-secret ',confirmPassword:' new-e\u0301-secret '});
});
test('invalid post-change identity and SSO flag contradictions fail without claiming success',async()=>{
 const identity={id:'new',authSource:'local',mustChangePassword:true};
 await assert.rejects(()=>completeInitialPasswordChange({client:{request:async()=>identity},identity,currentPassword:'old',newPassword:'new'}));
 await assert.rejects(()=>completeInitialPasswordChange({client:{request:async()=>({id:'wrong',mustChangePassword:false})},identity,currentPassword:'old',newPassword:'new'}));
 await assert.rejects(()=>completeInitialPasswordChange({client:{request:()=>{throw Error('must not call');}},identity:{...identity,authSource:'sso'},currentPassword:'old',newPassword:'new'}));
});
