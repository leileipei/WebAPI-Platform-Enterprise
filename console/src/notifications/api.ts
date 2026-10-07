import {apiRequest} from '../api/client';
export type NotificationChannel='Email'|'Webhook';
export type DeliveryStatus='Queued'|'Sending'|'RetryScheduled'|'Paused'|'Accepted'|'Failed'|'Suppressed'|'Expired';
export type NotificationDelivery={id:string;kind:'Alert'|'Test';eventId:string|null;channel:NotificationChannel;maskedTarget:string;status:DeliveryStatus;reason:string|null;attemptCount:number;maxAttempts:number;createdAt:string;expiresAt:string;nextAttemptAt:string|null;canRetry:boolean;revision:number};
export type NotificationAttempt={attemptNo:number;startedAt:string;completedAt:string|null;outcome:'Accepted'|'TransientFailure'|'PermanentFailure'|'OutcomeUnknown'|null;code:string|null;protocolStatus:number|null};
export type NotificationPage<T>={items:T[];total:number;page:number;pageSize:number};
export type NotificationDeploymentPolicy={allowedRecipients:string[];allowedDomains:string[];source:'DeploymentConfiguration';maxRecipients:number};
export const notificationApi={
 deploymentPolicy:(signal?:AbortSignal)=>apiRequest<NotificationDeploymentPolicy>('/settings/system/notification/deployment-policy',{signal}),
 createTest:(channel:NotificationChannel,email:string,revision:number,key:string,signal?:AbortSignal)=>apiRequest<NotificationDelivery>('/settings/system/notification/tests',{method:'POST',etag:'"'+revision+'"',idempotencyKey:key,body:{channel,...(channel==='Email'?{email}:{})},signal}),
 readTest:(id:string,signal?:AbortSignal)=>apiRequest<NotificationDelivery>('/settings/system/notification/tests/'+id,{signal})
};
