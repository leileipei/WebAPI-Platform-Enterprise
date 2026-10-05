import {apiRequest} from './client';
export type SourceState='Available'|'NoData'|'Partial'|'Unavailable'|'Stale'|'NotApplicable';
export type TimeRange={start:string;end:string};
export type ObservationScopeRequest={organizationId:string;projectId:string;environmentId:string|null;allAccessibleEnvironments:boolean};
export type ObservationEnvelope<T>={sourceState:SourceState;range:TimeRange;observedAt:string|null;coverage:{complete:boolean;missingNodes:string[];reason:string|null;truncated:boolean};sampling:{ratio:number;mode:string}|null;data:T|null};
export type MetricFilter={apiId?:string;applicationId?:string;destinationId?:string;groupBy?:string;page?:number;pageSize?:number;sortBy?:string};
export type MetricValue={metric:string;value:number|null;unit:string;sampleCount:number;state:SourceState};
export type MetricPoint={time:string;value:number|null};
export type MetricsDto={kpis:MetricValue[];trends:Record<string,MetricPoint[]>;groups:{key:string;name:string;values:MetricValue[]}[];totalGroups:number;page:number;pageSize:number;nodeHealth:{nodeName:string;environmentId:string;health:string;destinations:{clusterId:string;destinationId:string;health:string}[]}[]};
export function observationQuery(scope:ObservationScopeRequest,range:TimeRange,filter:Record<string,unknown>){const p=new URLSearchParams();for(const [k,v]of Object.entries({...scope,...range,...filter}))if(v!==undefined&&v!==null&&v!=='')p.set(k,String(v));return p.toString();}
export function loadMetrics(scope:ObservationScopeRequest,range:TimeRange,filter:MetricFilter,signal:AbortSignal){return apiRequest<ObservationEnvelope<MetricsDto>>('/observability/'+(filter.apiId?`apis/${filter.apiId}/metrics`:'metrics')+'?'+observationQuery(scope,range,filter),{signal});}
export type CursorPage<T>={items:T[];nextCursor:string|null;truncated:boolean};
export type PolicyDecisionDto={policyId:string;policyType:string;policyRevision:number;decision:string;rejectionReason:string|null};
export type AccessLogDto={id:string;time:string;environmentId:string;apiId:string|null;applicationKey:string;method:string;pathTemplate:string;status:number|null;durationMs:number;outcome:string;requestId:string;traceId:string|null;maskedIp:string;nodeName:string;configVersion:number|null;deploymentSequence:number|null;destinationId:string|null;policyDecisions:PolicyDecisionDto[]};
export type LogFilter={apiId?:string;applicationId?:string;destinationId?:string;status?:string;minDurationMs?:number;maxDurationMs?:number;ip?:string;keyword?:string;policyId?:string;policyDecision?:string;traceId?:string;limit?:number;cursor?:string};
export type TraceFilter={policyId?:string;policyDecision?:string;traceId?:string;apiId?:string;minDurationMs?:number;outcome?:string;limit?:number;cursor?:string};
export type TraceSummaryDto={traceId:string;start:string;durationMs:number;outcome:string};
export type TraceSpanDto={spanId:string;parentSpanId:string|null;name:string;start:string;durationMs:number;kind:string;status:string;tags:Record<string,string>;policyDecisions:PolicyDecisionDto[]};
export type TraceDetailDto={traceId:string;partialTrace:boolean;spans:TraceSpanDto[]};
export function loadLogs(scope:ObservationScopeRequest,range:TimeRange,filter:LogFilter,signal:AbortSignal){return filter.ip?apiRequest<ObservationEnvelope<CursorPage<AccessLogDto>>>('/observability/logs/query',{method:'POST',body:{scope,range,filter},signal}):apiRequest<ObservationEnvelope<CursorPage<AccessLogDto>>>('/observability/logs?'+observationQuery(scope,range,filter),{signal});}
export function loadLogDetail(scope:ObservationScopeRequest,range:TimeRange,id:string,signal:AbortSignal){return apiRequest<ObservationEnvelope<AccessLogDto>>('/observability/logs/'+id+'?'+observationQuery(scope,range,{}),{signal});}
export function exportLogs(scope:ObservationScopeRequest,range:TimeRange,filter:LogFilter,signal:AbortSignal){return filter.ip?apiRequest<Response>('/observability/logs/export',{method:'POST',body:{scope,range,filter:{...filter,cursor:undefined}},signal,responseType:'response'}):apiRequest<Response>('/observability/logs/export?'+observationQuery(scope,range,{...filter,cursor:undefined}),{signal,responseType:'response'});}
export function loadTraces(scope:ObservationScopeRequest,range:TimeRange,filter:TraceFilter,signal:AbortSignal){return apiRequest<ObservationEnvelope<CursorPage<TraceSummaryDto>>>('/observability/traces?'+observationQuery(scope,range,filter),{signal});}
export function loadTraceDetail(scope:ObservationScopeRequest,range:TimeRange,id:string,signal:AbortSignal){return apiRequest<ObservationEnvelope<TraceDetailDto>>('/observability/traces/'+id+'?'+observationQuery(scope,range,{}),{signal});}
