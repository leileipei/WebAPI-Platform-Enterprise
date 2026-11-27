export type SsoTest={id:string;providerRevision:number;testedAt:string;status:string;stages:{code:string;passed:boolean;detail:string}[]};
export type SsoProvider={id:string;organizationId:string|null;name:string;providerType:string;issuer:string;clientId:string;secretRef:string;scopes:string[];claimMapping:{displayName:string|null;email:string|null};enabled:boolean;isDefault:boolean;revision:number;authRevision:number;lastTest:SsoTest|null;callbackUrl:string};
export type SsoDraft={organizationId:string|null;name:string;issuer:string;clientId:string;secretRef:string;scopesText:string;displayNameClaim:string;emailClaim:string};
export type SsoReadPurpose='open'|'refresh'|'review';
export type SsoState={providerId:string|null;epoch:number;draft:SsoDraft|null;loaded:SsoProvider|null;etag:string|null;dirty:boolean;conflict:boolean;reviewed:SsoProvider|null;authority:string|null};
export type PublicSsoProvider={id:string;name:string;isDefault:boolean};
export type ExternalIdentity={id:string;userId:string;providerId:string;issuer:string;subject:string;enabled:boolean;revision:number};
