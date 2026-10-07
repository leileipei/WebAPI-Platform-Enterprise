export function initialJwtDraft():Record<string,any>;
export function validateAdvancedPolicyDraft(draft:any,scope?:any):string|null;
export function changeAuthenticationMode(state:any,mode:string,confirmed:boolean):any;
export function acceptApplicationOptions(state:any,event:any):any;
export function parsePublicJwksFile(raw:string,algorithms?:string[]):any;
export function effectiveAuthentication(route:any):string;
export function routePolicyPayload(value:any,original?:any):any;
export function advancedPolicyReview(policies:any[],options?:{baselinePolicies?:any[]|null}):any[];
