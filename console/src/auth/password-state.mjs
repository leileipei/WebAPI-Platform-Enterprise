export function passwordRoute(identity,path){return identity?.mustChangePassword===true?'/auth/change-password':path;}
function password(value){return typeof value==='string'&&value.length>0&&value.length<=1024;}
export function passwordChangeBody(values){
 const{currentPassword,newPassword,confirmPassword}=values;
 if(!password(currentPassword)||!password(newPassword)||!password(confirmPassword)||newPassword!==confirmPassword||currentPassword===newPassword)throw Error('请填写当前密码及一致的新密码，新旧密码须不同，最长1024字符。');
 return{currentPassword,newPassword,confirmPassword};
}
export function passwordResetBody(values){
 const{temporaryPassword,confirmPassword}=values;
 if(!password(temporaryPassword)||!password(confirmPassword)||temporaryPassword!==confirmPassword)throw Error('请填写一致的临时密码，最长1024字符。');
 return{temporaryPassword};
}
export function canResetLocalPassword(identity,target){return !!identity&&!identity.mustChangePassword&&identity.permissions?.includes('user.manage')===true&&target?.id!==identity.id&&target?.authSource==='local'&&target?.status==='Active';}
