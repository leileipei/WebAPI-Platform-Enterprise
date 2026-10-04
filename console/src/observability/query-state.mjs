const ranges=['1h','6h','24h','7d'],groups=['None','Api','Application','Destination','Status'];
const uuid=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function parseObservationSearch(search){
 const p=new URLSearchParams(search);const state={range:ranges.includes(p.get('range'))?p.get('range'):'1h',group:groups.includes(p.get('group'))?p.get('group'):'Api',page:Math.max(1,Math.min(1000000,Number(p.get('page'))||1))};
 for(const key of ['api','app','destination'])if(uuid.test(p.get(key)||''))state[key]=p.get(key);
 if(/^(?:[1-5]xx|[1-5][0-9]{2}|ClientAborted)$/.test(p.get('status')||''))state.status=p.get('status');
 if(p.get('duration')&&Number.isFinite(Number(p.get('duration')))&&Number(p.get('duration'))>=0)state.duration=Number(p.get('duration'));
 if(p.get('maxDuration')&&Number.isFinite(Number(p.get('maxDuration')))&&Number(p.get('maxDuration'))>=0)state.maxDuration=Number(p.get('maxDuration'));
 if(p.get('keyword')&&p.get('keyword').length<=256&&!/[\x00-\x1f]/.test(p.get('keyword')))state.keyword=p.get('keyword');
 if(['Success','Error'].includes(p.get('outcome')))state.outcome=p.get('outcome');
 if(/^[0-9a-f]{32}$/i.test(p.get('trace')||''))state.trace=p.get('trace');
 if((p.get('cursor')||'').length<=4096&&p.get('cursor'))state.cursor=p.get('cursor');
 if(p.get('end')&&Number.isFinite(Date.parse(p.get('end'))))state.end=new Date(p.get('end')).toISOString();
 if(p.get('all')==='true')state.all=true;return state;
}
export function serializeObservationSearch(state){
 const p=new URLSearchParams();for(const key of ['range','api','app','destination','status','duration','maxDuration','keyword','outcome','trace','group','cursor','end','page','all'])if(state[key]!==undefined&&state[key]!==null&&state[key]!=='')p.set(key,String(state[key]));
 const clean=parseObservationSearch(p.toString());const result=new URLSearchParams();for(const [key,value]of Object.entries(clean))result.set(key,String(value));return '?'+result.toString();
}
export function observationRange(state,now=new Date()){
 const end=state.end?new Date(state.end):now;const hours={'1h':1,'6h':6,'24h':24,'7d':168}[state.range]||1;
 return {start:new Date(end.getTime()-hours*3600000).toISOString(),end:end.toISOString()};
}
export function createObservationGate(){let current,scope;return{
 begin(key,cursor){current?.controller.abort();const switched=scope!==undefined&&scope!==key;scope=key;const controller=new AbortController();current={controller,signal:controller.signal,cursor:switched?undefined:cursor};return current;},
 isCurrent(request){return request===current&&!request.signal.aborted;},dispose(){current?.controller.abort();}
};}
export function metricDisplay(metric){
 if(metric.value===null||metric.value===undefined||!Number.isFinite(metric.value))return '—';
 if(metric.unit==='ratio')return (metric.value*100).toFixed(2)+'%';
 if(metric.unit==='%')return metric.value.toFixed(2)+'%';
 return new Intl.NumberFormat('zh-CN',{maximumFractionDigits:metric.unit==='requests'?0:2}).format(metric.value)+(metric.unit==='ms'?' ms':metric.unit==='requests/s'?' /s':'');
}
export function chartSegments(points){const segments=[];let row=[];for(const p of points.slice(0,600)){if(p.value===null||!Number.isFinite(p.value)||!Number.isFinite(Date.parse(p.time))){if(row.length)segments.push(row);row=[];}else row.push(p);}if(row.length)segments.push(row);return segments;}
export function resetObservationScopeSearch(search,kind){
 const state=parseObservationSearch(search);delete state.cursor;state.page=1;delete state.destination;
 if(kind!=='environment'){delete state.api;delete state.app;}return serializeObservationSearch(state);
}
