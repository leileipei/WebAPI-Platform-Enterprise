export const policyTypes:Record<string,string>;
export function initialPolicyDraft(type:string):Record<string,any>;
export function validatePolicyDraft(draft:any,scope?:any):string|null;
export function policyEditorReducer(state:any,event:any):any;
export function copyDraft(source:any):any;
export function refreshReview(review:any,revisions:any[],confirmed:boolean):any;
export function policyFieldErrors(type:string,config:Record<string,any>):Record<string,string>;
export function policyListQuery(filters:Record<string,string>):URLSearchParams;
