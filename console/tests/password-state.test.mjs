import {test} from 'node:test';
import assert from 'node:assert/strict';
import {passwordRoute,passwordChangeBody,passwordResetBody,canResetLocalPassword} from '../src/auth/password-state.mjs';
test('restricted deep link stays on password form',()=>{
 assert.equal(passwordRoute({mustChangePassword:true},'/applications'),'/auth/change-password');
 assert.equal(passwordRoute({mustChangePassword:false},'/applications'),'/applications');
 assert.equal(passwordRoute(null,'/dashboard'),'/dashboard');
});
test('password body preserves spaces and Unicode and accepts no target identity',()=>{
 const next='  New-e\u0301-private-password  ';
 assert.deepEqual(passwordChangeBody({currentPassword:' old-secret ',newPassword:next,confirmPassword:next,userId:'foreign'}),{currentPassword:' old-secret ',newPassword:next,confirmPassword:next});
 assert.deepEqual(passwordResetBody({temporaryPassword:next,confirmPassword:next,userId:'foreign'}),{temporaryPassword:next});
});
test('mismatch, unchanged and overlong inputs cannot submit or echo the secret',()=>{
 for(const input of [{currentPassword:'old',newPassword:'SECRET-a',confirmPassword:'SECRET-b'},{currentPassword:'SECRET-a',newPassword:'SECRET-a',confirmPassword:'SECRET-a'},{currentPassword:'old',newPassword:'x'.repeat(1025),confirmPassword:'x'.repeat(1025)}]){
  assert.throws(()=>passwordChangeBody(input),e=>!e.message.includes('SECRET-a')&&!e.message.includes('SECRET-b'));
 }
 assert.throws(()=>passwordResetBody({temporaryPassword:'SECRET-a',confirmPassword:'SECRET-b'}));
});
test('reset is limited to another active local user with user.manage and an unrestricted actor',()=>{
 const actor={id:'admin',permissions:['user.manage'],mustChangePassword:false},local={id:'target',authSource:'local',status:'Active'};
 assert.equal(canResetLocalPassword(actor,local),true);
 for(const changed of [{...local,id:'admin'},{...local,authSource:'sso'},{...local,status:'Disabled'}])assert.equal(canResetLocalPassword(actor,changed),false);
 assert.equal(canResetLocalPassword({...actor,mustChangePassword:true},local),false);
 assert.equal(canResetLocalPassword({...actor,permissions:[]},local),false);
});
