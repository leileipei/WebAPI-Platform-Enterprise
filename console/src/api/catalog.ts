import {apiRequest} from './client';import type {Page,Scope} from './types';
export type Api={id:string;organizationId:string;projectId:string;code:string;name:string;description?:string;groupId?:string|null;ownerUserId:string;lifecycleStatus:string;workingRevision:number};
export type Version={id:string;apiId:string;version:string;status:string;changeType:string;openapiDocument?:string|null;openapiSource?:string|null;sourceFormat?:string|null;schemaHash?:string;revision:number;createdAt?:string|null;sealedAt?:string|null};
export type ApiDetail={api:Api;workingRevision:number;runningConfigVersion:Record<string,number|null>;pendingReleaseId?:string|null;versions:Version[];canReadSchemas:boolean;canReadVersions:boolean;canWriteSchemas:boolean;canWriteVersions:boolean};
export function isEditableVersion(version?:Pick<Version,'status'|'sealedAt'>){
  return version?.status==='Draft'&&!version.sealedAt;
}
export function apiDetailPath(id:string, environmentId?:string){
  return `/apis/${id}`+(environmentId?'?'+new URLSearchParams({environmentId}):'');
}
export function apiInWorkspace(api:Api,id:string,workspace:Scope){
  return !!workspace.organizationId&&!!workspace.projectId&&api.id?.toLowerCase()===id.toLowerCase()&&
    api.organizationId===workspace.organizationId&&api.projectId===workspace.projectId;
}
export const catalog={list:(project:string)=>apiRequest<Page<Api>>(`/projects/${project}/apis`),detail:(id:string)=>apiRequest<ApiDetail>(apiDetailPath(id))};
