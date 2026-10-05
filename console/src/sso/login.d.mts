import type {PublicSsoProvider} from './contracts';
export function fetchLoginProviders(organizationId?:string,signal?:AbortSignal):Promise<PublicSsoProvider[]>;
export function safeSsoReturnPath(path:unknown):string;
export function createSsoStartForm(id:string,path:string,csrf:string,reauthenticate?:boolean,document?:Document):HTMLFormElement;
export function completeSsoLogin(options:{refresh:()=>Promise<void>;csrf:()=>Promise<unknown>;navigate:(path:string)=>void;returnPath:string|null}):Promise<void>;
export function ssoErrorMessage(search:string):string|null;
