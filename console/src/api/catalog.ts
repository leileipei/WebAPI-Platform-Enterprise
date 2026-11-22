import {apiRequest} from './client';import type {Page} from './types';
export type Api={id:string;organizationId:string;projectId:string;code:string;name:string;description?:string;groupId?:string|null;ownerUserId:string;lifecycleStatus:string;workingRevision:number};
export type Version={id:string;apiId:string;version:string;status:string;changeType:string;openapiDocument?:string|null;openapiSource?:string|null;sourceFormat?:string|null;schemaHash?:string;revision:number;sealedAt?:string|null};
export type ApiDetail={api:Api;workingRevision:number;runningConfigVersion:Record<string,number|null>;pendingReleaseId?:string|null;versions:Version[];canReadSchemas:boolean;canReadVersions:boolean;canWriteSchemas:boolean;canWriteVersions:boolean};
export function isEditableVersion(version?:Pick<Version,'status'|'sealedAt'>){
  return version?.status==='Draft'&&!version.sealedAt;
}
export function apiDetailPath(id:string, environmentId?:string){
  return `/apis/${id}`+(environmentId?'?'+new URLSearchParams({environmentId}):'');
}
export const catalog={list:(project:string)=>apiRequest<Page<Api>>(`/projects/${project}/apis`),detail:(id:string)=>apiRequest<ApiDetail>(apiDetailPath(id))};
