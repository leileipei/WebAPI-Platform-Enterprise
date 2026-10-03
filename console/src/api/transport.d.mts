export class ApiError extends Error {status:number;traceId?:string;constructor(status:number,detail:string,traceId?:string)}
export function apiRequest<T=unknown>(path:string,options?:{method?:string;body?:unknown;etag?:string;idempotencyKey?:string;headers?:Record<string,string>;signal?:AbortSignal}):Promise<T>;
