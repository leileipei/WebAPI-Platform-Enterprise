export const sourceTestTypes:{value:string;label:string}[];
export const productionTestTypes:{value:string;label:string}[];
export function deliveryAuthorityKey(user:unknown):string;
export function createDeliveryState(context?:Record<string,any>):any;
export function deliveryReducer(state:any,event:any):any;
export function manualEvidenceLabel(row:any):string;
export function deliveryPolicyLabel(policy:any):string;
export function verificationExpiryLabel(minutes:number):string;
export function mayAcceptTest(row:any,userId?:string):boolean;

export function deliveryIsCurrent(state:any,context:any):boolean;
