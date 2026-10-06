export function workbenchPlan(scope,can,end){
 const {organizationId,projectId,environmentId}=scope;
 const project={organizationId,projectId},environment={...project,environmentId};
 const make=(permission,required,path,target=environment)=>!required?{state:'empty'}:!can(permission,target)?{state:'denied'}:{state:'loading',path};
 const query=new URLSearchParams({organizationId:organizationId||'',projectId:projectId||'',environmentId:environmentId||'',allAccessibleEnvironments:'false'});
 const metrics=new URLSearchParams(query);metrics.set('start',new Date(Date.parse(end)-86400000).toISOString());metrics.set('end',end);metrics.set('groupBy','Api');metrics.set('sortBy','request_count');metrics.set('page','1');metrics.set('pageSize','5');
 const alerts=new URLSearchParams(query);alerts.set('status','Open');alerts.set('page','1');alerts.set('pageSize','5');
 const projectSelected=!!organizationId&&!!projectId,environmentSelected=projectSelected&&!!environmentId;
 return {
  api:make('api.read',projectSelected,`/projects/${encodeURIComponent(projectId)}/apis?page=1&pageSize=1`,project),
  metrics:make('metrics.read',environmentSelected,'/observability/metrics?'+metrics),
  nodes:make('gateway.read',environmentSelected,`/environments/${encodeURIComponent(environmentId)}/gateway-nodes?page=1&pageSize=5`),
  alerts:make('alert.read',environmentSelected,'/observability/alerts?'+alerts),
  releases:make('release.read',environmentSelected,`/environments/${encodeURIComponent(environmentId)}/releases?page=1&pageSize=50`)
 };
}

export async function loadWorkbench(plan,request,signal,publish){
 await Promise.all(Object.entries(plan).filter(([,entry])=>entry.state==='loading').map(async([key,entry])=>{
  try{const data=await request(entry.path,{signal});if(!signal.aborted)publish(key,{state:'ready',data,updatedAt:new Date().toISOString()});}
  catch(error){if(!signal.aborted)publish(key,{state:[401,403,404].includes(error.status)?'denied':'error',error:'读取失败，请刷新重试。',status:error.status});}
 }));
}

export function workbenchMetric(result,name){
 const envelope=result?.state==='ready'?result.data:null;
 if(!envelope||!['Available','Partial'].includes(envelope.sourceState))return null;
 const metric=envelope.data?.kpis?.find(k=>k.metric===name);
 return metric&&['Available','Partial'].includes(metric.state)&&Number.isFinite(metric.value)?metric:null;
}
