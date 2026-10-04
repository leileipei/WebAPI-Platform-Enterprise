import type {ObservationSearch} from './query-state.mjs';import type {TraceSpanDto} from '../api/observability';
export function updateInvestigationSearch(current:ObservationSearch,patch:Partial<ObservationSearch>,now?:Date):ObservationSearch;
export function createCursorTrail():{record(key:string,search:string):void;previous(key:string):string|undefined;hasPrevious(key:string):boolean;reset():void};
export function waterfallRows(spans:TraceSpanDto[]):(TraceSpanDto&{left:number;width:number;offsetMs:number;depth:number})[];
export function exportMetadata(headers:Headers):{rows:number;truncated:boolean;state:'Available'|'NoData'|'Partial'};
export function observationErrorLabel(status?:number):string;
