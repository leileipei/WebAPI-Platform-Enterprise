import type {Scope} from '../api/types';
import {apiRequest} from '../api/client';
export type NotificationChannel='Email'|'Webhook';
export type DeliveryStatus='Queued'|'Sending'|'RetryScheduled'|'Paused'|'Accepted'|'Failed'|'Suppressed'|'Expired';
export type NotificationDelivery={id:string;kind:'Alert'|'Test';eventId:string|null;channel:NotificationChannel;maskedTarget:string;status:DeliveryStatus;reason:string|null;attemptCount:number;maxAttempts:number;createdAt:string;expiresAt:string;nextAttemptAt:string|null;canRetry:boolean;revision:number};
export type NotificationAttempt={attemptNo:number;startedAt:string;completedAt:string|null;outcome:'Accepted'|'TransientFailure'|'PermanentFailure'|'OutcomeUnknown'|null;code:string|null;protocolStatus:number|null};
export type NotificationPage<T>={items:T[];total:number;page:number;pageSize:number};
export type NotificationDeploymentPolicy={allowedRecipients:string[];allowedDomains:string[];source:'DeploymentConfiguration';maxRecipients:number};
export type NotificationLimits={maxRecipients:number;emailConfigured:boolean;emailEnabled:boolean;webhookConfigured:boolean;webhookEnabled:boolean};
export const notificationLimitsPath=(scope:Scope)=>{const params=new URLSearchParams({organizationId:scope.organizationId});if(scope.projectId)params.set('projectId',scope.projectId);if(scope.environmentId)params.set('environmentId',scope.environmentId);return '/notification-limits?'+params;};
export const notificationApi={
 deploymentPolicy:(signal?:AbortSignal)=>apiRequest<NotificationDeploymentPolicy>('/settings/system/notification/deployment-policy',{signal}),
 createTest:(channel:NotificationChannel,email:string,revision:number,key:string,signal?:AbortSignal)=>apiRequest<NotificationDelivery>('/settings/system/notification/tests',{method:'POST',etag:'"'+revision+'"',idempotencyKey:key,body:{channel,...(channel==='Email'?{email}:{})},signal}),
 readTest:(id:string,signal?:AbortSignal)=>apiRequest<NotificationDelivery>('/settings/system/notification/tests/'+id,{signal}),
 deliveries:(eventId:string,page=1,signal?:AbortSignal)=>apiRequest<NotificationPage<NotificationDelivery>>(`/alerts/${eventId}/notifications?page=${page}&pageSize=20`,{signal}),
 attempts:(id:string,page=1,signal?:AbortSignal)=>apiRequest<NotificationPage<NotificationAttempt>>(`/notification-deliveries/${id}/attempts?page=${page}&pageSize=20`,{signal}),
 retry:(id:string,revision:number,key:string,signal?:AbortSignal)=>apiRequest<NotificationDelivery>(`/notification-deliveries/${id}/retry`,{method:'POST',etag:'"'+revision+'"',idempotencyKey:key,body:{},signal})
};
