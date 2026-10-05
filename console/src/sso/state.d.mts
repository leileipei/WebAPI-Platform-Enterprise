import type {SsoState,SsoProvider,SsoDraft} from './contracts';
export function createSsoState():SsoState;
export function beginSsoRequest(state:SsoState,id:string|null):SsoState;
export function applySsoResponse(state:SsoState,response:SsoProvider,epoch:number):SsoState;
export function applySsoFailure(state:SsoState,status:number,epoch:number):SsoState;
export function invalidateSsoState(state:SsoState):SsoState;
export function ssoAuthorityKey(user:any):string;
export function canEnableSsoProvider(provider:SsoProvider|null,now?:number):boolean;
export function ssoSemanticKey(operation:string,body:unknown,etag:string|null):string;
export function mayLeaveSso(state:SsoState,confirmed?:boolean):boolean;
export function toSsoDraft(response:SsoProvider):SsoDraft;
export function toSsoCommand(draft:SsoDraft):unknown;

export const SSO_SECRET_REFERENCE_PATTERN:string;
export const SSO_PROVIDER_NAME_MAX:number;
export function createSsoMutationGate():{readonly busy:boolean;begin(epoch:number):object|null;finish(token:object,epoch?:number):boolean;reset():void};
