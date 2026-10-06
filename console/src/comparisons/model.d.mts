export const COMPARISON_TIMEOUT_MS:number;
export function apiRoute(path:string):{kind:string;id?:string};
export function comparisonQueryState(query:string):{comparisonId?:string};
export function filterComparison(report:any,filters?:any):any[];
export function selectComparisonVersion(state:any,side:string,id:string):any;
export function reviewCommand(report:any,decision:string,comment:string,confirmRisk:boolean):any;
export function reviewRetryState(previous:any,body:any,makeKey?:()=>string):any;
export function loadComparison(request:(signal?:AbortSignal)=>Promise<any>,scopeKey:string,signal:AbortSignal,onResult:(value:any,scopeKey:string)=>void):Promise<any>;
export function timedComparison<T>(request:(signal:AbortSignal)=>Promise<T>,signal?:AbortSignal,timeout?:number):Promise<T>;
export function resolveReviewHandoff(apiId:string,reviewId:string,request:(path:string,options?:any)=>Promise<any>,signal?:AbortSignal,timeout?:number):Promise<any>;
export function releaseReviewIds(handoff:any,targetVersionId:string):string[]|undefined;

export function reviewDraftState(previous:any,event:any):any;

export function comparisonRead<T=any>(path:string,request:(path:string,options?:any)=>Promise<T>,signal?:AbortSignal,timeout?:number):Promise<T>;
