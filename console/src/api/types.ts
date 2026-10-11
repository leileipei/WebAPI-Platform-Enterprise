export type Scope={organizationId:string;projectId?:string|null;environmentId?:string|null};
export type Identity={id:string;username:string;displayName:string;permissions:string[];scopes:{scope:Scope;accessMode:string}[]};
export type Resource={id:string;code:string;name:string;status:string;revision:number;organizationId?:string;projectId?:string;isProduction?:boolean;sortOrder?:number;releasePolicyId?:string|null;desiredConfigVersion?:number|null;deploymentSequence?:number;gatewayPublicUrl?:string|null;gatewayInternalUrl?:string|null;basePath?:string;accessAddressRevision?:number};
export type ScopeTree={organizations:Resource[];projects:Resource[];environments:Resource[]};
export type Page<T>={items:T[];total:number;page:number;pageSize:number};
