export function initialNetworkPolicyDraft(type:string):Record<string,any>;
export function validateNetworkPolicyDraft(draft:any,scope?:any):string|null;
export function networkPolicyReview(type:string,config:any):any;
export function protectedTransformHeader(name:string):boolean;
