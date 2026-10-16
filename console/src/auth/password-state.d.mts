import type {Identity} from '../api/types';
export function passwordRoute(identity:Pick<Identity,'mustChangePassword'>|null,path:string):string;
export function passwordChangeBody(values:{currentPassword:string;newPassword:string;confirmPassword:string}):{currentPassword:string;newPassword:string;confirmPassword:string};
export function passwordResetBody(values:{temporaryPassword:string;confirmPassword:string}):{temporaryPassword:string};
export function canResetLocalPassword(identity:Identity|null,target:{id:string;authSource:string;status:string}):boolean;
