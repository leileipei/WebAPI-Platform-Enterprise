import {apiRequest} from './client';
export const applications={detail:(id:string)=>apiRequest<any>(`/applications/${id}`)};
