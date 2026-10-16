// Explicit fixture helper only. LocalClient and existing running accounts are never changed automatically.
export async function completeInitialPasswordChange({client,identity,currentPassword,newPassword}){
 if(identity?.mustChangePassword!==true)return identity;
 if(identity.authSource==='sso'||typeof currentPassword!=='string'||typeof newPassword!=='string'||!currentPassword||!newPassword||currentPassword===newPassword)throw Error('Fixture initial password change requires a local identity and distinct private passwords.');
 const result=await client.request('/auth/change-password',{method:'POST',body:{currentPassword,newPassword,confirmPassword:newPassword}});
 if(!result||result.id!==identity.id||result.mustChangePassword!==false)throw Error('Fixture initial password change did not return an unrestricted matching identity.');
 return result;
}
