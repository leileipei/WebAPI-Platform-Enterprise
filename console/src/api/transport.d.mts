export class ApiError extends Error {status:number;traceId?:string;issues:any[];code?:string;retryAfterSeconds?:number;constructor(status:number,detail:string,traceId?:string,issues?:any[],code?:string,retryAfterSeconds?:number)}
export function clearCsrfToken():void;
export function refreshCsrfToken():Promise<string>;
export function apiRequest<T=unknown>(path:string,options?:{method?:string;body?:unknown;etag?:string;idempotencyKey?:string;headers?:Record<string,string>;signal?:AbortSignal;responseType?:'response'}):Promise<T>;
