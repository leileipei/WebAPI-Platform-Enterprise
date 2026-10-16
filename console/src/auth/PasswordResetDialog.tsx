import {Editor} from '../ui';
import {apiRequest} from '../api/client';
import {useSession} from './SessionProvider';
import {canResetLocalPassword,passwordResetBody} from './password-state.mjs';
export function PasswordResetDialog({user,onClose,onSaved}:{user:{id:string;displayName:string;authSource:string;status:string;revision:number};onClose:()=>void;onSaved:()=>void}){
 const session=useSession();
 return <Editor title={'重置本地密码 · '+user.displayName} allowed={!session.error&&canResetLocalPassword(session.user,user)} fields={[{key:'temporaryPassword',label:'临时密码',type:'password',required:true,help:'遵循当前平台密码策略；不会回显或发送。'},{key:'confirmPassword',label:'再次输入临时密码',type:'password',required:true}]} help="重置后旧会话立即失效，用户须用临时密码登录并改密。请自行维护私有运维凭证记录。" close={onClose} save={async(value,key)=>{await apiRequest('/users/'+user.id+'/password-reset',{method:'POST',body:passwordResetBody({temporaryPassword:value.temporaryPassword,confirmPassword:value.confirmPassword}),etag:`\"${user.revision}\"`,idempotencyKey:key});onSaved();}}/>;
}
