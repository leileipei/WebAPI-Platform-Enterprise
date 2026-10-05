export function ssoUserPayload(value:Record<string,any>):Record<string,any>;
export function ssoBindingPayload(value:Record<string,any>):Record<string,any>;
export function mayEditSsoBinding(user:{authSource:string;status:string}|undefined,permissions:string[]):boolean;
