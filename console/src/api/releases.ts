import {apiRequest} from './client';
export const releases={get:(id:string)=>apiRequest<any>(`/releases/${id}`),act:(id:string,action:string,body?:unknown,key?:string)=>apiRequest<any>(`/releases/${id}/${action}`,{method:'POST',body,idempotencyKey:key})};
