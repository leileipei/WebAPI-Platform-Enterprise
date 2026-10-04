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
