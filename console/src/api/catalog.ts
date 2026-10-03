import {apiRequest} from './client';import type {Page} from './types';
export type Api={id:string;organizationId:string;projectId:string;code:string;name:string;description?:string;groupId?:string|null;ownerUserId:string;lifecycleStatus:string;workingRevision:number};
export type Version={id:string;apiId:string;version:string;status:string;changeType:string;openapiDocument?:string;openapiSource?:string;sourceFormat?:string;schemaHash?:string;revision:number;sealedAt?:string|null};
export type ApiDetail={api:Api;workingRevision:number;runningConfigVersion:Record<string,number|null>;pendingReleaseId?:string|null;versions:Version[]};
export const catalog={list:(project:string)=>apiRequest<Page<Api>>(`/projects/${project}/apis`),detail:(id:string)=>apiRequest<ApiDetail>(`/apis/${id}`)};
