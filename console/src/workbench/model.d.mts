import type {Scope} from '../api/types';
import type {ObservationEnvelope,MetricsDto} from '../api/observability';
export type WorkbenchKey='api'|'metrics'|'nodes'|'alerts'|'releases';
export type WorkbenchEntry={state:'empty'|'denied'|'loading'|'ready'|'error';path?:string;data?:any;error?:string;status?:number;updatedAt?:string};
export type WorkbenchResults=Record<WorkbenchKey,WorkbenchEntry>;
export function workbenchPlan(scope:Partial<Scope>,can:(code:string,scope:Scope)=>boolean,end:string):WorkbenchResults;
export function loadWorkbench(plan:WorkbenchResults,request:(path:string,options:{signal:AbortSignal})=>Promise<unknown>,signal:AbortSignal,publish:(key:WorkbenchKey,value:WorkbenchEntry)=>void):Promise<void>;
export function workbenchMetric(result:WorkbenchEntry|undefined,name:string):MetricsDto['kpis'][number]|null;
