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
export function promotionUrl(promotion:any,environment:any,side:'source'|'target'):string|undefined;
export function promotionMaySubmit(promotion:any,precheck:any):boolean;
export function mappingReady(mapping:any):boolean;
export function confirmationRequired(impact:any):boolean;
export function promotionExecutionLabel(promotion:any):string;
export function mayRecordProduction(promotion:any,userId?:string):boolean;
export function promotionReturnUrl(id:string,search?:string):string;

export function promotionRiskLabel(risk:any):string;
export function deliveryReasonLabel(code:string):string;

export function promotionCheckLabel(check:any):string;
